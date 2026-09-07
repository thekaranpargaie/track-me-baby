using Microsoft.EntityFrameworkCore;
using Octokit;
using TrackMeBaby.Data;
using TrackMeBaby.GitHub;
using TrackMeBaby.Sync;
using Project = TrackMeBaby.Data.Project;

namespace TrackMeBaby.Services;

public record TicketResult(bool Success, string Message, string? Repository = null, int? Number = null, string? Url = null, string? Status = null);

/// <summary>
/// The write side. Every method here changes something in GitHub, so each one is audited
/// and each one validates its target rather than trusting the arguments (plan section 24).
/// </summary>
public class GitHubActionService(
    TrackerDbContext db,
    GitHubAccess github,
    IOptions<TrackerOptions> options,
    ILogger<GitHubActionService> logger)
{
    private readonly GitHubOptions _options = options.Value.GitHub;

    public async Task<TicketResult> CreateTicketAsync(
        string title, string? body, string? repository, string[]? labels,
        bool assignToMe, string? project, string? status, CancellationToken ct = default)
    {
        var repoName = repository ?? _options.DefaultRepository;
        if (string.IsNullOrWhiteSpace(repoName))
            return await FailAsync("create_ticket", new { title, repository },
                "No repository given and Tracker:GitHub:DefaultRepository is not set.", ct);

        var location = RepoLocation.FromFullName(repoName);
        if (location is null)
            return await FailAsync("create_ticket", new { title, repository = repoName },
                $"'{repoName}' is not a valid owner/repo.", ct);

        var login = await github.GetLoginAsync(ct);
        var newIssue = new NewIssue(title) { Body = body };
        if (labels is not null)
            foreach (var label in labels) newIssue.Labels.Add(label);
        if (assignToMe) newIssue.Assignees.Add(login);

        Issue issue;
        try
        {
            issue = await github.Rest.Issue.Create(location.Owner, location.Name, newIssue);
        }
        catch (ApiException ex)
        {
            return await FailAsync("create_ticket", new { title, repository = repoName },
                $"GitHub rejected the issue: {ex.Message}", ct);
        }

        var messages = new List<string> { $"Created {location.FullName}#{issue.Number}." };
        var projectRef = project ?? _options.DefaultProject;
        string? appliedStatus = null;

        if (!string.IsNullOrWhiteSpace(projectRef))
        {
            var added = await AddToProjectAsync(projectRef, issue.NodeId, ct);
            messages.Add(added.Message);

            if (added.Success && !string.IsNullOrWhiteSpace(status) && added.ItemId is not null)
            {
                var moved = await SetStatusAsync(added.ProjectId!, added.ItemId, status, ct);
                messages.Add(moved.Message);
                if (moved.Success) appliedStatus = status;
            }
        }

        await AuditAsync("create_ticket", new { title, repository = location.FullName, project = projectRef }, true,
            $"#{issue.Number}", ct);

        return new TicketResult(true, string.Join(" ", messages), location.FullName, issue.Number, issue.HtmlUrl, appliedStatus);
    }

    public async Task<TicketResult> UpdateTicketAsync(
        string repository, int number, string? title, string? body,
        string[]? labels, string[]? addLabels, bool? closed, string? stateReason,
        CancellationToken ct = default)
    {
        var location = RepoLocation.FromFullName(repository);
        if (location is null)
            return await FailAsync("update_ticket", new { repository, number }, $"'{repository}' is not a valid owner/repo.", ct);

        Issue current;
        try
        {
            current = await github.Rest.Issue.Get(location.Owner, location.Name, number);
        }
        catch (NotFoundException)
        {
            return await FailAsync("update_ticket", new { repository, number },
                $"{repository}#{number} does not exist or is not visible to the token.", ct);
        }

        var update = current.ToUpdate();
        if (title is not null) update.Title = title;
        if (body is not null) update.Body = body;

        if (labels is not null)
        {
            update.ClearLabels();
            foreach (var label in labels) update.AddLabel(label);
        }
        else if (addLabels is not null)
        {
            foreach (var label in addLabels) update.AddLabel(label);
        }

        if (closed is not null)
        {
            update.State = closed.Value ? ItemState.Closed : ItemState.Open;
            if (closed.Value)
            {
                update.StateReason = string.Equals(stateReason, "not_planned", StringComparison.OrdinalIgnoreCase)
                    ? ItemStateReason.NotPlanned
                    : ItemStateReason.Completed;
            }
        }

        try
        {
            var updated = await github.Rest.Issue.Update(location.Owner, location.Name, number, update);
            await AuditAsync("update_ticket", new { repository, number, title, closed }, true, updated.State.StringValue, ct);
            return new TicketResult(true, $"Updated {repository}#{number}.", repository, number, updated.HtmlUrl, updated.State.StringValue);
        }
        catch (ApiException ex)
        {
            return await FailAsync("update_ticket", new { repository, number }, $"GitHub rejected the update: {ex.Message}", ct);
        }
    }

    public async Task<TicketResult> AddCommentAsync(string repository, int number, string body, CancellationToken ct = default)
    {
        var location = RepoLocation.FromFullName(repository);
        if (location is null)
            return await FailAsync("add_ticket_comment", new { repository, number }, $"'{repository}' is not a valid owner/repo.", ct);

        try
        {
            var comment = await github.Rest.Issue.Comment.Create(location.Owner, location.Name, number, body);
            await AuditAsync("add_ticket_comment", new { repository, number }, true, comment.Id.ToString(), ct);
            return new TicketResult(true, $"Commented on {repository}#{number}.", repository, number, comment.HtmlUrl);
        }
        catch (ApiException ex)
        {
            return await FailAsync("add_ticket_comment", new { repository, number }, $"GitHub rejected the comment: {ex.Message}", ct);
        }
    }

    /// <summary>
    /// Moves a board card to a named column. The item is located by repository+number (or by
    /// project item id) using what the last sync recorded, so no extra GitHub lookup is needed.
    /// </summary>
    public async Task<TicketResult> MoveTicketAsync(
        string? repository, int? number, string? itemId, string status, string? project, CancellationToken ct = default)
    {
        var query = db.ProjectItems.Include(i => i.Project).AsQueryable();

        if (!string.IsNullOrWhiteSpace(itemId))
        {
            query = query.Where(i => i.Id == itemId);
        }
        else if (number is not null)
        {
            query = query.Where(i => i.ContentNumber == number);
            if (!string.IsNullOrWhiteSpace(repository))
                query = query.Where(i => i.RepositoryFullName == repository);
        }
        else
        {
            return await FailAsync("move_ticket", new { repository, number, status },
                "Give either item_id or number (with repository).", ct);
        }

        if (!string.IsNullOrWhiteSpace(project))
        {
            var reference = ParseProjectRef(project);
            if (reference is not null)
                query = query.Where(i => i.Project!.OwnerLogin == reference.Value.Owner
                                         && i.Project!.Number == reference.Value.Number);
        }

        var candidates = await query.ToListAsync(ct);
        if (candidates.Count == 0)
            return await FailAsync("move_ticket", new { repository, number, status },
                "That item is not on any tracked board yet. Run sync_now, or add it with add_ticket_to_project.", ct);
        if (candidates.Count > 1)
            return await FailAsync("move_ticket", new { repository, number, status },
                $"Ambiguous: the item appears on {candidates.Count} boards ({string.Join(", ", candidates.Select(c => c.Project?.Title))}). Pass project as owner/number.", ct);

        var item = candidates[0];
        var result = await SetStatusAsync(item.ProjectId, item.Id, status, ct);
        if (!result.Success)
            return await FailAsync("move_ticket", new { repository, number, status }, result.Message, ct);

        // Reflect the move locally so the next read does not look stale, and record the event.
        item.PreviousStatus = item.Status;
        item.Status = result.AppliedStatus;
        item.StatusChangedAt = DateTimeOffset.UtcNow;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await AuditAsync("move_ticket", new { repository, number, itemId = item.Id, status }, true, result.AppliedStatus, ct);
        return new TicketResult(true,
            $"Moved '{item.Title}' to {result.AppliedStatus} on '{item.Project?.Title}'.",
            item.RepositoryFullName, item.ContentNumber, item.Url, result.AppliedStatus);
    }

    public async Task<TicketResult> AddTicketToProjectAsync(
        string repository, int number, string? project, string? status, CancellationToken ct = default)
    {
        var location = RepoLocation.FromFullName(repository);
        if (location is null)
            return await FailAsync("add_ticket_to_project", new { repository, number }, $"'{repository}' is not a valid owner/repo.", ct);

        var projectRef = project ?? _options.DefaultProject;
        if (string.IsNullOrWhiteSpace(projectRef))
            return await FailAsync("add_ticket_to_project", new { repository, number },
                "No project given and Tracker:GitHub:DefaultProject is not set.", ct);

        Issue issue;
        try
        {
            issue = await github.Rest.Issue.Get(location.Owner, location.Name, number);
        }
        catch (NotFoundException)
        {
            return await FailAsync("add_ticket_to_project", new { repository, number },
                $"{repository}#{number} does not exist or is not visible to the token.", ct);
        }

        var added = await AddToProjectAsync(projectRef, issue.NodeId, ct);
        if (!added.Success)
            return await FailAsync("add_ticket_to_project", new { repository, number, project = projectRef }, added.Message, ct);

        string? appliedStatus = null;
        var message = added.Message;
        if (!string.IsNullOrWhiteSpace(status) && added.ItemId is not null)
        {
            var moved = await SetStatusAsync(added.ProjectId!, added.ItemId, status, ct);
            message += " " + moved.Message;
            if (moved.Success) appliedStatus = moved.AppliedStatus;
        }

        await AuditAsync("add_ticket_to_project", new { repository, number, project = projectRef }, true, added.ItemId, ct);
        return new TicketResult(true, message, repository, number, issue.HtmlUrl, appliedStatus);
    }

    private record AddResult(bool Success, string Message, string? ProjectId = null, string? ItemId = null);

    private async Task<AddResult> AddToProjectAsync(string projectRef, string contentNodeId, CancellationToken ct)
    {
        var project = await ResolveProjectAsync(projectRef, ct);
        if (project is null)
            return new AddResult(false, $"Board '{projectRef}' is not tracked. Run sync_now first, or add it to Tracker:GitHub:Projects.");

        const string mutation = """
            mutation($projectId: ID!, $contentId: ID!) {
              addProjectV2ItemById(input: {projectId: $projectId, contentId: $contentId}) {
                item { id }
              }
            }
            """;

        try
        {
            var data = await github.GraphQlAsync(mutation, new { projectId = project.Id, contentId = contentNodeId }, ct);
            var itemId = data.TryGetProperty("addProjectV2ItemById", out var wrapper)
                         && wrapper.TryGetProperty("item", out var item)
                ? item.GetStringOrNull("id")
                : null;

            return new AddResult(true, $"Added to board '{project.Title}'.", project.Id, itemId);
        }
        catch (GitHubGraphQlException ex)
        {
            return new AddResult(false, $"Could not add to board '{project.Title}': {ex.Message}");
        }
    }

    private record StatusResult(bool Success, string Message, string? AppliedStatus = null);

    private async Task<StatusResult> SetStatusAsync(string projectId, string itemId, string status, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return new StatusResult(false, "Board not found locally; run sync_now.");
        if (project.StatusFieldId is null || project.StatusOptions is null)
            return new StatusResult(false, $"Board '{project.Title}' has no '{_options.StatusFieldName}' single-select field recorded. Run sync_now.");

        Dictionary<string, string> optionsByName;
        try
        {
            optionsByName = JsonSerializer.Deserialize<Dictionary<string, string>>(project.StatusOptions) ?? [];
        }
        catch (JsonException)
        {
            return new StatusResult(false, "Stored board columns are unreadable; run sync_now.");
        }

        var match = optionsByName.Keys.FirstOrDefault(k => k.Equals(status, StringComparison.OrdinalIgnoreCase))
                    ?? optionsByName.Keys.FirstOrDefault(k => k.Contains(status, StringComparison.OrdinalIgnoreCase))
                    ?? optionsByName.Keys.FirstOrDefault(k => status.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return new StatusResult(false,
                $"'{status}' is not a column on '{project.Title}'. Available: {string.Join(", ", optionsByName.Keys)}.");

        const string mutation = """
            mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
              updateProjectV2ItemFieldValue(input: {
                projectId: $projectId, itemId: $itemId, fieldId: $fieldId,
                value: {singleSelectOptionId: $optionId}
              }) {
                projectV2Item { id }
              }
            }
            """;

        try
        {
            await github.GraphQlAsync(mutation, new
            {
                projectId,
                itemId,
                fieldId = project.StatusFieldId,
                optionId = optionsByName[match]
            }, ct);

            return new StatusResult(true, $"Status set to '{match}'.", match);
        }
        catch (GitHubGraphQlException ex)
        {
            return new StatusResult(false, $"Could not set status: {ex.Message}");
        }
    }

    private async Task<Project?> ResolveProjectAsync(string projectRef, CancellationToken ct)
    {
        var reference = ParseProjectRef(projectRef);
        if (reference is not null)
        {
            var byNumber = await db.Projects.FirstOrDefaultAsync(
                p => p.OwnerLogin == reference.Value.Owner && p.Number == reference.Value.Number, ct);
            if (byNumber is not null) return byNumber;
        }

        // Also accept a board title, which is how a person would refer to it in conversation.
        return await db.Projects.FirstOrDefaultAsync(p => EF.Functions.ILike(p.Title, projectRef), ct)
               ?? await db.Projects.FirstOrDefaultAsync(p => EF.Functions.ILike(p.Title, $"%{projectRef}%"), ct);
    }

    private static (string Owner, int Number)? ParseProjectRef(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && int.TryParse(parts[1], out var number) ? (parts[0], number) : null;
    }

    private async Task<TicketResult> FailAsync(string tool, object args, string message, CancellationToken ct)
    {
        logger.LogWarning("{Tool} failed: {Message}", tool, message);
        await AuditAsync(tool, args, false, message, ct);
        return new TicketResult(false, message);
    }

    private async Task AuditAsync(string tool, object args, bool success, string? result, CancellationToken ct)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            Tool = tool,
            Arguments = JsonSerializer.Serialize(args),
            Success = success,
            Result = result,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }
}
