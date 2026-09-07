using System.ComponentModel;
using ModelContextProtocol.Server;
using TrackMeBaby.Data;
using TrackMeBaby.Services;

namespace TrackMeBaby.Mcp;

/// <summary>
/// Memory tools. GitHub records that a pull request merged; it cannot record that the merge
/// ended a two-day production investigation. That context lives here (plan section 11).
/// </summary>
[McpServerToolType]
public static class MemoryTools
{
    private const string KindHelp =
        "One of: decision (a choice made and why), problem (something broken and how it was fixed), " +
        "learning (something understood), accomplishment (ownership or impact worth remembering), " +
        "goal (what I want to improve), context (background on a project), note (anything else).";

    [McpServerTool(Name = "save_memory")]
    [Description("Record work context that GitHub cannot know: why something mattered, what broke and why, a decision and its reasoning, or an accomplishment. Call this whenever I explain the story behind work, and whenever I say 'remember that...'. Prefer several specific memories over one vague one.")]
    public static async Task<object> SaveMemory(
        MemoryService memories,
        PeriodResolver periods,
        [Description("Short headline, e.g. 'Reporting outage caused by unapplied EF Core migration in QA'.")] string title,
        [Description("The full story in my own words: what happened, what I did, why it mattered, what the impact was.")] string content,
        [Description(KindHelp)] string kind = "note",
        [Description("Free-form tags for later retrieval, e.g. ['reporting','ef-core','production'].")] string[]? tags = null,
        [Description("Date this is about (YYYY-MM-DD), not the date it is being written. Defaults to today.")] string? occurredOn = null,
        [Description("Related repository as owner/repo, if any.")] string? repository = null,
        [Description("Related pull request or issue number, if any.")] int? number = null,
        [Description("Activity ids from a read tool that this memory explains. Links the story to its evidence.")] long[]? relatedActivityIds = null,
        [Description("1 = minor, 3 = normal, 5 = career-defining. Drives what surfaces in appraisal summaries.")] int importance = 3,
        CancellationToken ct = default)
    {
        var occurredAt = string.IsNullOrWhiteSpace(occurredOn)
            ? DateTimeOffset.UtcNow
            : periods.Resolve(null, occurredOn, null).From;

        var saved = await memories.SaveAsync(kind, title, content, tags, occurredAt,
            repository, number, relatedActivityIds, importance, ct);

        return new { saved = true, memory = saved };
    }

    [McpServerTool(Name = "search_memory", ReadOnly = true)]
    [Description("Search my recorded work context by meaning, kind, tag or date. Always call this alongside activity tools when preparing a summary — the numbers describe what happened, the memories describe why it mattered.")]
    public static async Task<object> SearchMemory(
        MemoryService memories,
        PeriodResolver periods,
        [Description("What to look for, in natural language, e.g. 'production reporting failure'. Omit to list recent memories.")] string? query = null,
        [Description(KindHelp)] string? kind = null,
        [Description("Only memories carrying at least one of these tags.")] string[]? tags = null,
        [Description(ReadTools.PeriodHelp)] string? period = null,
        [Description(ReadTools.FromHelp)] string? from = null,
        [Description(ReadTools.ToHelp)] string? to = null,
        [Description("Maximum rows to return (default 25, max 500).")] int limit = 25,
        CancellationToken ct = default)
    {
        Period? resolved = null;
        if (period is not null || from is not null || to is not null)
            resolved = periods.Resolve(period, from, to, "all_time");

        var results = await memories.SearchAsync(query, kind, tags, resolved, limit, ct);
        return new { query, period = resolved?.Label, count = results.Count, memories = results };
    }

    [McpServerTool(Name = "get_memory", ReadOnly = true)]
    [Description("Fetch one memory in full by id.")]
    public static async Task<object> GetMemory(
        MemoryService memories,
        [Description("Memory id from search_memory.")] long id,
        CancellationToken ct = default)
    {
        var memory = await memories.GetAsync(id, ct);
        return memory is null
            ? new { found = false, memory = (MemoryDto?)null, message = (string?)$"No memory with id {id}." }
            : new { found = true, memory = (MemoryDto?)memory, message = (string?)null };
    }

    [McpServerTool(Name = "update_memory")]
    [Description("Amend an existing memory — correct it, add detail, or reclassify it. Only the arguments given are changed.")]
    public static async Task<object> UpdateMemory(
        MemoryService memories,
        PeriodResolver periods,
        [Description("Memory id from search_memory.")] long id,
        [Description("New headline.")] string? title = null,
        [Description("New body. Replaces the old text, so include anything worth keeping.")] string? content = null,
        [Description(KindHelp)] string? kind = null,
        [Description("Replacement tag list.")] string[]? tags = null,
        [Description("Corrected date this is about (YYYY-MM-DD).")] string? occurredOn = null,
        [Description("Related repository as owner/repo.")] string? repository = null,
        [Description("Related pull request or issue number.")] int? number = null,
        [Description("1 to 5.")] int? importance = null,
        CancellationToken ct = default)
    {
        DateTimeOffset? occurredAt = string.IsNullOrWhiteSpace(occurredOn)
            ? null
            : periods.Resolve(null, occurredOn, null).From;

        var updated = await memories.UpdateAsync(id, kind, title, content, tags, occurredAt, repository, number, importance, ct);
        return updated is null
            ? new { updated = false, memory = (MemoryDto?)null, message = (string?)$"No memory with id {id}." }
            : new { updated = true, memory = (MemoryDto?)updated, message = (string?)null };
    }

    [McpServerTool(Name = "delete_memory", Destructive = true)]
    [Description("Delete a memory permanently. Only do this when I explicitly ask; memories are the part of this system that cannot be re-collected from GitHub.")]
    public static async Task<object> DeleteMemory(
        MemoryService memories,
        [Description("Memory id from search_memory.")] long id,
        CancellationToken ct = default)
    {
        var deleted = await memories.DeleteAsync(id, ct);
        return new { deleted, message = deleted ? $"Deleted memory {id}." : $"No memory with id {id}." };
    }

    [McpServerTool(Name = "list_memory_kinds", ReadOnly = true)]
    [Description("The valid memory kinds.")]
    public static object ListMemoryKinds() => new { kinds = MemoryKinds.All };
}
