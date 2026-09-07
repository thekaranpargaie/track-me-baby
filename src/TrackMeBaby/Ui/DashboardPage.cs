using System.Text;
using TrackMeBaby.Services;

namespace TrackMeBaby.Ui;

/// <summary>
/// The dashboard. The status page answers "is collection working"; this answers "what does the work
/// look like" — and it is deliberately the same body an exported report uses, so the file you send
/// to someone is the page you checked.
/// </summary>
public static class DashboardPage
{
    private static readonly (string Keyword, string Label)[] Ranges =
    [
        ("last_7_days", "7 days"),
        ("last_30_days", "30 days"),
        ("last_90_days", "90 days"),
        ("this_quarter", "This quarter"),
        ("last_6_months", "6 months"),
        ("last_12_months", "12 months")
    ];

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        PeriodResolver periods,
        DeliveryService delivery,
        FlowMetricsService flowMetrics,
        PerformanceService performance,
        IOptions<TrackerOptions> options)
    {
        var keyword = context.Request.Query["period"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(keyword)) keyword = "last_30_days";
        var period = periods.FromKeyword(keyword);
        var ct = context.RequestAborted;

        var deliveryReport = await delivery.BuildAsync(period, ct: ct);
        var flowReport = await flowMetrics.BuildAsync(period, ct: ct);
        var performanceReport = await performance.BuildAsync(period, ct: ct);

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>Dashboard &middot; track-me-baby</title>");
        html.Append($"<style>{Theme.Css}</style></head><body><main>");

        var query = $"period={Uri.EscapeDataString(keyword)}";
        html.Append($"""
            <header class="head">
              <div>
                <h1>Dashboard</h1>
                <p class="sub">{Theme.Escape(period.Label)} &middot;
                   {Theme.Escape(periods.LocalDate(period.From))} to {Theme.Escape(periods.LocalDate(period.To))}</p>
              </div>
              <div class="actions no-print">
                <a class="btn" href="/">Status</a>
                <a class="btn" href="/export/report.html?{query}">Export HTML</a>
                <a class="btn" href="/export/report.md?{query}">Markdown</a>
                <a class="btn" href="/export/data.zip?{query}">CSV data</a>
                <button type="button" onclick="window.print()">Print / PDF</button>
              </div>
            </header>
            """);

        html.Append("<div class=\"tabs no-print\">");
        foreach (var (word, label) in Ranges)
            html.Append($"<a class=\"tab{(word == keyword ? " on" : "")}\" href=\"/dashboard?period={word}\">"
                        + $"{Theme.Escape(label)}</a>");
        html.Append("</div>");

        html.Append(Sections.Headline(flowReport, performanceReport));
        html.Append(Sections.Delivery(deliveryReport));
        html.Append(Sections.Flow(flowReport));
        html.Append(Sections.WorkInProgress(flowReport));
        html.Append(Sections.Collaboration(flowReport));
        html.Append(Sections.Evidence(performanceReport));

        var notes = flowReport.Notes.Concat(performanceReport.Notes).ToList();
        if (notes.Count > 0)
        {
            html.Append("<h2>Notes on this data</h2><div class=\"card\"><ul class=\"caveats\">");
            foreach (var note in notes) html.Append($"<li>{Theme.Escape(note)}</li>");
            html.Append("</ul></div>");
        }

        html.Append($"<p class=\"tile-foot\" style=\"margin-top:26px\">Generated "
                    + $"{Theme.Escape(periods.Local(DateTimeOffset.UtcNow))} from your own collected history. "
                    + $"Time zone {Theme.Escape(string.IsNullOrWhiteSpace(options.Value.TimeZone)
                        ? TimeZoneInfo.Local.Id
                        : options.Value.TimeZone)}.</p>");

        html.Append($"</main><script>{Theme.Script}</script></body></html>");
        return Results.Content(html.ToString(), "text/html");
    }
}
