using System.Text;
using TrackMeBaby.Services;

namespace TrackMeBaby.Ui;

/// <summary>
/// The page body, in pieces. Both the live dashboard and an exported file are built from these, so
/// a report is the same thing you looked at rather than a second implementation of it that drifts.
/// </summary>
public static class Sections
{
    /// <summary>The headline row: one hero figure and the tiles that qualify it.</summary>
    public static string Headline(FlowReport flow, PerformanceReport performance)
    {
        var html = new StringBuilder();
        html.Append("<section class=\"grid hero-row\">");
        html.Append($"""
            <div class="card hero">
              <p class="label">Changes shipped</p>
              <p class="figure">{Theme.Fmt(flow.Merged)}</p>
              <p class="sub">merged in {Theme.Escape(flow.Period)}</p>
            </div>
            <div class="grid four" style="gap:14px">
            """);

        html.Append(Charts.Tile("Median lead time", Theme.Escape(flow.LeadTime.Median),
            $"first commit to merge, over {flow.LeadTime.Count} changes"));
        html.Append(Charts.Tile("Active days", Theme.Fmt(flow.ActiveDays),
            $"of {flow.PeriodDays} in the period",
            spark: Charts.Sparkline(flow.Calendar.Select(d => d.Count).ToList())));
        html.Append(Charts.Tile("Reviews given", Theme.Fmt(flow.ReviewsGiven),
            flow.ReviewPartners.Count == 0
                ? "no review activity recorded"
                : $"for {flow.ReviewPartners.Count} teammate(s)"));
        html.Append(Charts.Tile("Lines changed", Theme.Compact(
                performance.Metrics.LinesAdded + performance.Metrics.LinesRemoved),
            $"{Theme.Compact(performance.Metrics.LinesAdded)} added, "
            + $"{Theme.Compact(performance.Metrics.LinesRemoved)} removed"));

        html.Append("</div></section>");
        return html.ToString();
    }

    /// <summary>
    /// DORA, one block per environment. The four metrics are shown together because they are only
    /// meaningful together — throughput without stability is half a picture, and the pairing is
    /// the whole point of the framework.
    /// </summary>
    public static string Delivery(DeliveryReport report)
    {
        var html = new StringBuilder();
        html.Append("<h2>Delivery performance <span class=\"muted\">&middot; DORA, per environment</span></h2>");
        html.Append("<div class=\"card\">");

        if (report.Environments.Count == 0)
            html.Append("<p class=\"muted\">No environments configured.</p>");

        foreach (var environment in report.Environments)
        {
            html.Append("<div class=\"env\">");
            html.Append($"""
                <div class="env-head">
                  <span class="env-name">{Theme.Escape(environment.Environment)}</span>
                  <span class="muted">{Theme.Escape(environment.TriggerDescription)}</span>
                </div>
                """);

            html.Append("<div class=\"grid four\">");
            html.Append(Metric("Deployment frequency", Theme.Escape(environment.FrequencyLabel),
                $"{Theme.Fmt(environment.Deployments)} deployment(s)", environment.FrequencyBand));
            html.Append(Metric("Lead time for changes",
                environment.Trigger == DeployTriggers.Commit ? "n/a" : Theme.Escape(environment.LeadTime.Median),
                environment.Trigger == DeployTriggers.Commit
                    ? "the commit is the deployment"
                    : $"p90 {Theme.Escape(environment.LeadTime.P90)} over {environment.LeadTime.Count} change(s)",
                environment.LeadTimeBand));
            html.Append(Metric("Change failure rate", Theme.Percent(environment.ChangeFailureRate),
                $"{Theme.Fmt(environment.FailedDeployments)} of {Theme.Fmt(environment.Deployments)} needed repair",
                environment.ChangeFailureBand));
            html.Append(Metric("Time to restore", Theme.Escape(environment.TimeToRestore.Median),
                environment.TimeToRestore.Count == 0
                    ? "no restores in this period"
                    : $"across {environment.TimeToRestore.Count} restore(s)",
                environment.RestoreBand));
            html.Append("</div>");

            html.Append("<div style=\"margin-top:18px\">");
            html.Append(Charts.Columns(
                $"Deployments to {environment.Environment}", "per week",
                environment.ByWeek, "deployments", $"dora-{environment.Environment}"));
            html.Append("</div>");

            if (environment.Recent.Count > 0)
            {
                html.Append("<details class=\"table-view\"><summary>Recent deployments</summary><table>");
                html.Append("<tr><th>When</th><th>Repository</th><th>Reference</th><th>What</th>"
                            + "<th class=\"num\">Lead time</th><th>State</th></tr>");
                foreach (var deployment in environment.Recent)
                {
                    var state = deployment.Failed
                        ? Theme.Pill("Repaired later", "critical", "&#9888;")
                        : deployment.Restore
                            ? Theme.Pill("Restore", "warning", "&#9679;")
                            : "";
                    var reference = deployment.Url is null
                        ? Theme.Escape(deployment.Reference)
                        : $"<a href=\"{Theme.Escape(deployment.Url)}\">{Theme.Escape(deployment.Reference)}</a>";
                    html.Append($"<tr><td class=\"nowrap\">{Theme.Escape(deployment.Date)}</td>"
                                + $"<td>{Theme.Escape(Shorten(deployment.Repository))}</td>"
                                + $"<td class=\"nowrap\">{reference}</td>"
                                + $"<td>{Theme.Escape(Truncate(deployment.Title, 70))}</td>"
                                + $"<td class=\"num nowrap\">{Theme.Escape(DurationStats.Format(deployment.LeadTimeHours))}</td>"
                                + $"<td>{state}</td></tr>");
                }
                html.Append("</table></details>");
            }

            if (environment.Notes.Count > 0)
                html.Append(Caveats(environment.Notes));

            html.Append("</div>");
        }

        html.Append(Caveats(report.Notes));
        html.Append("</div>");
        return html.ToString();
    }

    /// <summary>How work moved: durations, throughput, and what the work was.</summary>
    public static string Flow(FlowReport flow)
    {
        var html = new StringBuilder();
        html.Append("<h2>Flow <span class=\"muted\">&middot; how long things took</span></h2>");

        html.Append("<section class=\"grid two\">");
        html.Append($"<div class=\"card\">{Charts.Line(
            "Median lead time by week", "first commit to merge", flow.LeadTimeByWeek, "hours", "lead-time")}</div>");
        html.Append($"<div class=\"card\">{Charts.Columns(
            "Changes merged by week", null, flow.ThroughputByWeek, "changes", "throughput")}</div>");
        html.Append("</section>");

        html.Append("<section class=\"grid two\" style=\"margin-top:14px\">");
        html.Append($"<div class=\"card\">{Charts.DurationBars(
            "Lead time distribution", "first commit to merge", flow.LeadTime, "changes")}</div>");
        html.Append($"<div class=\"card\">{Charts.DurationBars(
            "Time in review", "opened to merged", flow.ReviewTime, "changes")}</div>");
        html.Append("</section>");

        html.Append("<section class=\"grid two\" style=\"margin-top:14px\">");
        html.Append($"<div class=\"card\">{Charts.Composition(
            "What the work was", "share of merged changes", flow.WorkTypeMix, "changes")}"
            + $"<div style=\"margin-top:22px\">{Charts.Composition(
                "Change size", "lines added plus removed", flow.SizeMix, "changes")}</div></div>");
        html.Append($"<div class=\"card\">{Charts.Bars(
            "Where the work was", "merged changes by repository", flow.RepositoryMix, "changes")}</div>");
        html.Append("</section>");

        if (flow.BoardTimeByStatus.Count > 0)
        {
            html.Append("<section class=\"grid two\" style=\"margin-top:14px\">");
            html.Append($"<div class=\"card\">{Charts.Bars(
                "Time spent per board column", "total hours across your cards", flow.BoardTimeByStatus,
                "hours", slot: 2, secondaryLabel: "times entered")}</div>");
            html.Append($"<div class=\"card\">{Charts.DurationBars(
                "Board cycle time", "first move to done", flow.BoardCycleTime, "cards")}</div>");
            html.Append("</section>");
        }

        return html.ToString();
    }

    /// <summary>Work in progress, oldest first. Age is the signal; a count would hide the pile-up.</summary>
    public static string WorkInProgress(FlowReport flow)
    {
        var html = new StringBuilder();
        html.Append("<h2>Open work <span class=\"muted\">&middot; oldest first</span></h2><div class=\"card\">");

        if (flow.OpenWork.Count == 0)
        {
            html.Append("<p class=\"muted\">Nothing open. Either everything shipped, or nothing is being tracked.</p>");
            html.Append("</div>");
            return html.ToString();
        }

        html.Append("<table><tr><th>Age</th><th>Kind</th><th>Item</th><th>Where</th><th>Status</th></tr>");
        foreach (var item in flow.OpenWork)
        {
            var age = item.AgeDays >= 14
                ? $"<span class=\"bad\">{item.AgeDays:0} days</span>"
                : $"{item.AgeDays:0} days";
            var title = item.Url is null
                ? Theme.Escape(Truncate(item.Title, 80))
                : $"<a href=\"{Theme.Escape(item.Url)}\">{Theme.Escape(Truncate(item.Title, 80))}</a>";
            html.Append($"<tr><td class=\"num nowrap\">{age}</td>"
                        + $"<td class=\"nowrap muted\">{Theme.Escape(item.Kind)}</td>"
                        + $"<td>{title}</td>"
                        + $"<td class=\"nowrap muted\">{Theme.Escape(Shorten(item.Repository))}"
                        + (item.Number is null ? "" : $" #{item.Number}") + "</td>"
                        + $"<td class=\"nowrap\">{Theme.Escape(item.Status)}</td></tr>");
        }
        html.Append("</table>");

        if (flow.OldestOpenDays is > 30)
            html.Append($"<p class=\"tile-foot\">The oldest open item has been sitting for "
                        + $"{flow.OldestOpenDays:0} days. Long-lived work in progress is the usual reason "
                        + "lead time looks worse than the work felt.</p>");

        html.Append("</div>");
        return html.ToString();
    }

    /// <summary>Reviewing and consistency — the two things a commit count never shows.</summary>
    public static string Collaboration(FlowReport flow)
    {
        var html = new StringBuilder();
        html.Append("<h2>Collaboration and consistency</h2>");
        html.Append("<section class=\"grid two\">");

        html.Append("<div class=\"card\">");
        html.Append(Charts.Bars("Reviews given", "by pull request author",
            flow.ReviewPartners.Select(p => new Slice(p.Author, p.Author, p.Reviews)).ToList(),
            "reviews", slot: 1));
        html.Append("</div>");

        html.Append($"<div class=\"card\">{Charts.Heatmap(
            "Activity calendar", "every recorded event, by day", flow.Calendar, "calendar")}");
        if (flow.WeekendDays > 0)
            html.Append($"<p class=\"tile-foot\">{flow.WeekendDays} weekend day(s) had activity.</p>");
        html.Append("</div>");

        html.Append("</section>");
        return html.ToString();
    }

    /// <summary>
    /// The evidence a claim rests on. Numbers first is how a dashboard reads; evidence last is how
    /// a report is checked, and both need the same rows.
    /// </summary>
    public static string Evidence(PerformanceReport performance, int limit = 25)
    {
        var html = new StringBuilder();
        html.Append("<h2>Evidence</h2><div class=\"card\">");

        void Rows(string heading, List<ActivityDto> rows)
        {
            if (rows.Count == 0) return;
            html.Append($"<h3 style=\"margin-top:16px\">{Theme.Escape(heading)}</h3>");
            html.Append("<table><tr><th>Date</th><th>Repository</th><th>What</th><th class=\"num\">Size</th></tr>");
            foreach (var row in rows.Take(limit))
            {
                var what = row.Url is null
                    ? Theme.Escape(Truncate(row.Title, 90))
                    : $"<a href=\"{Theme.Escape(row.Url)}\">{Theme.Escape(Truncate(row.Title, 90))}</a>";
                var size = row.Additions is null && row.Deletions is null
                    ? ""
                    : $"+{Theme.Fmt(row.Additions ?? 0)} / -{Theme.Fmt(row.Deletions ?? 0)}";
                html.Append($"<tr><td class=\"nowrap\">{Theme.Escape(row.Date)}</td>"
                            + $"<td class=\"nowrap muted\">{Theme.Escape(Shorten(row.Repository))}"
                            + (row.Number is null ? "" : $" #{row.Number}") + "</td>"
                            + $"<td>{what}</td><td class=\"num nowrap\">{size}</td></tr>");
            }
            html.Append("</table>");
        }

        Rows("Merged pull requests", performance.MergedPullRequests);
        Rows("Issues closed", performance.IssuesClosed);
        Rows("Largest changes", performance.LargestChanges);

        if (performance.Memories.Count > 0)
        {
            html.Append("<h3 style=\"margin-top:20px\">Context you recorded</h3>");
            foreach (var memory in performance.Memories.OrderByDescending(m => m.Importance).Take(limit))
                html.Append($"<div style=\"padding:10px 0;border-bottom:1px solid var(--grid)\">"
                            + $"<strong>{Theme.Escape(memory.Title)}</strong> "
                            + $"<span class=\"muted\">{Theme.Escape(memory.Kind)} &middot; {Theme.Escape(memory.OccurredOn)}</span>"
                            + $"<p class=\"sub\" style=\"margin-top:4px\">{Theme.Escape(Truncate(memory.Content, 400))}</p></div>");
        }
        else
        {
            html.Append("<p class=\"muted\" style=\"margin-top:16px\">No memories recorded for this period. "
                        + "The numbers above describe volume, not impact — ask Claude to save the context "
                        + "behind the work that mattered.</p>");
        }

        html.Append("</div>");
        return html.ToString();
    }

    private static string Metric(string label, string value, string foot, DoraBand band)
    {
        var (tone, glyph) = Theme.BandTone(band);
        return Charts.Tile(label, value, Theme.Escape(foot),
            badge: Theme.Pill(Stats.BandLabel(band), tone, glyph));
    }

    private static string Caveats(List<string> notes)
    {
        if (notes.Count == 0) return "";
        var html = new StringBuilder("<ul class=\"caveats\">");
        foreach (var note in notes) html.Append($"<li>{Theme.Escape(note)}</li>");
        html.Append("</ul>");
        return html.ToString();
    }

    /// <summary>Owner prefixes repeat on every row and only push the name out of view.</summary>
    internal static string Shorten(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "";
        var slash = fullName.IndexOf('/');
        return slash < 0 ? fullName : fullName[(slash + 1)..];
    }

    internal static string Truncate(string? value, int length) =>
        value is null ? "" : value.Length <= length ? value : value[..(length - 1)] + "…";
}
