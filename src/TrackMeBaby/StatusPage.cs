using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TrackMeBaby.Data;
using TrackMeBaby.GitHub;
using TrackMeBaby.Services;
using TrackMeBaby.Sync;

namespace TrackMeBaby;

/// <summary>
/// A single page showing whether collection is actually working (plan section 23). Everything here
/// answers one question: is the collector healthy, and does what it holds look right? What the work
/// itself looks like is <see cref="Ui.DashboardPage"/>, which is a separate page on purpose — a
/// health check that doubles as a report tells you neither thing clearly.
/// </summary>
public static class StatusPage
{
    public static async Task<object> BuildAsync(TrackerDbContext db, SyncCoordinator coordinator, PeriodResolver periods)
    {
        var state = await db.SyncStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Source == GitHubSyncService.SourceName);

        var byType = await db.Activities.AsNoTracking()
            .GroupBy(a => a.ActivityType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync();

        var repositories = await db.Repositories.CountAsync();
        var projects = await db.Projects.CountAsync();
        var projectItems = await db.ProjectItems.CountAsync();
        var memories = await db.Memories.CountAsync();
        var snapshots = await db.PerformanceSnapshots.CountAsync();
        var writes = await db.AuditEntries.CountAsync();

        var oldest = await db.Activities.OrderBy(a => a.OccurredAt).Select(a => (DateTimeOffset?)a.OccurredAt).FirstOrDefaultAsync();
        var newest = await db.Activities.OrderByDescending(a => a.OccurredAt).Select(a => (DateTimeOffset?)a.OccurredAt).FirstOrDefaultAsync();

        return new
        {
            source = GitHubSyncService.SourceName,
            status = state?.Status ?? SyncStatuses.Never,
            running = coordinator.IsRunning,
            lastSuccessfulSync = state?.LastSuccessfulSync is { } ls ? periods.Local(ls) : null,
            lastAttemptedSync = state?.LastAttemptedSync is { } la ? periods.Local(la) : null,
            error = state?.Error,
            consecutiveFailures = state?.ConsecutiveFailures ?? 0,
            lastRecordsProcessed = state?.LastItemsProcessed ?? 0,
            lastDurationMs = state?.LastDurationMs ?? 0,
            counts = new
            {
                activities = byType.Sum(x => x.Count),
                repositories,
                projects,
                projectItems,
                memories,
                snapshots,
                writeOperations = writes
            },
            activityByType = byType.ToDictionary(x => x.Type, x => x.Count),
            historyFrom = oldest is null ? null : periods.LocalDate(oldest.Value),
            historyTo = newest is null ? null : periods.Local(newest.Value)
        };
    }

    public static async Task<IResult> RenderAsync(
        TrackerDbContext db,
        SyncCoordinator coordinator,
        PeriodResolver periods,
        GitHubAccess github,
        IOptions<TrackerOptions> options)
    {
        var state = await db.SyncStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Source == GitHubSyncService.SourceName);

        var byType = await db.Activities.AsNoTracking()
            .GroupBy(a => a.ActivityType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync();

        var byRepository = await db.Activities.AsNoTracking()
            .Where(a => a.RepositoryFullName != null)
            .GroupBy(a => a.RepositoryFullName!)
            .Select(g => new { Repository = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(8)
            .ToListAsync();

        // Fifteen days back, because the local-time buckets straddle the UTC boundary.
        var since = DateTimeOffset.UtcNow.AddDays(-15);
        var recentStamps = await db.Activities.AsNoTracking()
            .Where(a => a.OccurredAt >= since)
            .Select(a => a.OccurredAt)
            .ToListAsync();
        var perDay = recentStamps
            .GroupBy(stamp => periods.ToLocalTime(stamp).Date)
            .ToDictionary(g => g.Key, g => g.Count());
        var today = periods.NowLocal().Date;
        var days = Enumerable.Range(0, 14).Select(offset => today.AddDays(offset - 13)).ToList();

        var projects = await db.Projects.AsNoTracking().OrderByDescending(p => p.LastSyncedAt).ToListAsync();
        var itemsPerBoard = await db.ProjectItems.AsNoTracking()
            .GroupBy(i => i.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count);

        var recentWrites = await db.AuditEntries.AsNoTracking()
            .OrderByDescending(a => a.CreatedAt).Take(10).ToListAsync();

        var repositoryCount = await db.Repositories.CountAsync();
        var memoryCount = await db.Memories.CountAsync();
        var snapshotCount = await db.PerformanceSnapshots.CountAsync();
        var writeCount = await db.AuditEntries.CountAsync();
        var itemCount = itemsPerBoard.Values.Sum();
        var activityCount = byType.Sum(x => x.Count);

        var oldest = await db.Activities.OrderBy(a => a.OccurredAt).Select(a => (DateTimeOffset?)a.OccurredAt).FirstOrDefaultAsync();
        var newest = await db.Activities.OrderByDescending(a => a.OccurredAt).Select(a => (DateTimeOffset?)a.OccurredAt).FirstOrDefaultAsync();

        string? login = null;
        string rateLimit = "unknown";
        string? tokenProblem = null;
        if (github.HasToken)
        {
            try
            {
                login = await github.GetLoginAsync();
                var (remaining, limit, reset) = await github.GetRateLimitAsync();
                rateLimit = $"{remaining} of {limit} remaining, resets {periods.Local(reset)}";
            }
            catch (Exception ex)
            {
                tokenProblem = ex.Message;
            }
        }
        else
        {
            tokenProblem = "No token configured. Set Tracker:GitHub:Token in appsettings.Local.json or the GITHUB_TOKEN environment variable.";
        }

        // Status never travels as colour alone: every pill carries a glyph and a word.
        var (badge, glyph, tone) = (state?.Status ?? SyncStatuses.Never) switch
        {
            SyncStatuses.Success when state?.ConsecutiveFailures == 0 => ("Healthy", "&#10003;", "good"),
            SyncStatuses.Success => ("Recovered", "&#10003;", "warning"),
            SyncStatuses.Running => ("Syncing", "&#9679;", "warning"),
            SyncStatuses.Failed => ("Failed", "&#9888;", "critical"),
            _ => ("Never run", "&#8211;", "idle")
        };

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>track-me-baby</title>");
        if (coordinator.IsRunning) html.Append("<meta http-equiv=\"refresh\" content=\"6\">");
        html.Append($"<style>{Css}</style></head><body><main>");

        // ---- header ------------------------------------------------------------------
        html.Append($"""
            <header class="head">
              <div>
                <h1>track-me-baby <span class="pill {tone}"><span aria-hidden="true">{glyph}</span> {badge}</span></h1>
                <p class="sub">GitHub collector for <strong>{Escape(login ?? "unknown account")}</strong>
                   &middot; MCP endpoint <code>/mcp</code>
                   &middot; {(options.Value.Sync.Enabled ? $"syncing every {options.Value.Sync.IntervalMinutes} min" : "schedule disabled")}</p>
              </div>
              <div class="actions">
                <a class="btn" href="/dashboard">Dashboard</a>
                <form method="post" action="/sync"><button type="submit">Sync now</button></form>
              </div>
            </header>
            """);

        if (tokenProblem is not null)
            html.Append($"<div class=\"note critical\"><strong>Token problem.</strong> {Escape(tokenProblem)}</div>");
        if (!string.IsNullOrWhiteSpace(state?.Error))
            html.Append($"<div class=\"note critical\"><strong>Last sync reported.</strong> {Escape(state.Error)}</div>");

        // ---- hero + tiles ------------------------------------------------------------
        var span = oldest is null || newest is null
            ? "nothing collected yet"
            : $"{periods.LocalDate(oldest.Value)} &rarr; {periods.LocalDate(newest.Value)}";

        html.Append($"""
            <section class="grid hero-row">
              <div class="card hero">
                <p class="label">Activity events collected</p>
                <p class="figure">{Fmt(activityCount)}</p>
                <p class="sub">{span}</p>
              </div>
              <div class="tiles">
            """);
        Tile(html, "Repositories", repositoryCount);
        Tile(html, "Boards", projects.Count);
        Tile(html, "Board items", itemCount);
        Tile(html, "Memories", memoryCount);
        Tile(html, "Snapshots", snapshotCount);
        Tile(html, "Write operations", writeCount);
        html.Append("</div></section>");

        // ---- sync health -------------------------------------------------------------
        html.Append("<h2>Sync health</h2><div class=\"card\"><dl class=\"facts\">");
        Fact(html, "Last successful", state?.LastSuccessfulSync is { } ls2 ? periods.Local(ls2) : "never");
        Fact(html, "Last attempt", state?.LastAttemptedSync is { } la2 ? periods.Local(la2) : "never");
        Fact(html, "Last run", $"{Fmt(state?.LastItemsProcessed ?? 0)} records in {Fmt(state?.LastDurationMs ?? 0)} ms");
        Fact(html, "Consecutive failures", (state?.ConsecutiveFailures ?? 0) == 0
            ? "<span class=\"ok\">none</span>"
            : $"<span class=\"bad\">{state?.ConsecutiveFailures}</span>");
        Fact(html, "API rate limit", Escape(rateLimit));
        html.Append("</dl></div>");

        // ---- collection over time ----------------------------------------------------
        var dayPeak = days.Count == 0 ? 0 : days.Max(d => perDay.GetValueOrDefault(d));
        html.Append("<h2>Events collected per day <span class=\"muted\">&middot; last 14 days</span></h2><div class=\"card\">");
        if (dayPeak == 0)
        {
            html.Append($"<p class=\"muted\">No events in the last 14 days. The first sync backfills "
                        + $"{options.Value.GitHub.InitialBackfillDays} days and can take a few minutes.</p>");
        }
        else
        {
            html.Append("<div class=\"columns\">");
            foreach (var day in days)
            {
                var count = perDay.GetValueOrDefault(day);
                var height = count == 0 ? 0 : Math.Max(3, count * 100 / dayPeak);
                // Label the busiest day and today only; the rest are in the tooltip and the table.
                var peak = count == dayPeak && count > 0;
                var labelled = peak || (day == today && count > 0);
                html.Append($"""
                    <div class="col" title="{day:ddd d MMM} &middot; {Fmt(count)} events">
                      <span class="col-value{(peak ? "" : " ghost")}">{(labelled ? Fmt(count) : "")}</span>
                      <span class="col-bar" style="height:{height}%"></span>
                    </div>
                    """);
            }
            html.Append("</div><div class=\"col-labels\">");
            foreach (var day in days)
                html.Append($"<span>{day:d MMM}</span>");
            html.Append("</div>");
            html.Append("<details class=\"table-view\"><summary>Table view</summary><table><tr><th>Day</th><th class=\"num\">Events</th></tr>");
            foreach (var day in days)
                html.Append($"<tr><td>{day:ddd d MMM yyyy}</td><td class=\"num\">{Fmt(perDay.GetValueOrDefault(day))}</td></tr>");
            html.Append("</table></details>");
        }
        html.Append("</div>");

        // ---- what was collected ------------------------------------------------------
        html.Append("<section class=\"grid two\">");

        html.Append("<div><h2>Activity by type</h2><div class=\"card\">");
        if (byType.Count == 0)
            html.Append("<p class=\"muted\">Nothing collected yet.</p>");
        else
            Bars(html, byType.Select(x => (Friendly(x.Type), x.Count)).ToList());
        html.Append("</div></div>");

        html.Append("<div><h2>Where the activity is <span class=\"muted\">&middot; top 8</span></h2><div class=\"card\">");
        if (byRepository.Count == 0)
            html.Append("<p class=\"muted\">No repository activity yet.</p>");
        else
            Bars(html, byRepository.Select(x => (Shorten(x.Repository), x.Count)).ToList());
        html.Append("</div></div>");

        html.Append("</section>");

        // ---- boards ------------------------------------------------------------------
        html.Append("<h2>Boards</h2><div class=\"card\">");
        if (projects.Count == 0)
        {
            html.Append("<p class=\"muted\">No Projects V2 boards tracked. Auto-discovery finds your own boards only and needs "
                        + "<code>read:project</code> on the token; list org boards explicitly in "
                        + "<code>Tracker:GitHub:Projects</code> as <code>owner/number</code>.</p>");
        }
        else
        {
            foreach (var project in projects)
            {
                var columns = ActivityQueryService.ReadStatusColumns(project.StatusOptions);
                var boardItems = itemsPerBoard.GetValueOrDefault(project.Id);
                var title = Escape(project.Title);
                html.Append("<div class=\"board\">");
                html.Append("<div class=\"board-head\"><span class=\"board-name\">"
                            + (string.IsNullOrWhiteSpace(project.Url) ? title : $"<a href=\"{Escape(project.Url)}\">{title}</a>")
                            + $"</span> <span class=\"muted\">#{project.Number} &middot; {Escape(project.OwnerLogin)}</span>"
                            + $"<span class=\"board-count\">{Fmt(boardItems)} {(boardItems == 1 ? "item" : "items")}</span></div>");
                html.Append(columns.Length == 0
                    ? "<p class=\"muted\">No Status field found.</p>"
                    : "<div class=\"chips\">" + string.Concat(columns.Select(c => $"<span class=\"chip\">{Escape(c)}</span>")) + "</div>");
                if (project.LastSyncedAt is { } synced)
                    html.Append($"<p class=\"muted stamp\">synced {periods.Local(synced)}</p>");
                html.Append("</div>");
            }
        }
        html.Append("</div>");

        // ---- writes ------------------------------------------------------------------
        if (recentWrites.Count > 0)
        {
            html.Append("<h2>Recent write operations</h2><div class=\"card\"><table>");
            html.Append("<tr><th>When</th><th>Tool</th><th>Result</th></tr>");
            foreach (var write in recentWrites)
                html.Append($"<tr><td class=\"nowrap\">{periods.Local(write.CreatedAt)}</td>"
                            + $"<td><code>{Escape(write.Tool)}</code></td>"
                            + $"<td>{(write.Success ? "<span class=\"ok\">ok</span>" : "<span class=\"bad\">failed</span>")} "
                            + $"<span class=\"muted\">{Escape(write.Result ?? "")}</span></td></tr>");
            html.Append("</table></div>");
        }

        html.Append("</main></body></html>");
        return Results.Content(html.ToString(), "text/html");
    }

    private static void Tile(StringBuilder sb, string label, int value) =>
        sb.Append($"<div class=\"card tile\"><p class=\"label\">{label}</p><p class=\"tile-value\">{Fmt(value)}</p></div>");

    private static void Fact(StringBuilder sb, string label, string value) =>
        sb.Append($"<div><dt>{label}</dt><dd>{value}</dd></div>");

    /// <summary>Horizontal bars, longest first, each labelled with its own value at the tip.</summary>
    private static void Bars(StringBuilder sb, List<(string Label, int Count)> rows)
    {
        var peak = rows.Max(r => r.Count);
        sb.Append("<div class=\"bars\">");
        foreach (var (label, count) in rows)
        {
            var width = peak == 0 ? 0 : Math.Max(2, count * 100 / peak);
            sb.Append($"""
                <div class="bar-row" title="{Escape(label)} &middot; {Fmt(count)}">
                  <span class="bar-label">{Escape(label)}</span>
                  <span class="bar-track"><span class="bar-fill" style="width:{width}%"></span></span>
                  <span class="bar-value">{Fmt(count)}</span>
                </div>
                """);
        }
        sb.Append("</div>");
    }

    private static string Friendly(string activityType) => activityType switch
    {
        ActivityTypes.Commit => "Commits",
        ActivityTypes.PrOpened => "PRs opened",
        ActivityTypes.PrMerged => "PRs merged",
        ActivityTypes.PrClosed => "PRs closed",
        ActivityTypes.PrReview => "Reviews given",
        ActivityTypes.PrComment => "PR comments",
        ActivityTypes.IssueOpened => "Issues opened",
        ActivityTypes.IssueClosed => "Issues closed",
        ActivityTypes.IssueComment => "Issue comments",
        ActivityTypes.ProjectItemAdded => "Board items added",
        ActivityTypes.ProjectItemMoved => "Board items moved",
        _ => activityType.Replace('_', ' ')
    };

    /// <summary>Owner prefixes are the same on every row and just push the name out of view.</summary>
    private static string Shorten(string fullName)
    {
        var slash = fullName.IndexOf('/');
        return slash < 0 ? fullName : fullName[(slash + 1)..];
    }

    private static string Fmt(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    /// <summary>
    /// Colours come from the validated reference palette: one blue for the single data series
    /// (it passes the lightness, chroma and 3:1 contrast checks on both surfaces), and the
    /// reserved status four, which never appear as data.
    /// </summary>
    private const string Css = """
      :root {
        color-scheme: light;
        --plane: #f9f9f7; --surface: #fcfcfb;
        --ink: #0b0b0b; --ink-2: #52514e; --muted: #898781;
        --hairline: rgba(11,11,11,0.10); --grid: #e1e0d9; --track: #eceae4;
        --series: #2a78d6;
        --good: #0ca30c; --warning: #fab219; --critical: #d03b3b; --idle: #898781;
        --good-ink: #006300; --critical-ink: #b02a2a;
      }
      @media (prefers-color-scheme: dark) {
        :root {
          color-scheme: dark;
          --plane: #0d0d0d; --surface: #1a1a19;
          --ink: #ffffff; --ink-2: #c3c2b7; --muted: #898781;
          --hairline: rgba(255,255,255,0.10); --grid: #2c2c2a; --track: #2c2c2a;
          --series: #3987e5;
          --good-ink: #0ca30c; --critical-ink: #e66767;
        }
      }
      * { box-sizing: border-box; }
      body { margin: 0; padding: 40px 24px 72px;
             font: 15px/1.55 system-ui, -apple-system, "Segoe UI", sans-serif;
             background: var(--plane); color: var(--ink); }
      main { max-width: 1040px; margin: 0 auto; }
      a { color: inherit; text-decoration-color: var(--muted); text-underline-offset: 2px; }

      h1 { font-size: 21px; margin: 0 0 6px; letter-spacing: -0.01em; }
      h2 { font-size: 12px; margin: 34px 0 10px; text-transform: uppercase;
           letter-spacing: .08em; color: var(--ink-2); font-weight: 600; }
      .sub { margin: 0; color: var(--ink-2); font-size: 13.5px; }
      .muted { color: var(--muted); font-weight: 400; }
      .ok { color: var(--good-ink); font-weight: 600; }
      .bad { color: var(--critical-ink); font-weight: 600; }
      code { background: var(--track); padding: 1px 6px; border-radius: 5px;
             font-size: 12.5px; font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; }

      .head { display: flex; align-items: flex-start; justify-content: space-between;
              gap: 20px; flex-wrap: wrap; margin-bottom: 22px; }
      button, .btn { font: inherit; font-size: 14px; font-weight: 600; padding: 9px 18px;
               border-radius: 8px; border: 1px solid var(--hairline); background: var(--surface);
               color: var(--ink); cursor: pointer; box-shadow: 0 1px 2px rgba(0,0,0,.05);
               text-decoration: none; display: inline-block; }
      button:hover, .btn:hover { border-color: var(--muted); }
      .actions { display: flex; gap: 8px; align-items: center; }

      .pill { display: inline-flex; align-items: center; gap: 6px; vertical-align: 3px;
              padding: 3px 11px; border-radius: 999px; font-size: 11.5px; font-weight: 700;
              letter-spacing: .04em; text-transform: uppercase; color: #fff; }
      .pill.good { background: var(--good); } .pill.warning { background: var(--warning); color: #2b2100; }
      .pill.critical { background: var(--critical); } .pill.idle { background: var(--idle); }

      .card { background: var(--surface); border: 1px solid var(--hairline);
              border-radius: 12px; padding: 18px 20px; }
      .note { margin: 14px 0 0; padding: 14px 18px; border-radius: 12px;
              border: 1px solid var(--critical); color: var(--critical-ink);
              background: color-mix(in srgb, var(--critical) 8%, var(--surface)); }

      .grid { display: grid; gap: 14px; }
      .hero-row { grid-template-columns: minmax(230px, 1fr) 2.2fr; align-items: stretch; }
      .two { grid-template-columns: 1fr 1fr; align-items: start; }
      .tiles { display: grid; grid-template-columns: repeat(3, 1fr); gap: 14px; }
      @media (max-width: 820px) {
        .hero-row, .two { grid-template-columns: 1fr; }
        .tiles { grid-template-columns: repeat(2, 1fr); }
      }

      .label { margin: 0; font-size: 12px; color: var(--ink-2); }
      .hero { display: flex; flex-direction: column; justify-content: center; }
      .figure { margin: 6px 0 4px; font-size: 52px; line-height: 1; font-weight: 600;
                letter-spacing: -0.02em; }
      .tile { padding: 14px 16px; }
      .tile-value { margin: 4px 0 0; font-size: 26px; font-weight: 600; line-height: 1.1; }

      .facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
               gap: 16px 24px; margin: 0; }
      dt { font-size: 12px; color: var(--ink-2); margin-bottom: 2px; }
      dd { margin: 0; font-size: 14.5px; font-variant-numeric: tabular-nums; }

      /* Columns: 4px rounded cap, square on the baseline, capped width, air in the band. */
      .columns { display: flex; align-items: flex-end; gap: 8px; height: 160px; padding-top: 6px;
                 border-bottom: 1px solid var(--grid); }
      .col { flex: 1; display: flex; flex-direction: column; justify-content: flex-end;
             align-items: center; height: 100%; gap: 6px; }
      .col-bar { display: block; width: 100%; max-width: 24px; background: var(--series);
                 border-radius: 4px 4px 0 0; min-height: 0; }
      .col-value { font-size: 11.5px; font-weight: 600; font-variant-numeric: tabular-nums; }
      .col-value.ghost { color: var(--muted); font-weight: 400; }
      .col-labels { display: flex; gap: 8px; padding-top: 8px; }
      .col-labels span { flex: 1; text-align: center; font-size: 11px; color: var(--muted);
                         white-space: nowrap; }
      .col:hover .col-bar { filter: brightness(1.08); }

      .table-view { margin-top: 16px; border-top: 1px solid var(--grid); padding-top: 10px; }
      .table-view summary { font-size: 12.5px; color: var(--ink-2); cursor: pointer; }
      .table-view table { margin-top: 8px; }

      /* Bars: thin marks, 4px rounded tip, value at the tip, recessive track. */
      .bars { display: flex; flex-direction: column; gap: 11px; }
      .bar-row { display: grid; grid-template-columns: 142px 1fr 52px; align-items: center; gap: 12px; }
      .bar-label { font-size: 13px; color: var(--ink-2); overflow: hidden;
                   text-overflow: ellipsis; white-space: nowrap; }
      .bar-track { background: var(--track); border-radius: 5px; height: 10px; }
      .bar-fill { display: block; height: 100%; background: var(--series);
                  border-radius: 0 4px 4px 0; }
      .bar-value { font-size: 13px; font-variant-numeric: tabular-nums; text-align: right; }
      .bar-row:hover .bar-fill { filter: brightness(1.08); }

      .board { padding: 14px 0; border-bottom: 1px solid var(--grid); }
      .board:first-child { padding-top: 0; }
      .board:last-child { border-bottom: none; padding-bottom: 0; }
      .board-head { display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap; }
      .board-name { font-weight: 600; }
      .board-count { margin-left: auto; font-size: 13px; color: var(--ink-2);
                     font-variant-numeric: tabular-nums; }
      .chips { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 10px; }
      .chip { font-size: 11.5px; padding: 3px 9px; border-radius: 999px;
              background: var(--track); color: var(--ink-2); }
      .stamp { margin: 10px 0 0; font-size: 12px; }

      table { width: 100%; border-collapse: collapse; }
      td, th { text-align: left; padding: 8px 10px 8px 0; border-bottom: 1px solid var(--grid);
               font-size: 13.5px; }
      th { color: var(--ink-2); font-weight: 600; font-size: 12px; }
      tr:last-child td { border-bottom: none; }
      .num { text-align: right; font-variant-numeric: tabular-nums; }
      .nowrap { white-space: nowrap; }
      """;
}
