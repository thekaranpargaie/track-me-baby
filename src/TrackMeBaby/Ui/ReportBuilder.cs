using System.Text;
using TrackMeBaby.GitHub;
using TrackMeBaby.Services;

namespace TrackMeBaby.Ui;

public record ExportBundle(string Directory, List<string> Files, string Period);

/// <summary>
/// Turns a period into files you can hand to someone: one self-contained HTML page, a Markdown
/// version for pasting into a doc, the charts as standalone SVGs, and the underlying rows as CSV.
///
/// The HTML is deliberately one file with no external reference of any kind — the charts are inline
/// SVG whose marks carry their own colours — so it opens on a machine that has never heard of this
/// project, and prints to a clean PDF from any browser.
/// </summary>
public class ReportBuilder(
    DeliveryService delivery,
    FlowMetricsService flowMetrics,
    PerformanceService performance,
    PeriodResolver periods,
    GitHubAccess github,
    IOptions<TrackerOptions> options,
    IHostEnvironment environment)
{
    private readonly ReportOptions _reports = options.Value.Reports;

    private record Payload(DeliveryReport Delivery, FlowReport Flow, PerformanceReport Performance, string Owner);

    private async Task<Payload> LoadAsync(Period period, CancellationToken ct)
    {
        var owner = _reports.DisplayName;
        if (string.IsNullOrWhiteSpace(owner))
        {
            try { owner = await github.GetLoginAsync(ct); }
            catch (Exception) { owner = "this account"; }
        }

        return new Payload(
            await delivery.BuildAsync(period, ct: ct),
            await flowMetrics.BuildAsync(period, ct: ct),
            await performance.BuildAsync(period, ct: ct),
            owner);
    }

    /// <summary>One HTML file: cover, dashboard body, evidence, and the caveats.</summary>
    public async Task<string> HtmlAsync(Period period, CancellationToken ct = default)
    {
        var data = await LoadAsync(period, ct);
        var html = new StringBuilder();

        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append($"<title>{Theme.Escape(Title(period))}</title>");
        html.Append($"<style>{Theme.Css}</style></head><body><main>");

        html.Append($"""
            <header class="cover">
              <h1>{Theme.Escape(Title(period))}</h1>
              <p class="sub">{Theme.Escape(data.Owner)}{(string.IsNullOrWhiteSpace(_reports.Role)
                  ? "" : " &middot; " + Theme.Escape(_reports.Role))}</p>
              <p class="sub">{Theme.Escape(periods.LocalDate(period.From))} to
                 {Theme.Escape(periods.LocalDate(period.To))} &middot;
                 generated {Theme.Escape(periods.Local(DateTimeOffset.UtcNow))}</p>
            </header>
            """);

        html.Append(Sections.Headline(data.Flow, data.Performance));
        html.Append(Sections.Delivery(data.Delivery));
        html.Append(Sections.Flow(data.Flow));
        html.Append(Sections.Collaboration(data.Flow));
        html.Append(Sections.WorkInProgress(data.Flow));
        html.Append(Sections.Evidence(data.Performance));

        html.Append("<h2>How to read this</h2><div class=\"card\"><ul class=\"caveats\">");
        foreach (var note in data.Delivery.Notes.Concat(data.Flow.Notes).Concat(data.Performance.Notes))
            html.Append($"<li>{Theme.Escape(note)}</li>");
        html.Append("<li>Everything here was collected from GitHub activity on this account and from "
                    + "context recorded by hand. No figure is an estimate.</li>");
        html.Append("</ul></div>");

        html.Append($"</main><script>{Theme.Script}</script></body></html>");
        return html.ToString();
    }

    /// <summary>
    /// Markdown, for pasting into a doc, a ticket or a wiki. Tables carry every number, because
    /// tables are the one thing every Markdown renderer agrees on; charts are referenced as sibling
    /// SVG files, which resolve when the whole bundle is written to disk.
    /// </summary>
    public async Task<string> MarkdownAsync(Period period, bool withChartLinks = false, CancellationToken ct = default)
    {
        var data = await LoadAsync(period, ct);
        var md = new StringBuilder();

        md.AppendLine($"# {Title(period)}");
        md.AppendLine();
        md.AppendLine($"**{data.Owner}**"
                      + (string.IsNullOrWhiteSpace(_reports.Role) ? "" : $" · {_reports.Role}"));
        md.AppendLine();
        md.AppendLine($"{periods.LocalDate(period.From)} to {periods.LocalDate(period.To)} · "
                      + $"generated {periods.Local(DateTimeOffset.UtcNow)}");
        md.AppendLine();

        md.AppendLine("## Summary");
        md.AppendLine();
        md.AppendLine("| Measure | Value | Detail |");
        md.AppendLine("| --- | --- | --- |");
        md.AppendLine($"| Changes shipped | {data.Flow.Merged} | merged pull requests |");
        md.AppendLine($"| Median lead time | {data.Flow.LeadTime.Median} | first commit to merge, "
                      + $"p90 {data.Flow.LeadTime.P90} |");
        md.AppendLine($"| Time in review | {data.Flow.ReviewTime.Median} | opened to merged |");
        md.AppendLine($"| Reviews given | {data.Flow.ReviewsGiven} | for {data.Flow.ReviewPartners.Count} teammates |");
        md.AppendLine($"| Active days | {data.Flow.ActiveDays} of {data.Flow.PeriodDays} | days with recorded activity |");
        md.AppendLine($"| Lines changed | {data.Performance.Metrics.LinesAdded + data.Performance.Metrics.LinesRemoved:N0} "
                      + $"| +{data.Performance.Metrics.LinesAdded:N0} / -{data.Performance.Metrics.LinesRemoved:N0} |");
        md.AppendLine();

        md.AppendLine("## Delivery performance (DORA)");
        md.AppendLine();
        foreach (var env in data.Delivery.Environments)
        {
            md.AppendLine($"### {env.Environment} — {env.TriggerDescription}");
            md.AppendLine();
            md.AppendLine("| Metric | Value | Band | Basis |");
            md.AppendLine("| --- | --- | --- | --- |");
            md.AppendLine($"| Deployment frequency | {env.FrequencyLabel} | {Stats.BandLabel(env.FrequencyBand)} "
                          + $"| {env.Deployments} deployments |");
            md.AppendLine($"| Lead time for changes | "
                          + $"{(env.Trigger == DeployTriggers.Commit ? "n/a" : env.LeadTime.Median)} "
                          + $"| {Stats.BandLabel(env.LeadTimeBand)} | "
                          + $"{(env.Trigger == DeployTriggers.Commit
                              ? "the commit is the deployment"
                              : $"p90 {env.LeadTime.P90} over {env.LeadTime.Count} changes")} |");
            md.AppendLine($"| Change failure rate | {Theme.Percent(env.ChangeFailureRate)} "
                          + $"| {Stats.BandLabel(env.ChangeFailureBand)} "
                          + $"| {env.FailedDeployments} of {env.Deployments} needed repair |");
            md.AppendLine($"| Time to restore | {env.TimeToRestore.Median} | {Stats.BandLabel(env.RestoreBand)} "
                          + $"| {env.TimeToRestore.Count} restores |");
            md.AppendLine();

            if (withChartLinks)
            {
                md.AppendLine($"![Deployments to {env.Environment}](charts/dora-{Slug(env.Environment)}.svg)");
                md.AppendLine();
            }

            if (env.ByWeek.Any(b => b.Count > 0))
            {
                md.AppendLine("| Week of | Deployments |");
                md.AppendLine("| --- | --- |");
                foreach (var bucket in env.ByWeek) md.AppendLine($"| {bucket.Label} | {bucket.Count} |");
                md.AppendLine();
            }

            foreach (var note in env.Notes) md.AppendLine($"> {note}");
            if (env.Notes.Count > 0) md.AppendLine();
        }

        md.AppendLine("## Flow");
        md.AppendLine();
        if (withChartLinks)
        {
            md.AppendLine("![Median lead time by week](charts/lead-time.svg)");
            md.AppendLine();
            md.AppendLine("![Changes merged by week](charts/throughput.svg)");
            md.AppendLine();
        }

        md.AppendLine("| Distribution | Fastest | Median | p75 | p90 | Slowest |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- |");
        AppendDistribution(md, "Lead time", data.Flow.LeadTime);
        AppendDistribution(md, "Time in review", data.Flow.ReviewTime);
        AppendDistribution(md, "Board cycle time", data.Flow.BoardCycleTime);
        md.AppendLine();

        AppendSlices(md, "What the work was", data.Flow.WorkTypeMix, "Changes");
        AppendSlices(md, "Change size", data.Flow.SizeMix, "Changes");
        AppendSlices(md, "Where the work was", data.Flow.RepositoryMix, "Changes");

        if (data.Flow.ReviewPartners.Count > 0)
        {
            md.AppendLine("### Reviews given");
            md.AppendLine();
            md.AppendLine("| Author | Reviews |");
            md.AppendLine("| --- | --- |");
            foreach (var partner in data.Flow.ReviewPartners.Take(20))
                md.AppendLine($"| {partner.Author} | {partner.Reviews} |");
            md.AppendLine();
        }

        if (data.Flow.OpenWork.Count > 0)
        {
            md.AppendLine("## Open work");
            md.AppendLine();
            md.AppendLine("| Age (days) | Kind | Item | Where | Status |");
            md.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var item in data.Flow.OpenWork.Take(25))
                md.AppendLine($"| {item.AgeDays:0} | {item.Kind} | {Link(item.Title, item.Url)} "
                              + $"| {Sections.Shorten(item.Repository)}"
                              + $"{(item.Number is null ? "" : $" #{item.Number}")} | {item.Status} |");
            md.AppendLine();
        }

        md.AppendLine("## Evidence");
        md.AppendLine();
        AppendActivities(md, "Merged pull requests", data.Performance.MergedPullRequests);
        AppendActivities(md, "Issues closed", data.Performance.IssuesClosed);
        AppendActivities(md, "Largest changes", data.Performance.LargestChanges);

        if (data.Performance.Memories.Count > 0)
        {
            md.AppendLine("### Context recorded");
            md.AppendLine();
            foreach (var memory in data.Performance.Memories.OrderByDescending(m => m.Importance))
            {
                md.AppendLine($"**{memory.Title}** — *{memory.Kind}, {memory.OccurredOn}*");
                md.AppendLine();
                md.AppendLine(memory.Content);
                md.AppendLine();
            }
        }

        md.AppendLine("## How to read this");
        md.AppendLine();
        foreach (var note in data.Delivery.Notes.Concat(data.Flow.Notes).Concat(data.Performance.Notes))
            md.AppendLine($"- {note}");

        return md.ToString();
    }

    /// <summary>The rows behind the charts, for a spreadsheet.</summary>
    public async Task<List<(string Name, string Content)>> CsvAsync(Period period, CancellationToken ct = default)
    {
        var data = await LoadAsync(period, ct);
        var files = new List<(string, string)>();

        var dora = new StringBuilder("environment,trigger,deployments,per_week,frequency_band,"
                                     + "lead_time_median_hours,lead_time_p90_hours,lead_time_band,"
                                     + "failed_deployments,change_failure_rate,change_failure_band,"
                                     + "restore_median_hours,restore_band\n");
        foreach (var env in data.Delivery.Environments)
            dora.AppendLine(string.Join(',', new[]
            {
                Csv(env.Environment), Csv(env.Trigger), env.Deployments.ToString(),
                Num(env.PerWeek), Csv(Stats.BandLabel(env.FrequencyBand)),
                Num(env.LeadTime.MedianHours), Num(env.LeadTime.P90Hours), Csv(Stats.BandLabel(env.LeadTimeBand)),
                env.FailedDeployments.ToString(), Num(env.ChangeFailureRate), Csv(Stats.BandLabel(env.ChangeFailureBand)),
                Num(env.TimeToRestore.MedianHours), Csv(Stats.BandLabel(env.RestoreBand))
            }));
        files.Add(("dora-summary.csv", dora.ToString()));

        var deployments = new StringBuilder("environment,date,repository,reference,title,changes,lead_time_hours,failed,restore\n");
        foreach (var env in data.Delivery.Environments)
            foreach (var d in env.Recent)
                deployments.AppendLine(string.Join(',', new[]
                {
                    Csv(env.Environment), Csv(d.Date), Csv(d.Repository), Csv(d.Reference), Csv(d.Title),
                    d.Changes.ToString(), Num(d.LeadTimeHours), d.Failed ? "true" : "false",
                    d.Restore ? "true" : "false"
                }));
        files.Add(("deployments.csv", deployments.ToString()));

        var weekly = new StringBuilder("week_of,changes_merged,lines_changed,median_lead_time_hours,sample\n");
        for (var i = 0; i < data.Flow.ThroughputByWeek.Count; i++)
        {
            var trend = i < data.Flow.LeadTimeByWeek.Count ? data.Flow.LeadTimeByWeek[i] : null;
            weekly.AppendLine(string.Join(',', new[]
            {
                Csv(data.Flow.ThroughputByWeek[i].Start),
                data.Flow.ThroughputByWeek[i].Count.ToString(),
                (i < data.Flow.ChurnByWeek.Count ? data.Flow.ChurnByWeek[i].Count : 0).ToString(),
                Num(trend?.Value), (trend?.Sample ?? 0).ToString()
            }));
        }
        files.Add(("weekly.csv", weekly.ToString()));

        var mix = new StringBuilder("dimension,key,label,count,secondary\n");
        void Mix(string dimension, List<Slice> slices)
        {
            foreach (var slice in slices)
                mix.AppendLine(string.Join(',', new[]
                {
                    Csv(dimension), Csv(slice.Key), Csv(slice.Label),
                    slice.Count.ToString(), (slice.Secondary ?? 0).ToString()
                }));
        }
        Mix("work_type", data.Flow.WorkTypeMix);
        Mix("size", data.Flow.SizeMix);
        Mix("repository", data.Flow.RepositoryMix);
        Mix("board_status_hours", data.Flow.BoardTimeByStatus);
        files.Add(("composition.csv", mix.ToString()));

        var calendar = new StringBuilder("date,events,weekend\n");
        foreach (var day in data.Flow.Calendar)
            calendar.AppendLine($"{day.Date},{day.Count},{(day.Weekend ? "true" : "false")}");
        files.Add(("calendar.csv", calendar.ToString()));

        var wip = new StringBuilder("kind,age_days,title,repository,number,status,url\n");
        foreach (var item in data.Flow.OpenWork)
            wip.AppendLine(string.Join(',', new[]
            {
                Csv(item.Kind), Num(item.AgeDays), Csv(item.Title), Csv(item.Repository),
                item.Number?.ToString() ?? "", Csv(item.Status), Csv(item.Url)
            }));
        files.Add(("open-work.csv", wip.ToString()));

        var evidence = new StringBuilder("type,date,repository,number,title,additions,deletions,url\n");
        foreach (var row in data.Performance.MergedPullRequests
                     .Concat(data.Performance.IssuesClosed)
                     .Concat(data.Performance.ReviewsGiven))
            evidence.AppendLine(string.Join(',', new[]
            {
                Csv(row.Type), Csv(row.Date), Csv(row.Repository), row.Number?.ToString() ?? "",
                Csv(row.Title), (row.Additions ?? 0).ToString(), (row.Deletions ?? 0).ToString(), Csv(row.Url)
            }));
        files.Add(("evidence.csv", evidence.ToString()));

        return files;
    }

    /// <summary>
    /// Standalone SVG files for the headline charts, so a chart can be dropped into a slide or
    /// referenced from the Markdown without the page around it.
    /// </summary>
    public async Task<List<(string Name, string Svg)>> ChartsAsync(Period period, CancellationToken ct = default)
    {
        var data = await LoadAsync(period, ct);
        var charts = new List<(string, string)>();

        foreach (var env in data.Delivery.Environments)
            charts.Add(($"dora-{Slug(env.Environment)}.svg", Standalone(Charts.Columns(
                $"Deployments to {env.Environment}", "per week", env.ByWeek, "deployments", "chart"))));

        charts.Add(("lead-time.svg", Standalone(Charts.Line(
            "Median lead time by week", null, data.Flow.LeadTimeByWeek, "hours", "chart"))));
        charts.Add(("throughput.svg", Standalone(Charts.Columns(
            "Changes merged by week", null, data.Flow.ThroughputByWeek, "changes", "chart"))));
        charts.Add(("calendar.svg", Standalone(Charts.Heatmap(
            "Activity calendar", null, data.Flow.Calendar, "chart"))));

        return charts.Where(c => c.Item2.Length > 0).ToList();
    }

    /// <summary>Writes the whole bundle to disk and returns what it wrote.</summary>
    public async Task<ExportBundle> WriteAsync(
        Period period, string? name = null, CancellationToken ct = default)
    {
        var root = Path.IsPathRooted(_reports.Directory)
            ? _reports.Directory
            : Path.Combine(environment.ContentRootPath, _reports.Directory);

        var folder = Path.Combine(root, Slug(name ?? $"{periods.LocalDate(period.From)}-to-{periods.LocalDate(period.To)}"));
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "charts"));
        Directory.CreateDirectory(Path.Combine(folder, "data"));

        var written = new List<string>();

        var htmlPath = Path.Combine(folder, "report.html");
        await File.WriteAllTextAsync(htmlPath, await HtmlAsync(period, ct), ct);
        written.Add(htmlPath);

        var markdownPath = Path.Combine(folder, "report.md");
        await File.WriteAllTextAsync(markdownPath, await MarkdownAsync(period, withChartLinks: true, ct), ct);
        written.Add(markdownPath);

        foreach (var (chartName, svg) in await ChartsAsync(period, ct))
        {
            var path = Path.Combine(folder, "charts", chartName);
            await File.WriteAllTextAsync(path, svg, ct);
            written.Add(path);
        }

        foreach (var (csvName, content) in await CsvAsync(period, ct))
        {
            var path = Path.Combine(folder, "data", csvName);
            await File.WriteAllTextAsync(path, content, ct);
            written.Add(path);
        }

        return new ExportBundle(folder, written, period.Label);
    }

    /// <summary>
    /// Lifts the SVG out of a figure and makes it a document: an xmlns, a surface-coloured
    /// background, and none of the interaction plumbing.
    ///
    /// The hover attributes have to go, not just because they are dead weight in a file: their
    /// values contain markup for the tooltip, and a .svg is parsed as strict XML, where a raw
    /// angle bracket inside an attribute is a parse error rather than something to shrug at.
    /// </summary>
    private static string Standalone(string figure)
    {
        var start = figure.IndexOf("<svg", StringComparison.Ordinal);
        var end = figure.IndexOf("</svg>", StringComparison.Ordinal);
        if (start < 0 || end < 0) return "";

        var svg = figure[start..(end + 6)];
        svg = Regex.Replace(svg, "<rect class=\"hit\"[^>]*></rect>", "");
        svg = Regex.Replace(svg, " data-tip=\"[^\"]*\"", "");

        var viewBox = Regex.Match(svg, "viewBox=\"0 0 ([0-9.]+) ([0-9.]+)\"");
        var background = viewBox.Success
            ? $"<rect width=\"{viewBox.Groups[1].Value}\" height=\"{viewBox.Groups[2].Value}\" fill=\"#fcfcfb\"></rect>"
            : "";

        // Split at the end of the opening tag so the namespace lands on the tag and the background
        // becomes the first child, behind everything else.
        var openEnd = svg.IndexOf('>');
        if (openEnd < 0) return "";
        var open = svg[..(openEnd + 1)].Replace("<svg ", "<svg xmlns=\"http://www.w3.org/2000/svg\" ");
        return $"{open}{background}{svg[(openEnd + 1)..]}";
    }

    private static void AppendDistribution(StringBuilder md, string label, DurationStats stats) =>
        md.AppendLine($"| {label} | {DurationStats.Format(stats.MinHours)} | {stats.Median} "
                      + $"| {DurationStats.Format(stats.P75Hours)} | {stats.P90} "
                      + $"| {DurationStats.Format(stats.MaxHours)} |");

    private static void AppendSlices(StringBuilder md, string heading, List<Slice> slices, string unit)
    {
        if (slices.Count == 0) return;
        var total = Math.Max(1, slices.Sum(s => s.Count));
        md.AppendLine($"### {heading}");
        md.AppendLine();
        md.AppendLine($"| Category | {unit} | Share |");
        md.AppendLine("| --- | --- | --- |");
        foreach (var slice in slices.OrderByDescending(s => s.Count))
            md.AppendLine($"| {slice.Label} | {slice.Count} | {slice.Count * 100.0 / total:0.#}% |");
        md.AppendLine();
    }

    private static void AppendActivities(StringBuilder md, string heading, List<ActivityDto> rows)
    {
        if (rows.Count == 0) return;
        md.AppendLine($"### {heading}");
        md.AppendLine();
        md.AppendLine("| Date | Repository | What | Size |");
        md.AppendLine("| --- | --- | --- | --- |");
        foreach (var row in rows)
            md.AppendLine($"| {row.Date} | {Sections.Shorten(row.Repository)}"
                          + $"{(row.Number is null ? "" : $" #{row.Number}")} "
                          + $"| {Link(row.Title, row.Url)} "
                          + $"| +{row.Additions ?? 0} / -{row.Deletions ?? 0} |");
        md.AppendLine();
    }

    private static string Title(Period period) => $"Work report — {Capitalise(period.Label)}";

    private static string Link(string? text, string? url)
    {
        var label = (text ?? "").Replace("|", "\\|");
        return string.IsNullOrWhiteSpace(url) ? label : $"[{label}]({url})";
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        var escaped = value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }

    private static string Num(double? value) =>
        value is null ? "" : value.Value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Capitalise(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    internal static string Slug(string value)
    {
        var cleaned = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return cleaned.Length == 0 ? "report" : cleaned;
    }
}
