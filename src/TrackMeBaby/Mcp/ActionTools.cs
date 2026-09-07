using System.ComponentModel;
using ModelContextProtocol.Server;
using TrackMeBaby.Services;

namespace TrackMeBaby.Mcp;

/// <summary>
/// Write tools. These are the only way Claude can change anything, and they are deliberately
/// business-level: no arbitrary GitHub API access and no SQL (plan sections 13 and 24).
/// Everything here is recorded in audit_entries.
/// </summary>
[McpServerToolType]
public static class ActionTools
{
    [McpServerTool(Name = "create_ticket", Destructive = false)]
    [Description("Create a GitHub issue and optionally put it on a board. Use for 'create a ticket for...'. Confirm the repository with me first unless I named one or a default is configured.")]
    public static async Task<object> CreateTicket(
        GitHubActionService actions,
        [Description("Issue title.")] string title,
        [Description("Issue body in markdown. Include the context we discussed so the ticket stands alone.")] string? body = null,
        [Description("Target repository as owner/repo. Falls back to Tracker:GitHub:DefaultRepository.")] string? repository = null,
        [Description("Labels to apply. They must already exist in the repository.")] string[]? labels = null,
        [Description("Assign the issue to me (default true).")] bool assignToMe = true,
        [Description("Board to add it to, as owner/number or the board title. Falls back to Tracker:GitHub:DefaultProject.")] string? project = null,
        [Description("Board column to place it in, e.g. 'Todo'. Requires a board.")] string? status = null,
        CancellationToken ct = default)
    {
        var result = await actions.CreateTicketAsync(title, body, repository, labels, assignToMe, project, status, ct);
        return result;
    }

    [McpServerTool(Name = "update_ticket", Destructive = false)]
    [Description("Change an existing issue's title, body, labels or open/closed state.")]
    public static async Task<object> UpdateTicket(
        GitHubActionService actions,
        [Description("Repository as owner/repo.")] string repository,
        [Description("Issue number.")] int number,
        [Description("New title.")] string? title = null,
        [Description("New body. Replaces the existing body entirely.")] string? body = null,
        [Description("Replace all labels with this list.")] string[]? labels = null,
        [Description("Add these labels, keeping existing ones. Ignored when labels is given.")] string[]? addLabels = null,
        [Description("true closes the issue, false reopens it, omit to leave the state alone.")] bool? closed = null,
        [Description("When closing: 'completed' (default) or 'not_planned'.")] string? closeReason = null,
        CancellationToken ct = default)
    {
        return await actions.UpdateTicketAsync(repository, number, title, body, labels, addLabels, closed, closeReason, ct);
    }

    [McpServerTool(Name = "close_ticket", Destructive = false)]
    [Description("Close an issue. Shorthand for update_ticket with closed = true.")]
    public static async Task<object> CloseTicket(
        GitHubActionService actions,
        [Description("Repository as owner/repo.")] string repository,
        [Description("Issue number.")] int number,
        [Description("'completed' (default) or 'not_planned'.")] string? reason = null,
        [Description("Optional closing comment posted before the issue is closed.")] string? comment = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(comment))
            await actions.AddCommentAsync(repository, number, comment, ct);
        return await actions.UpdateTicketAsync(repository, number, null, null, null, null, true, reason, ct);
    }

    [McpServerTool(Name = "move_ticket", Destructive = false)]
    [Description("Move a board item to a different column, e.g. 'move the migration ticket to In Progress'. Call get_my_projects first if you are unsure of the exact column names.")]
    public static async Task<object> MoveTicket(
        GitHubActionService actions,
        [Description("Target column, e.g. 'In Progress'. Matched case-insensitively against the board's columns.")] string status,
        [Description("Repository as owner/repo, together with number.")] string? repository = null,
        [Description("Issue or pull request number.")] int? number = null,
        [Description("Project item id from get_project_items, when the item has no issue number (a draft card).")] string? itemId = null,
        [Description("Board as owner/number or title. Only needed when the item sits on more than one board.")] string? project = null,
        CancellationToken ct = default)
    {
        return await actions.MoveTicketAsync(repository, number, itemId, status, project, ct);
    }

    [McpServerTool(Name = "add_ticket_comment", Destructive = false)]
    [Description("Post a comment on an issue or pull request.")]
    public static async Task<object> AddTicketComment(
        GitHubActionService actions,
        [Description("Repository as owner/repo.")] string repository,
        [Description("Issue or pull request number.")] int number,
        [Description("Comment body in markdown.")] string body,
        CancellationToken ct = default)
    {
        return await actions.AddCommentAsync(repository, number, body, ct);
    }

    [McpServerTool(Name = "add_ticket_to_project", Destructive = false)]
    [Description("Put an existing issue or pull request onto a board, optionally setting its column.")]
    public static async Task<object> AddTicketToProject(
        GitHubActionService actions,
        [Description("Repository as owner/repo.")] string repository,
        [Description("Issue or pull request number.")] int number,
        [Description("Board as owner/number or title. Falls back to Tracker:GitHub:DefaultProject.")] string? project = null,
        [Description("Column to place it in.")] string? status = null,
        CancellationToken ct = default)
    {
        return await actions.AddTicketToProjectAsync(repository, number, project, status, ct);
    }
}
