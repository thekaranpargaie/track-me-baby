using System.ComponentModel;
using ModelContextProtocol.Server;
using TrackMeBaby.Services;

namespace TrackMeBaby.Mcp;

/// <summary>
/// Performance tools. These return evidence, never conclusions — the interpretation is
/// Claude's job, and keeping the two apart is what makes the output auditable (FR-11, FR-12).
/// </summary>
[McpServerToolType]
public static class PerformanceTools
{
    [McpServerTool(Name = "get_my_performance", ReadOnly = true)]
    [Description("""
        The full evidence package for a period: metrics, the same metrics for the preceding
        period of equal length, per-repository breakdown, merged pull requests, reviews given,
        closed issues, completed and in-flight board work, largest changes, and my recorded
        memories. This is the tool to use for weekly, monthly and appraisal summaries.

        Write the narrative yourself from what comes back, cite specific pull requests, issues
        and memories as evidence, and lead with impact rather than counts. Do not invent
        anything that is not in the response, and say so plainly if the period looks empty.
        """)]
    public static async Task<object> GetMyPerformance(
        PerformanceService performance,
        PeriodResolver periods,
        [Description(ReadTools.PeriodHelp)] string? period = null,
        [Description(ReadTools.FromHelp)] string? from = null,
        [Description(ReadTools.ToHelp)] string? to = null,
        [Description("How many rows to include per detail list (default 60, max 500). Raise for a six-month review, lower to keep a weekly summary tight.")] int detailLimit = 60,
        CancellationToken ct = default)
    {
        var resolved = periods.Resolve(period, from, to);
        return await performance.BuildAsync(resolved, detailLimit, ct);
    }

    [McpServerTool(Name = "save_performance_snapshot")]
    [Description("Store a summary you have written so it becomes part of the historical record. Do this after producing a weekly or monthly review, so a later six-month appraisal can build on it instead of re-deriving everything. Re-saving the same period and type overwrites the previous snapshot.")]
    public static async Task<object> SavePerformanceSnapshot(
        PerformanceService performance,
        PeriodResolver periods,
        [Description("'weekly', 'monthly', 'quarterly', 'six_month' or 'custom'.")] string periodType,
        [Description("Headline for the snapshot, e.g. 'August 2026 — migration ownership and reporting fix'.")] string title,
        [Description("The summary in markdown. Include the evidence you cited.")] string content,
        [Description(ReadTools.PeriodHelp)] string? period = null,
        [Description(ReadTools.FromHelp)] string? from = null,
        [Description(ReadTools.ToHelp)] string? to = null,
        [Description("Optional JSON metrics block the summary was based on, kept so the numbers can be checked later.")] string? metricsJson = null,
        CancellationToken ct = default)
    {
        var resolved = periods.Resolve(period, from, to, "last_week");
        var snapshot = await performance.SaveSnapshotAsync(periodType, resolved, title, content, metricsJson, ct);
        return new { saved = true, period = resolved.Label, snapshot };
    }

    [McpServerTool(Name = "list_performance_snapshots", ReadOnly = true)]
    [Description("Previously saved summaries, newest first. Read these before writing a long-range review — they carry context that may no longer be obvious from the raw activity.")]
    public static async Task<object> ListPerformanceSnapshots(
        PerformanceService performance,
        [Description("Filter by type: 'weekly', 'monthly', 'quarterly', 'six_month', 'custom'.")] string? periodType = null,
        [Description("Maximum rows to return (default 25, max 500).")] int limit = 25,
        CancellationToken ct = default)
    {
        var snapshots = await performance.ListSnapshotsAsync(periodType, limit, ct);
        return new { count = snapshots.Count, snapshots };
    }
}
