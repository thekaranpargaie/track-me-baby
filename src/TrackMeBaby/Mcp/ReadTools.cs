using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using TrackMeBaby.Data;
using TrackMeBaby.Services;
using TrackMeBaby.Sync;

namespace TrackMeBaby.Mcp;

/// <summary>
/// Read tools. Every one of these answers from PostgreSQL, never from GitHub, so questions
/// about six months ago cost nothing and work offline (plan section 14).
/// </summary>
[McpServerToolType]
public static class ReadTools
{
    internal const string PeriodHelp =
        "Named period: today, yesterday, this_week, last_week, this_month, last_month, " +
        "this_quarter, last_quarter, this_year, last_7_days, last_30_days, last_90_days, " +
        "last_3_months, last_6_months, last_12_months, all_time. Takes precedence over from/to.";

    internal const string FromHelp = "Start date (YYYY-MM-DD or ISO 8601). Ignored when period is set.";
    internal const string ToHelp = "End date (YYYY-MM-DD or ISO 8601). Ignored when period is set.";

    [McpServerTool(Name = "get_my_activity", ReadOnly = true)]
    [Description("Every recorded work event of mine in a period: commits, pull requests, reviews, comments, issues and board moves. Use this when the question is broad or when a more specific tool has no matching filter.")]
    public static async Task<object> GetMyActivity(
        ActivityQueryService activities,
        PeriodResolver periods,
        [Description(PeriodHelp)] string? period = null,
        [Description(FromHelp)] string? from = null,
        [Description(ToHelp)] string? to = null,
        [Description("Restrict to these activity types: commit, pr_opened, pr_merged, pr_closed, pr_review, pr_comment, issue_opened, issue_closed, issue_comment, project_item_added, project_item_moved.")] string[]? types = null,
        [Description("Substring match on repository full name, e.g. 'billing' or 'my-org/api'.")] string? repository = null,
        [Description("Maximum rows to return (default 100, max 500).")] int limit = 100,
        CancellationToken ct = default)
    {
        var resolved = periods.Resolve(period, from, to);
        var rows = await activities.ListAsync(resolved, types, repository, limit, ct);
        return new { period = resolved.Label, from = resolved.From.ToString("u"), to = resolved.To.ToString("u"), count = rows.Count, activities = rows };
    }

    [McpServerTool(Name = "get_my_commits", ReadOnly = true)]
    [Description("Commits I authored in a period.")]
    public static async Task<object> GetMyCommits(
        ActivityQueryService activities,
        PeriodResolver periods,
        [Description(PeriodHelp)] string? period = null,
        [Description(FromHelp)] string? from = null,
        [Description(ToHelp)] string? to = null,
        [Description("Substring match on repository full name.")] string? repository = null,
        [Description("Maximum rows to return (default 100, max 500).")] int limit = 100,
        CancellationToken ct = default)
    {
        var resolved = periods.Resolve(period, from, to);
        var rows = await activities.ListAsync(resolved, [ActivityTypes.Commit], repository, limit, ct);
        return new { period = resolved.Label, count = rows.Count, commits = rows };
    }

    [McpServerTool(Name = "get_my_pull_requests", ReadOnly = true)]
    [Description("Pull requests I opened, merged or closed in a period, with size (lines added/removed) and labels.")]
    public static async Task<object> GetMyPullRequests(
        ActivityQueryService activities,
        PeriodResolver periods,
        [Description(PeriodHelp)] string? period = null,
        [Description(FromHelp)] string? from = null,
        [Description(ToHelp)] string? to = null,
        [Description("'merged', 'opened', 'closed', or 'all' (default).")] string state = "all",
        [Description("Substring match on repository full name.")] string? repository = null,
        [Description("Maximum rows to return (default 100, max 500).")] int limit = 100,
        CancellationToken ct = default)
    {
        var types = state.ToLowerInvariant() switch
        {
            "merged" => new[] { ActivityTypes.PrMerged },
            "opened" or "open" => [ActivityTypes.PrOpened],
            "closed" => [ActivityTypes.PrClosed],
            _ => [ActivityTypes.PrOpened, ActivityTypes.PrMerged, ActivityTypes.PrClosed]
        };

        var resolved = periods.Resolve(period, from, to);
        var rows = await activities.ListAsync(resolved, types, repository, limit, ct);
        return new { period = resolved.Label, state, count = rows.Count, pullRequests = rows };
    }

    [McpServerTool(Name = "get_my_reviews", ReadOnly = true)]
    [Description("Code reviews I gave on other people's pull requests. This is the evidence for collaboration and mentoring, which raw commit counts miss.")]
    public static async Task<object> GetMyReviews(
        ActivityQueryService activities,
        PeriodResolver periods,
        [Description(PeriodHelp)] string? period = null,
        [Description(FromHelp)] string? from = null,
        [Description(ToHelp)] string? to = null,
        [Description("Substring match on repository full name.")] string? repository = null,
        [Description("Maximum rows to return (default 100, max 500).")] int limit = 100,
        CancellationToken ct = default)
    {
        var resolved = periods.Resolve(period, from, to);
        var rows = await activities.ListAsync(resolved, [ActivityTypes.PrReview], repository, limit, ct);
        return new { period = resolved.Label, count = rows.Count, reviews = rows };
    }

    [McpServerTool(Name = "get_my_issues", ReadOnly = true)]
    [Description("Issues I opened or that were closed while assigned to me.")]
    public static async Task<object> GetMyIssues(
        ActivityQueryService activities,
        PeriodResolver periods,
        [Description(PeriodHelp)] string? period = null,
        [Description(FromHelp)] string? from = null,
        [Description(ToHelp)] string? to = null,
        [Description("'opened', 'closed', or 'all' (default).")] string state = "all",
        [Description("Maximum rows to return (default 100, max 500).")] int limit = 100,
        CancellationToken ct = default)
    {
        var types = state.ToLowerInvariant() switch
        {
            "opened" or "open" => new[] { ActivityTypes.IssueOpened },
            "closed" => [ActivityTypes.IssueClosed],
            _ => [ActivityTypes.IssueOpened, ActivityTypes.IssueClosed]
        };

        var resolved = periods.Resolve(period, from, to);
        var rows = await activities.ListAsync(resolved, types, null, limit, ct);
        return new { period = resolved.Label, state, count = rows.Count, issues = rows };
    }

    [McpServerTool(Name = "get_my_completed_work", ReadOnly = true)]
    [Description("Work I finished in a period: merged pull requests, closed issues, and board items that reached a done column. Use this for 'what did I accomplish' questions.")]
    public static async Task<object> GetMyCompletedWork(
        ActivityQueryService activities,
        PeriodResolver periods,
        [Description(PeriodHelp)] string? period = null,
        [Description(FromHelp)] string? from = null,
        [Description(ToHelp)] string? to = null,
        [Description("Maximum rows per category (default 100, max 500).")] int limit = 100,
        CancellationToken ct = default)
    {
        var resolved = periods.Resolve(period, from, to);
        var merged = await activities.ListAsync(resolved, [ActivityTypes.PrMerged], null, limit, ct);
        var closed = await activities.ListAsync(resolved, [ActivityTypes.IssueClosed], null, limit, ct);
        var moves = await activities.ListAsync(resolved, [ActivityTypes.ProjectItemMoved], null, limit, ct);

        return new
        {
            period = resolved.Label,
            mergedPullRequests = merged,
            closedIssues = closed,
            boardMoves = moves,
            totals = new { merged = merged.Count, closedIssues = closed.Count, boardMoves = moves.Count }
        };
    }

    [McpServerTool(Name = "get_my_open_work", ReadOnly = true)]
    [Description("What is on my plate right now: board items not in a done column, plus pull requests I opened that are still open.")]
    public static async Task<object> GetMyOpenWork(
        ActivityQueryService activities,
        TrackerDbContext db,
        PeriodResolver periods,
        [Description("Maximum rows to return (default 100, max 500).")] int limit = 100,
        CancellationToken ct = default)
    {
        var items = await activities.ListProjectItemsAsync(true, null, false, limit, ct);
        var openItems = items.Where(i => !IsDone(i.Status)).ToList();

        var openPrs = await db.Activities.AsNoTracking()
            .Where(a => a.IsMine && a.ActivityType == ActivityTypes.PrOpened && a.State == "open")
            .OrderByDescending(a => a.OccurredAt)
            .Take(ActivityQueryService.Clamp(limit))
            .ToListAsync(ct);

        return new
        {
            boardItems = openItems,
            openPullRequests = openPrs.Select(activities.ToDto).ToList(),
            note = "Board status reflects the last sync. Run sync_now if something was just moved."
        };

        static bool IsDone(string? status) =>
            status is not null && (status.Contains("done", StringComparison.OrdinalIgnoreCase)
                                   || status.Contains("complete", StringComparison.OrdinalIgnoreCase)
                                   || status.Contains("closed", StringComparison.OrdinalIgnoreCase));
    }

    [McpServerTool(Name = "get_my_projects", ReadOnly = true)]
    [Description("The GitHub Projects boards being tracked, their columns, and how many of my items are on each. Call this before move_ticket to learn the exact column names.")]
    public static async Task<object> GetMyProjects(
        ActivityQueryService activities,
        CancellationToken ct = default)
    {
        var projects = await activities.ListProjectsAsync(ct);
        return new { count = projects.Count, projects };
    }

    [McpServerTool(Name = "get_project_items", ReadOnly = true)]
    [Description("Board items, optionally filtered to one column. Use get_my_projects first to see valid column names.")]
    public static async Task<object> GetProjectItems(
        ActivityQueryService activities,
        [Description("Column name to filter by, e.g. 'In Progress'. Omit for all columns.")] string? status = null,
        [Description("Include items assigned to other people (default false).")] bool includeOthers = false,
        [Description("Include archived items (default false).")] bool includeArchived = false,
        [Description("Maximum rows to return (default 200, max 500).")] int limit = 200,
        CancellationToken ct = default)
    {
        var items = await activities.ListProjectItemsAsync(!includeOthers, status, includeArchived, limit, ct);
        return new { count = items.Count, items };
    }

    [McpServerTool(Name = "get_repositories", ReadOnly = true)]
    [Description("Repositories I was active in during a period, ranked by how much activity each saw. Useful for showing project breadth.")]
    public static async Task<object> GetRepositories(
        ActivityQueryService activities,
        PeriodResolver periods,
        [Description(PeriodHelp)] string? period = null,
        [Description(FromHelp)] string? from = null,
        [Description(ToHelp)] string? to = null,
        CancellationToken ct = default)
    {
        var resolved = periods.Resolve(period, from, to, "last_6_months");
        var stats = await activities.RepositoryStatsAsync(resolved, ct);
        return new { period = resolved.Label, count = stats.Count, repositories = stats };
    }

    [McpServerTool(Name = "get_sync_status", ReadOnly = true)]
    [Description("Health of the GitHub collector: last successful sync, last attempt, any error, and how much history is stored. Check this first if a report looks empty or out of date.")]
    public static async Task<object> GetSyncStatus(
        TrackerDbContext db,
        SyncCoordinator coordinator,
        PeriodResolver periods,
        IOptions<TrackerOptions> options,
        CancellationToken ct = default)
    {
        var state = await db.SyncStates.AsNoTracking().FirstOrDefaultAsync(s => s.Source == GitHubSyncService.SourceName, ct);
        var total = await db.Activities.CountAsync(ct);
        var oldest = await db.Activities.OrderBy(a => a.OccurredAt).Select(a => (DateTimeOffset?)a.OccurredAt).FirstOrDefaultAsync(ct);
        var newest = await db.Activities.OrderByDescending(a => a.OccurredAt).Select(a => (DateTimeOffset?)a.OccurredAt).FirstOrDefaultAsync(ct);
        var interval = options.Value.Sync.IntervalMinutes;

        return new SyncStatusDto(
            GitHubSyncService.SourceName,
            state?.Status ?? SyncStatuses.Never,
            state?.LastSuccessfulSync is { } ls ? periods.Local(ls) : null,
            state?.LastAttemptedSync is { } la ? periods.Local(la) : null,
            state?.Error,
            state?.ConsecutiveFailures ?? 0,
            state?.LastItemsProcessed ?? 0,
            state?.LastDurationMs ?? 0,
            coordinator.IsRunning,
            state?.LastSuccessfulSync is { } last && options.Value.Sync.Enabled
                ? periods.Local(last.AddMinutes(interval))
                : null,
            total,
            oldest is null ? null : periods.LocalDate(oldest.Value),
            newest is null ? null : periods.Local(newest.Value));
    }

    [McpServerTool(Name = "sync_now", Destructive = false)]
    [Description("Force an immediate GitHub sync instead of waiting for the hourly schedule. Use after doing work you want reflected right away. Takes seconds to a few minutes.")]
    public static async Task<object> SyncNow(
        SyncCoordinator coordinator,
        PeriodResolver periods,
        [Description("Re-sync everything changed since this date (YYYY-MM-DD) instead of since the last successful sync. Use to backfill.")] string? since = null,
        [Description("Run even if another sync appears to be in progress.")] bool force = false,
        CancellationToken ct = default)
    {
        DateTimeOffset? sinceUtc = null;
        if (!string.IsNullOrWhiteSpace(since))
            sinceUtc = periods.Resolve(null, since, null).From;

        var result = await coordinator.RunAsync(sinceUtc, force, ct);
        return new
        {
            success = result.Success,
            recordsProcessed = result.ItemsProcessed,
            window = $"{periods.Local(result.From)} to {periods.Local(result.To)}",
            durationMs = result.DurationMs,
            error = result.Error,
            notes = result.Notes
        };
    }
}
