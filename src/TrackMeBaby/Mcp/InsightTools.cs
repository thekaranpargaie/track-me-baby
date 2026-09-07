using System.ComponentModel;
using ModelContextProtocol.Server;
using TrackMeBaby.Data;
using TrackMeBaby.Services;
using TrackMeBaby.Ui;

namespace TrackMeBaby.Mcp;

/// <summary>
/// Delivery and flow measurement, plus the export that turns any of it into files.
///
/// Same rule as the rest of the read surface: these return measurements, never verdicts. The DORA
/// bands come back as labels precisely so a summary has to say what band and why, rather than
/// implying a score.
/// </summary>
[McpServerToolType]
public static class InsightTools
{
    [McpServerTool(Name = "get_dora_metrics", ReadOnly = true)]
    [Description("""
        The four DORA metrics — deployment frequency, lead time for changes, change failure rate
        and time to restore — computed per environment from my own activity.

        Each environment defines a deployment differently (a commit reaching dev is not a tag
        reaching stage), so figures are never blended across environments: quote them per
        environment and name the trigger. The bands are published team benchmarks applied to one
        person's slice of the work, so present them as context, not as a grade, and pass on the
        caveats in the notes rather than dropping them.
        """)]
    public static async Task<object> GetDoraMetrics(
        DeliveryService delivery,
        PeriodResolver periods,
        [Description(ReadTools.PeriodHelp)] string? period = null,
        [Description(ReadTools.FromHelp)] string? from = null,
        [Description(ReadTools.ToHelp)] string? to = null,
        [Description("How many recent deployments to list per environment (default 15).")] int recentLimit = 15,
        CancellationToken ct = default)
    {
        var resolved = periods.Resolve(period, from, to, "last_30_days");
        return await delivery.BuildAsync(resolved, recentLimit, ct);
    }

    [McpServerTool(Name = "get_flow_metrics", ReadOnly = true)]
    [Description("""
        How work actually moved in a period: lead time and time-in-review distributions, weekly
        throughput, board cycle time and time per column, the mix of feature/fix/maintenance work,
        change sizes, review load by teammate, open work with its age, and the activity calendar.

        Use this whenever a question is about pace, consistency, review contribution or work in
        progress — get_my_performance counts events, this measures durations. Quote medians with
        their sample size, and prefer the p90 over the mean when describing the worst case.
        """)]
    public static async Task<object> GetFlowMetrics(
        FlowMetricsService flow,
        PeriodResolver periods,
        [Description(ReadTools.PeriodHelp)] string? period = null,
        [Description(ReadTools.FromHelp)] string? from = null,
        [Description(ReadTools.ToHelp)] string? to = null,
        [Description("How many open-work rows to include (default 40, max 500).")] int limit = 40,
        CancellationToken ct = default)
    {
        var resolved = periods.Resolve(period, from, to, "last_30_days");
        return await flow.BuildAsync(resolved, limit, ct);
    }

    [McpServerTool(Name = "export_report")]
    [Description("""
        Write a shareable report for a period to disk: one self-contained HTML file (opens
        anywhere, prints to a clean PDF), a Markdown version for pasting into a document, the
        charts as standalone SVGs, and every underlying row as CSV.

        Use this when I ask for something to send, show, print or attach. Tell me the folder it
        landed in and what is in it. The dashboard at /dashboard shows the same content live, and
        /export/report.html serves the same file over HTTP if I would rather download it.
        """)]
    public static async Task<object> ExportReport(
        ReportBuilder reports,
        PeriodResolver periods,
        TrackerDbContext db,
        [Description(ReadTools.PeriodHelp)] string? period = null,
        [Description(ReadTools.FromHelp)] string? from = null,
        [Description(ReadTools.ToHelp)] string? to = null,
        [Description("Folder name for this export, e.g. 'august-2026-review'. Defaults to the date range.")] string? name = null,
        CancellationToken ct = default)
    {
        var resolved = periods.Resolve(period, from, to, "last_30_days");
        var now = DateTimeOffset.UtcNow;

        try
        {
            var bundle = await reports.WriteAsync(resolved, name, ct);

            db.AuditEntries.Add(new AuditEntry
            {
                Tool = "export_report",
                Arguments = JsonSerializer.Serialize(new { period = resolved.Label, name }),
                Success = true,
                Result = bundle.Directory,
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);

            return new
            {
                written = true,
                period = resolved.Label,
                folder = bundle.Directory,
                files = bundle.Files.Select(Path.GetFileName).ToList(),
                open = Path.Combine(bundle.Directory, "report.html"),
                hint = "The HTML file is self-contained; open it in a browser and print to PDF if a PDF is wanted."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            db.AuditEntries.Add(new AuditEntry
            {
                Tool = "export_report",
                Arguments = JsonSerializer.Serialize(new { period = resolved.Label, name }),
                Success = false,
                Result = ex.Message,
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);

            return new
            {
                written = false,
                error = ex.Message,
                hint = "Check Tracker:Reports:Directory is writable. In Docker it must be a mounted volume."
            };
        }
    }
}
