using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octokit;
using TrackMeBaby.Data;
using TrackMeBaby.GitHub;
using Activity = TrackMeBaby.Data.Activity;

namespace TrackMeBaby.Sync;

public record SyncResult(
    bool Success,
    int ItemsProcessed,
    DateTimeOffset From,
    DateTimeOffset To,
    long DurationMs,
    string? Error,
    List<string> Notes);

/// <summary>
/// Collects GitHub activity into the activities table.
///
/// Rather than walking every repository, this drives off the GitHub search API with
/// author:/reviewed-by:/commenter:/assignee: qualifiers. One set of queries covers every
/// repository the token can see, public or private, so incremental sync stays cheap and
/// no repository configuration is required.
/// </summary>
public class GitHubSyncService(
    TrackerDbContext db,
    GitHubAccess github,
    ProjectsSyncService projectsSync,
    TagSyncService tagSync,
    IOptions<TrackerOptions> options,
    ILogger<GitHubSyncService> logger)
{
    public const string SourceName = "github";

    private readonly GitHubOptions _options = options.Value.GitHub;

    public async Task<SyncResult> SyncAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var notes = new List<string>();

        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == SourceName, ct);
        if (state is null)
        {
            state = new SyncState { Source = SourceName };
            db.SyncStates.Add(state);
        }

        if (!github.HasToken)
        {
            const string message = "No GitHub token configured. Set Tracker:GitHub:Token or the GITHUB_TOKEN environment variable.";
            state.Status = SyncStatuses.Failed;
            state.Error = message;
            state.LastAttemptedSync = startedAt;
            state.UpdatedAt = startedAt;
            await db.SaveChangesAsync(ct);
            return new SyncResult(false, 0, startedAt, startedAt, 0, message, notes);
        }

        var to = startedAt;
        var from = since
            ?? (state.LastSuccessfulSync?.AddMinutes(-_options.OverlapMinutes)
                ?? startedAt.AddDays(-_options.InitialBackfillDays));

        if (from > to) from = to.AddMinutes(-_options.OverlapMinutes);

        state.Status = SyncStatuses.Running;
        state.LastAttemptedSync = startedAt;
        state.UpdatedAt = startedAt;
        await db.SaveChangesAsync(ct);

        var processed = 0;
        try
        {
            var login = await github.GetLoginAsync(ct);
            logger.LogInformation("GitHub sync starting for {Login}: {From:u} -> {To:u}", login, from, to);

            // Checkpoint after each window. A long backfill that dies two thirds of the way
            // through then resumes from the last completed window instead of starting over,
            // and the bookmark still never moves past data we have not processed (FR-05).
            foreach (var window in SliceWindow(from, to, _options.MaxWindowDays))
            {
                processed += await SyncWindowAsync(login, window.From, window.To, notes, ct);
                // Inside the loop so the checkpoint covers it too, and so its own search
                // stays within the same small window.
                processed += await SyncDirectCommitsAsync(login, window.From, window.To, notes, ct);

                state.LastSuccessfulSync = window.To;
                state.LastItemsProcessed = processed;
                state.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Window {From:yyyy-MM-dd} -> {To:yyyy-MM-dd} done ({Processed} records so far)",
                    window.From, window.To, processed);
            }

            if (_options.SyncProjects)
            {
                try
                {
                    processed += await projectsSync.SyncAsync(login, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Boards are a bonus on top of the activity history. A missing read:project
                    // scope, an unexpected GraphQL shape, anything at all here — none of it should
                    // discard a completed activity sync, so this swallows more than GraphQL errors
                    // on purpose and surfaces the reason in the status page instead.
                    notes.Add($"Projects sync skipped: {ex.Message}");
                    logger.LogWarning(ex, "Projects V2 sync failed; continuing without it");
                }
            }

            try
            {
                processed += await tagSync.SyncAsync(notes, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same reasoning as boards: tags only feed the DORA view. Losing them must not
                // discard an activity sync that succeeded.
                notes.Add($"Tag sync skipped: {ex.Message}");
                logger.LogWarning(ex, "Tag sync failed; continuing without it");
            }

            var duration = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

            // FR-04/FR-05: the bookmark only moves once everything above succeeded.
            state.LastSuccessfulSync = to;
            state.Status = SyncStatuses.Success;
            state.Error = notes.Count > 0 ? string.Join(" | ", notes) : null;
            state.ConsecutiveFailures = 0;
            state.LastItemsProcessed = processed;
            state.LastDurationMs = duration;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("GitHub sync finished: {Processed} records in {Duration}ms", processed, duration);
            return new SyncResult(true, processed, from, to, duration, null, notes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var duration = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            logger.LogError(ex, "GitHub sync failed; LastSuccessfulSync stays at {Last:u}", state.LastSuccessfulSync);

            // Reload state: the failure may have come from a partially-tracked SaveChanges.
            db.ChangeTracker.Clear();
            var fresh = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == SourceName, ct)
                        ?? new SyncState { Source = SourceName };
            if (db.Entry(fresh).State == EntityState.Detached) db.SyncStates.Add(fresh);

            fresh.Status = SyncStatuses.Failed;
            fresh.Error = ex.Message;
            fresh.ConsecutiveFailures += 1;
            fresh.LastAttemptedSync = startedAt;
            fresh.LastDurationMs = duration;
            fresh.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return new SyncResult(false, processed, from, to, duration, ex.Message, notes);
        }
    }

    /// <summary>
    /// Search caps any single query at 1000 results, so long windows (the initial backfill)
    /// are split into slices small enough that no slice can overflow.
    /// </summary>
    internal static IEnumerable<(DateTimeOffset From, DateTimeOffset To)> SliceWindow(
        DateTimeOffset from, DateTimeOffset to, int maxDays)
    {
        if (maxDays < 1) maxDays = 1;
        var cursor = from;
        while (cursor < to)
        {
            var next = cursor.AddDays(maxDays);
            if (next > to) next = to;
            yield return (cursor, next);
            cursor = next;
        }
        if (from >= to) yield return (from, to);
    }

    private async Task<int> SyncWindowAsync(
        string login, DateTimeOffset from, DateTimeOffset to, List<string> notes, CancellationToken ct)
    {
        var range = $"{from:yyyy-MM-ddTHH:mm:ssZ}..{to:yyyy-MM-ddTHH:mm:ssZ}";
        var scope = BuildScopeQualifiers();

        // Each qualifier surfaces a different kind of involvement; a PR found by more than
        // one is fetched once thanks to the seen-set below.
        string[] queries =
        [
            $"type:pr author:{login} updated:{range}{scope}",
            $"type:pr reviewed-by:{login} updated:{range}{scope}",
            $"type:pr commenter:{login} updated:{range}{scope}",
            $"type:pr assignee:{login} updated:{range}{scope}",
            $"type:issue author:{login} updated:{range}{scope}",
            $"type:issue assignee:{login} updated:{range}{scope}",
            $"type:issue commenter:{login} updated:{range}{scope}"
        ];

        var seenPulls = new HashSet<string>();
        var seenIssues = new HashSet<string>();
        var processed = 0;
        var skipped = 0;

        foreach (var query in queries)
        {
            var isPullQuery = query.StartsWith("type:pr", StringComparison.Ordinal);
            foreach (var issue in await SearchAllAsync(query, notes, ct))
            {
                var location = RepoLocation.FromUrl(issue.HtmlUrl);
                if (location is null || !IsRepositoryAllowed(location.FullName)) continue;

                var key = $"{location.FullName}#{issue.Number}";
                var isPull = isPullQuery || issue.PullRequest is not null;

                if (!(isPull ? seenPulls : seenIssues).Add(key)) continue;

                // Search already tells us when the item last changed. If what we stored is at
                // least that fresh, skip it and save the detail requests entirely — this is what
                // makes the hourly sync cheap and a resumed backfill fast.
                if (await IsUpToDateAsync(location.FullName, issue.Number, issue.UpdatedAt, ct))
                {
                    skipped++;
                    continue;
                }

                processed += isPull
                    ? await SyncPullRequestAsync(login, location, issue.Number, notes, ct)
                    : await SyncIssueAsync(login, location, issue, ct);
            }
        }

        if (skipped > 0)
            logger.LogDebug("Skipped {Skipped} items already up to date in this window", skipped);

        return processed;
    }

    /// <summary>
    /// True when we already hold this item at least as fresh as the source says it is. Keyed on
    /// repository and number rather than id, because a search hit for a pull request carries the
    /// issue id, not the pull request id.
    /// </summary>
    private Task<bool> IsUpToDateAsync(
        string repositoryFullName, int number, DateTimeOffset? sourceUpdatedAt, CancellationToken ct)
    {
        // No timestamp from the source means we cannot prove it is unchanged, so re-fetch.
        if (sourceUpdatedAt is null) return Task.FromResult(false);

        var updatedAt = sourceUpdatedAt.Value.ToUniversalTime();
        return db.Activities.AnyAsync(
            a => a.RepositoryFullName == repositoryFullName
                 && a.Number == number
                 && a.SourceUpdatedAt != null
                 && a.SourceUpdatedAt >= updatedAt, ct);
    }

    private string BuildScopeQualifiers()
    {
        if (_options.IncludeRepositories.Count > 0)
            return " " + string.Join(" ", _options.IncludeRepositories.Select(r => $"repo:{r}"));
        if (_options.Organizations.Count > 0)
            return " " + string.Join(" ", _options.Organizations.Select(o => $"org:{o}"));
        return "";
    }

    private bool IsRepositoryAllowed(string fullName)
    {
        if (_options.ExcludeRepositories.Any(r => r.Equals(fullName, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (_options.IncludeRepositories.Count > 0)
            return _options.IncludeRepositories.Any(r => r.Equals(fullName, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private async Task<List<Issue>> SearchAllAsync(string query, List<string> notes, CancellationToken ct)
    {
        var results = new List<Issue>();
        for (var page = 1; page <= 10; page++)
        {
            ct.ThrowIfCancellationRequested();
            var request = new SearchIssuesRequest(query)
            {
                SortField = IssueSearchSort.Updated,
                Order = SortDirection.Ascending,
                PerPage = 100,
                Page = page
            };

            SearchIssuesResult result;
            try
            {
                // Throttled and retried: search is the endpoint GitHub polices most aggressively.
                result = await github.SearchAsync(() => github.Rest.Search.SearchIssues(request), ct);
            }
            catch (ApiValidationException ex)
            {
                notes.Add($"Search rejected ('{query}'): {ex.Message}");
                return results;
            }

            results.AddRange(result.Items);
            if (result.Items.Count < 100) break;
            if (page == 10 && result.TotalCount > 1000)
                notes.Add($"Search hit the 1000-result cap for '{query}'; reduce Tracker:GitHub:MaxWindowDays for the backfill.");
        }

        return results;
    }

    private async Task<int> SyncPullRequestAsync(
        string login, RepoLocation location, int number, List<string> notes, CancellationToken ct)
    {
        PullRequest pr;
        try
        {
            pr = await github.WithRetryAsync(() => github.Rest.PullRequest.Get(location.Owner, location.Name, number), ct);
        }
        catch (NotFoundException)
        {
            return 0; // deleted, transferred, or no longer visible to the token
        }

        var repoId = await UpsertRepositoryAsync(pr.Base?.Repository, location, ct);
        var isMine = Same(pr.User?.Login, login);
        var count = 0;

        if (isMine)
        {
            var labels = pr.Labels?.Select(l => l.Name).ToArray() ?? [];

            count += await UpsertActivityAsync(new Activity
            {
                SourceId = $"pr:{pr.Id}",
                ActivityType = ActivityTypes.PrOpened,
                RepositoryId = repoId,
                RepositoryFullName = location.FullName,
                Actor = login,
                IsMine = true,
                OccurredAt = pr.CreatedAt,
                SourceUpdatedAt = pr.UpdatedAt,
                Title = pr.Title,
                Summary = Excerpt(pr.Body),
                Url = pr.HtmlUrl,
                Number = pr.Number,
                State = pr.Merged ? "merged" : pr.State.StringValue,
                Additions = pr.Additions,
                Deletions = pr.Deletions,
                ChangedFiles = pr.ChangedFiles,
                Labels = labels,
                Milestone = pr.Milestone?.Title,
                Payload = Json(new
                {
                    pr.Id, pr.Number, pr.Title, pr.State, pr.Merged, pr.Draft,
                    pr.Additions, pr.Deletions, pr.ChangedFiles, pr.Commits,
                    pr.CreatedAt, pr.UpdatedAt, pr.MergedAt, pr.ClosedAt,
                    Author = pr.User?.Login, Repository = location.FullName,
                    BaseRef = pr.Base?.Ref, HeadRef = pr.Head?.Ref,
                    Labels = labels, Milestone = pr.Milestone?.Title, pr.HtmlUrl
                })
            }, ct);

            if (pr is { Merged: true, MergedAt: not null })
            {
                count += await UpsertActivityAsync(new Activity
                {
                    SourceId = $"pr:{pr.Id}",
                    ActivityType = ActivityTypes.PrMerged,
                    RepositoryId = repoId,
                    RepositoryFullName = location.FullName,
                    Actor = login,
                    IsMine = true,
                    OccurredAt = pr.MergedAt.Value,
                    SourceUpdatedAt = pr.UpdatedAt,
                    Title = pr.Title,
                    Summary = Excerpt(pr.Body),
                    Url = pr.HtmlUrl,
                    Number = pr.Number,
                    State = "merged",
                    Additions = pr.Additions,
                    Deletions = pr.Deletions,
                    ChangedFiles = pr.ChangedFiles,
                    Labels = labels,
                    Milestone = pr.Milestone?.Title,
                    Payload = Json(new
                    {
                        pr.Id, pr.Number, pr.Title, pr.MergedAt,
                        MergedBy = pr.MergedBy?.Login, pr.Additions, pr.Deletions,
                        pr.ChangedFiles, pr.Commits, Repository = location.FullName, pr.HtmlUrl
                    })
                }, ct);
            }
            else if (pr is { Merged: false, ClosedAt: not null })
            {
                count += await UpsertActivityAsync(new Activity
                {
                    SourceId = $"pr:{pr.Id}",
                    ActivityType = ActivityTypes.PrClosed,
                    RepositoryId = repoId,
                    RepositoryFullName = location.FullName,
                    Actor = login,
                    IsMine = true,
                    OccurredAt = pr.ClosedAt.Value,
                    SourceUpdatedAt = pr.UpdatedAt,
                    Title = pr.Title,
                    Url = pr.HtmlUrl,
                    Number = pr.Number,
                    State = "closed",
                    Labels = labels,
                    Payload = Json(new { pr.Id, pr.Number, pr.Title, pr.ClosedAt, Repository = location.FullName, pr.HtmlUrl })
                }, ct);
            }

            count += await SyncPullRequestCommitsAsync(login, location, pr, repoId, ct);
        }

        count += await SyncReviewsAsync(login, location, pr, repoId, ct);

        if (_options.SyncComments && pr.Comments > 0)
            count += await SyncCommentsAsync(login, location, pr.Number, pr.Title, repoId, ActivityTypes.PrComment, ct);

        return count;
    }

    private async Task<int> SyncPullRequestCommitsAsync(
        string login, RepoLocation location, PullRequest pr, long? repoId, CancellationToken ct)
    {
        IReadOnlyList<PullRequestCommit> commits;
        try
        {
            commits = await github.WithRetryAsync(() => github.Rest.PullRequest.Commits(location.Owner, location.Name, pr.Number), ct);
        }
        catch (ApiException)
        {
            return 0;
        }

        var count = 0;
        foreach (var commit in commits)
        {
            var authorLogin = commit.Author?.Login;
            var authorName = commit.Commit?.Author?.Name;
            // Commits made through the web UI or with a different git identity have no linked
            // user, so fall back to matching the PR author.
            var mine = Same(authorLogin, login) || (authorLogin is null && Same(pr.User?.Login, login));
            if (!mine) continue;

            var message = commit.Commit?.Message ?? "";
            var when = commit.Commit?.Author?.Date ?? commit.Commit?.Committer?.Date ?? pr.CreatedAt;

            count += await UpsertActivityAsync(new Activity
            {
                SourceId = $"commit:{commit.Sha}",
                ActivityType = ActivityTypes.Commit,
                RepositoryId = repoId,
                RepositoryFullName = location.FullName,
                Actor = authorLogin ?? login,
                IsMine = true,
                OccurredAt = when,
                Title = FirstLine(message),
                Summary = Excerpt(message),
                Url = commit.HtmlUrl,
                Number = pr.Number,
                Payload = Json(new
                {
                    commit.Sha, Message = message, AuthorLogin = authorLogin, AuthorName = authorName,
                    Date = when, Repository = location.FullName, PullRequest = pr.Number, commit.HtmlUrl
                })
            }, ct);
        }

        return count;
    }

    private async Task<int> SyncReviewsAsync(
        string login, RepoLocation location, PullRequest pr, long? repoId, CancellationToken ct)
    {
        IReadOnlyList<PullRequestReview> reviews;
        try
        {
            reviews = await github.WithRetryAsync(() => github.Rest.PullRequest.Review.GetAll(location.Owner, location.Name, pr.Number), ct);
        }
        catch (ApiException)
        {
            return 0;
        }

        var count = 0;
        foreach (var review in reviews.Where(r => Same(r.User?.Login, login)))
        {
            count += await UpsertActivityAsync(new Activity
            {
                SourceId = $"review:{review.Id}",
                ActivityType = ActivityTypes.PrReview,
                RepositoryId = repoId,
                RepositoryFullName = location.FullName,
                Actor = login,
                IsMine = true,
                OccurredAt = review.SubmittedAt,
                Title = $"Reviewed #{pr.Number}: {pr.Title}",
                Summary = Excerpt(review.Body),
                Url = review.HtmlUrl,
                Number = pr.Number,
                State = review.State.StringValue,
                Payload = Json(new
                {
                    review.Id, State = review.State.StringValue, review.Body, review.SubmittedAt,
                    PullRequest = pr.Number, PullRequestTitle = pr.Title,
                    PullRequestAuthor = pr.User?.Login, Repository = location.FullName, review.HtmlUrl
                })
            }, ct);
        }

        return count;
    }

    private async Task<int> SyncIssueAsync(string login, RepoLocation location, Issue searchResult, CancellationToken ct)
    {
        // The search result already carries title, body, state, labels, assignees, author and all
        // the timestamps, so there is nothing worth a second request for. Skipping the per-issue
        // GET is what keeps a repository with hundreds of issues from taking hours to sync.
        var issue = searchResult;

        var repoId = await UpsertRepositoryAsync(issue.Repository, location, ct);
        var isMine = Same(issue.User?.Login, login);
        var isAssigned = issue.Assignees?.Any(a => Same(a.Login, login)) == true;
        var labels = issue.Labels?.Select(l => l.Name).ToArray() ?? [];
        var count = 0;

        if (isMine || isAssigned)
        {
            var payload = Json(new
            {
                issue.Id, issue.Number, issue.Title, State = issue.State.StringValue,
                issue.CreatedAt, issue.UpdatedAt, issue.ClosedAt,
                Author = issue.User?.Login,
                Assignees = issue.Assignees?.Select(a => a.Login).ToArray() ?? [],
                Labels = labels, Milestone = issue.Milestone?.Title,
                Repository = location.FullName, issue.HtmlUrl
            });

            if (isMine)
            {
                count += await UpsertActivityAsync(new Activity
                {
                    SourceId = $"issue:{issue.Id}",
                    ActivityType = ActivityTypes.IssueOpened,
                    RepositoryId = repoId,
                    RepositoryFullName = location.FullName,
                    Actor = login,
                    IsMine = true,
                    OccurredAt = issue.CreatedAt,
                    SourceUpdatedAt = issue.UpdatedAt,
                    Title = issue.Title,
                    Summary = Excerpt(issue.Body),
                    Url = issue.HtmlUrl,
                    Number = issue.Number,
                    State = issue.State.StringValue,
                    Labels = labels,
                    Milestone = issue.Milestone?.Title,
                    Payload = payload
                }, ct);
            }

            if (issue.ClosedAt is not null)
            {
                count += await UpsertActivityAsync(new Activity
                {
                    SourceId = $"issue:{issue.Id}",
                    ActivityType = ActivityTypes.IssueClosed,
                    RepositoryId = repoId,
                    RepositoryFullName = location.FullName,
                    Actor = issue.ClosedBy?.Login ?? login,
                    // Counts as mine when I opened it or was assigned to it.
                    IsMine = true,
                    OccurredAt = issue.ClosedAt.Value,
                    SourceUpdatedAt = issue.UpdatedAt,
                    Title = issue.Title,
                    Summary = Excerpt(issue.Body),
                    Url = issue.HtmlUrl,
                    Number = issue.Number,
                    State = "closed",
                    Labels = labels,
                    Milestone = issue.Milestone?.Title,
                    Payload = payload
                }, ct);
            }
        }

        if (_options.SyncComments && issue.Comments > 0)
            count += await SyncCommentsAsync(login, location, issue.Number, issue.Title, repoId, ActivityTypes.IssueComment, ct);

        return count;
    }

    private async Task<int> SyncCommentsAsync(
        string login, RepoLocation location, int number, string? title,
        long? repoId, string activityType, CancellationToken ct)
    {
        IReadOnlyList<IssueComment> comments;
        try
        {
            comments = await github.WithRetryAsync(() => github.Rest.Issue.Comment.GetAllForIssue(location.Owner, location.Name, number), ct);
        }
        catch (ApiException)
        {
            return 0;
        }

        var count = 0;
        foreach (var comment in comments.Where(c => Same(c.User?.Login, login)))
        {
            count += await UpsertActivityAsync(new Activity
            {
                SourceId = $"comment:{comment.Id}",
                ActivityType = activityType,
                RepositoryId = repoId,
                RepositoryFullName = location.FullName,
                Actor = login,
                IsMine = true,
                OccurredAt = comment.CreatedAt,
                Title = $"Commented on #{number}: {title}",
                Summary = Excerpt(comment.Body),
                Url = comment.HtmlUrl,
                Number = number,
                Payload = Json(new
                {
                    comment.Id, comment.Body, comment.CreatedAt, comment.UpdatedAt,
                    Target = number, TargetTitle = title, Repository = location.FullName, comment.HtmlUrl
                })
            }, ct);
        }

        return count;
    }

    /// <summary>
    /// Commit search catches work pushed straight to a branch with no pull request.
    /// Best effort: this endpoint is occasionally unavailable for a given token, and PR
    /// commits already cover the common case.
    /// </summary>
    private async Task<int> SyncDirectCommitsAsync(
        string login, DateTimeOffset from, DateTimeOffset to, List<string> notes, CancellationToken ct)
    {
        var scope = BuildScopeQualifiers();
        var query = $"author:{login} author-date:{from:yyyy-MM-ddTHH:mm:ssZ}..{to:yyyy-MM-ddTHH:mm:ssZ}{scope}";
        var count = 0;

        for (var page = 1; page <= 10; page++)
        {
            var url = $"search/commits?q={Uri.EscapeDataString(query)}&per_page=100&page={page}&sort=author-date&order=asc";
            var response = await github.SearchAsync(() => github.RestGetAsync(url, ct), ct);
            if (response is null)
            {
                notes.Add("Commit search unavailable; commits are still captured through pull requests.");
                return count;
            }

            if (!response.Value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return count;

            var pageCount = 0;
            foreach (var item in items.EnumerateArray())
            {
                pageCount++;
                var sha = item.GetStringOrNull("sha");
                if (sha is null) continue;

                var repoFullName = item.TryGetProperty("repository", out var repo)
                    ? repo.GetStringOrNull("full_name")
                    : null;
                if (repoFullName is null || !IsRepositoryAllowed(repoFullName)) continue;

                var location = RepoLocation.FromFullName(repoFullName);
                if (location is null) continue;

                var commitNode = item.TryGetProperty("commit", out var c) ? c : default;
                var message = commitNode.ValueKind == JsonValueKind.Object ? commitNode.GetStringOrNull("message") ?? "" : "";
                var when = (commitNode.ValueKind == JsonValueKind.Object
                            && commitNode.TryGetProperty("author", out var ca)
                        ? ca.GetDateOrNull("date")
                        : null) ?? from;

                var repoId = await EnsureRepositoryAsync(repoFullName, repo, ct);

                count += await UpsertActivityAsync(new Activity
                {
                    SourceId = $"commit:{sha}",
                    ActivityType = ActivityTypes.Commit,
                    RepositoryId = repoId,
                    RepositoryFullName = location.FullName,
                    Actor = login,
                    IsMine = true,
                    OccurredAt = when,
                    Title = FirstLine(message),
                    Summary = Excerpt(message),
                    Url = item.GetStringOrNull("html_url"),
                    Payload = Json(new { Sha = sha, Message = message, Date = when, Repository = repoFullName })
                }, ct);
            }

            if (pageCount < 100) break;
        }

        return count;
    }

    /// <summary>
    /// Upserts on (Source, SourceId, ActivityType). Facts already stored are refreshed rather
    /// than duplicated, which is what makes the overlap window and retries safe (FR-06).
    /// </summary>
    private async Task<int> UpsertActivityAsync(Activity incoming, CancellationToken ct)
    {
        incoming.Source = SourceName;
        var now = DateTimeOffset.UtcNow;

        var existing = await db.Activities.FirstOrDefaultAsync(
            a => a.Source == incoming.Source
                 && a.SourceId == incoming.SourceId
                 && a.ActivityType == incoming.ActivityType, ct);

        if (existing is null)
        {
            incoming.CreatedAt = now;
            incoming.UpdatedAt = now;
            db.Activities.Add(incoming);
            await db.SaveChangesAsync(ct);
            return 1;
        }

        existing.RepositoryId = incoming.RepositoryId ?? existing.RepositoryId;
        existing.RepositoryFullName = incoming.RepositoryFullName ?? existing.RepositoryFullName;
        existing.Actor = incoming.Actor;
        existing.IsMine = incoming.IsMine;
        existing.OccurredAt = incoming.OccurredAt;
        existing.Title = incoming.Title;
        existing.Summary = incoming.Summary;
        existing.Url = incoming.Url;
        existing.Number = incoming.Number;
        existing.State = incoming.State;
        existing.Additions = incoming.Additions;
        existing.Deletions = incoming.Deletions;
        existing.ChangedFiles = incoming.ChangedFiles;
        existing.Labels = incoming.Labels;
        existing.Milestone = incoming.Milestone;
        existing.Payload = incoming.Payload ?? existing.Payload;
        existing.SourceUpdatedAt = incoming.SourceUpdatedAt ?? existing.SourceUpdatedAt;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return 1;
    }

    private async Task<long?> UpsertRepositoryAsync(
        Octokit.Repository? repo, RepoLocation location, CancellationToken ct)
    {
        if (repo is null) return await LookupRepositoryIdAsync(location.FullName, ct);

        var now = DateTimeOffset.UtcNow;
        var existing = await db.Repositories.FindAsync([repo.Id], ct);
        if (existing is null)
        {
            db.Repositories.Add(new Data.Repository
            {
                Id = repo.Id,
                FullName = repo.FullName,
                Owner = repo.Owner?.Login ?? location.Owner,
                Name = repo.Name,
                IsPrivate = repo.Private,
                Description = repo.Description,
                Language = repo.Language,
                HtmlUrl = repo.HtmlUrl,
                FirstSeenAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.FullName = repo.FullName;
            existing.Description = repo.Description;
            existing.Language = repo.Language;
            existing.IsPrivate = repo.Private;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return repo.Id;
    }

    private async Task<long?> EnsureRepositoryAsync(string fullName, JsonElement repoNode, CancellationToken ct)
    {
        var existingId = await LookupRepositoryIdAsync(fullName, ct);
        if (existingId is not null) return existingId;

        if (repoNode.ValueKind != JsonValueKind.Object
            || !repoNode.TryGetProperty("id", out var idNode)
            || !idNode.TryGetInt64(out var id))
            return null;

        var location = RepoLocation.FromFullName(fullName);
        var now = DateTimeOffset.UtcNow;
        db.Repositories.Add(new Data.Repository
        {
            Id = id,
            FullName = fullName,
            Owner = location?.Owner ?? "",
            Name = location?.Name ?? fullName,
            IsPrivate = repoNode.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True,
            Description = repoNode.GetStringOrNull("description"),
            HtmlUrl = repoNode.GetStringOrNull("html_url"),
            FirstSeenAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    private Task<long?> LookupRepositoryIdAsync(string fullName, CancellationToken ct) =>
        db.Repositories.Where(r => r.FullName == fullName).Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static string? Excerpt(string? text, int max = 2000)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "...";
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}

/// <summary>An "owner/repo" pair, usually recovered from an html_url.</summary>
public record RepoLocation(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    public static RepoLocation? FromUrl(string? htmlUrl)
    {
        if (string.IsNullOrWhiteSpace(htmlUrl)) return null;
        if (!Uri.TryCreate(htmlUrl, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 2 ? new RepoLocation(segments[0], segments[1]) : null;
    }

    public static RepoLocation? FromFullName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        var parts = fullName.Split('/');
        return parts.Length == 2 ? new RepoLocation(parts[0], parts[1]) : null;
    }
}

internal static class JsonElementExtensions
{
    /// <summary>
    /// Reads a timestamp property, treating absent, null and unparseable values as null.
    /// Necessary because <see cref="JsonElement.TryGetDateTimeOffset"/> throws on a JSON null
    /// instead of returning false, and GitHub returns null for things like closedAt.
    /// </summary>
    public static DateTimeOffset? GetDateOrNull(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : null;

    public static string? GetStringOrNull(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
