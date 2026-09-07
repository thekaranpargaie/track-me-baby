using Microsoft.EntityFrameworkCore;
using TrackMeBaby.Data;
using TrackMeBaby.Sync;

namespace TrackMeBaby.Services;

/// <summary>
/// The durations and distributions behind "how do I actually work" — the questions
/// <see cref="PerformanceService"/> cannot answer because counting events cannot answer them.
///
/// Nothing here is a score. Every figure is a measurement with its denominator attached, because a
/// median cycle time of two days means one thing on twelve changes and another on two.
/// </summary>
public class FlowMetricsService(
    TrackerDbContext db,
    ChangeLogService changeLog,
    PeriodResolver periods)
{
    public async Task<FlowReport> BuildAsync(Period period, int limit = 40, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var changes = await changeLog.LoadAsync(period, ct);

        // Merged inside the window is the honest denominator for anything about finished work:
        // a change merged before it started belongs to the previous report.
        var merged = changes.Where(c => c.MergedAt >= period.From && c.MergedAt <= period.To).ToList();
        var abandoned = changes
            .Where(c => !c.IsMerged && c.ClosedAt >= period.From && c.ClosedAt <= period.To)
            .ToList();

        var leadTime = Stats.Durations(merged.Select(c => c.LeadTime).Where(t => t is not null).Select(t => t!.Value));
        var reviewTime = Stats.Durations(merged.Select(c => c.ReviewTime).Where(t => t is not null).Select(t => t!.Value));
        var branchLife = Stats.Durations(merged
            .Where(c => c.HasCommitData)
            .Select(c => c.MergedAt!.Value - c.FirstCommitAt));

        if (merged.Count == 0)
            notes.Add("No pull requests merged in this period, so the duration figures are empty.");

        var noCommits = merged.Count(c => !c.HasCommitData);
        if (noCommits > 0)
            notes.Add($"Lead time is measured over {merged.Count - noCommits} of {merged.Count} merged changes. "
                      + $"The other {noCommits} had no commits on record — usually a branch authored under a "
                      + "different git identity, or a squash merge whose commits GitHub no longer attributes to "
                      + "you — and are excluded rather than assigned the open time, which would report them as "
                      + "near-instant.");

        var (boardCycle, boardByStatus) = await BoardFlowAsync(period, ct);
        var openWork = await OpenWorkAsync(period, changes, limit, ct);
        var (reviewsGiven, partners) = await ReviewLoadAsync(period, ct);
        var calendar = await CalendarAsync(period, ct);

        notes.Add("Time to first review is not measured: only reviews you gave are collected, not reviews "
                  + "others gave you, so the clock on your own pull requests cannot be started.");

        return new FlowReport(
            period.Label,
            period.From.ToString("u"),
            period.To.ToString("u"),

            leadTime,
            reviewTime,
            boardCycle,

            merged.Count,
            abandoned.Count,
            merged.Count + abandoned.Count == 0
                ? null
                : (double)merged.Count / (merged.Count + abandoned.Count),
            branchLife,

            Weekly(period, merged, c => c.MergedAt!.Value, c => 1),
            Weekly(period, merged, c => c.MergedAt!.Value, c => c.Churn),
            LeadTimeTrend(period, merged),
            WorkTypeMix(merged),
            SizeMix(merged),
            RepositoryMix(merged),
            boardByStatus,

            reviewsGiven,
            partners,

            openWork,
            openWork.Count == 0 ? null : openWork.Max(w => w.AgeDays),

            calendar.Count(d => d.Count > 0),
            calendar.Count,
            calendar,
            calendar.Count(d => d.Weekend && d.Count > 0),
            notes);
    }

    /// <summary>
    /// Board flow, reconstructed from the project_item_moved trail. Each move records where the
    /// card came from and when, so consecutive moves bracket the time spent in a column.
    /// </summary>
    private async Task<(DurationStats Cycle, List<Slice> ByStatus)> BoardFlowAsync(Period period, CancellationToken ct)
    {
        var moves = await db.Activities.AsNoTracking()
            .Where(a => a.IsMine && a.ActivityType == ActivityTypes.ProjectItemMoved
                        && a.OccurredAt >= period.From - ChangeLogService.Lookback
                        && a.OccurredAt <= period.To)
            .OrderBy(a => a.OccurredAt)
            .Select(a => new { a.Payload, a.OccurredAt, a.State, a.RepositoryFullName, a.Number, a.Title })
            .ToListAsync(ct);

        var spans = new List<(string Status, TimeSpan Elapsed, DateTimeOffset LeftAt)>();
        var completed = new List<TimeSpan>();

        // Group by the card, not the issue: the same issue can sit on two boards.
        var byItem = moves
            .Select(m => new
            {
                Item = ReadItemId(m.Payload) ?? $"{m.RepositoryFullName}#{m.Number}",
                From = ReadStatus(m.Payload, "FromStatus"),
                To = ReadStatus(m.Payload, "ToStatus") ?? m.State,
                m.OccurredAt
            })
            .GroupBy(m => m.Item);

        foreach (var item in byItem)
        {
            var ordered = item.OrderBy(m => m.OccurredAt).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var status = ordered[i - 1].To;
                if (status is null) continue;
                var elapsed = ordered[i].OccurredAt - ordered[i - 1].OccurredAt;
                if (elapsed <= TimeSpan.Zero) continue;
                spans.Add((status, elapsed, ordered[i].OccurredAt));
            }

            // Entry to done, for the cards that reached done inside the window.
            var start = ordered[0].OccurredAt;
            var done = ordered.LastOrDefault(m => LooksDone(m.To));
            if (done is not null && done.OccurredAt >= period.From && done.OccurredAt <= period.To
                && done.OccurredAt > start)
                completed.Add(done.OccurredAt - start);
        }

        var inWindow = spans.Where(s => s.LeftAt >= period.From && s.LeftAt <= period.To).ToList();

        var byStatus = inWindow
            .GroupBy(s => s.Status, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Slice(
                g.Key,
                g.Key,
                (int)Math.Round(g.Sum(s => s.Elapsed.TotalHours)),
                g.Count()))
            .OrderByDescending(s => s.Count)
            .ToList();

        return (Stats.Durations(completed), byStatus);
    }

    /// <summary>
    /// What is open right now and how long it has been open. Age, not count, is what tells you
    /// whether work in progress is flowing or piling up.
    /// </summary>
    private async Task<List<WipItem>> OpenWorkAsync(
        Period period, List<ChangeRecord> changes, int limit, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var items = new List<WipItem>();

        foreach (var change in changes.Where(c => !c.IsMerged && c.ClosedAt is null))
            items.Add(new WipItem(
                "pull request",
                change.Title,
                change.Repository,
                change.Number,
                "open",
                change.Url,
                Math.Round((now - change.OpenedAt).TotalDays, 1),
                periods.LocalDate(change.OpenedAt)));

        var boardItems = await db.ProjectItems.AsNoTracking()
            .Include(i => i.Project)
            .Where(i => i.IsMine && !i.IsArchived && i.ClosedAt == null)
            .ToListAsync(ct);

        foreach (var item in boardItems.Where(i => !LooksDone(i.Status)))
        {
            var since = item.StatusChangedAt ?? item.ItemCreatedAt;
            items.Add(new WipItem(
                "board item",
                item.Title,
                item.RepositoryFullName ?? item.Project?.Title,
                item.ContentNumber,
                item.Status,
                item.Url,
                Math.Round((now - since).TotalDays, 1),
                periods.LocalDate(since)));
        }

        return items.OrderByDescending(i => i.AgeDays).Take(ActivityQueryService.Clamp(limit)).ToList();
    }

    /// <summary>
    /// Review load, and whose work it was. Reviewing is the least visible thing an engineer does
    /// and the easiest to lose at appraisal time, so the partner breakdown is the evidence.
    /// </summary>
    private async Task<(int Given, List<ReviewPartner> Partners)> ReviewLoadAsync(Period period, CancellationToken ct)
    {
        var reviews = await db.Activities.AsNoTracking()
            .Where(a => a.IsMine && a.ActivityType == ActivityTypes.PrReview
                        && a.OccurredAt >= period.From && a.OccurredAt <= period.To)
            .Select(a => a.Payload)
            .ToListAsync(ct);

        var partners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in reviews)
        {
            var author = ReadString(payload, "PullRequestAuthor");
            if (author is null) continue;
            partners[author] = partners.GetValueOrDefault(author) + 1;
        }

        return (reviews.Count, partners
            .OrderByDescending(p => p.Value)
            .Select(p => new ReviewPartner(p.Key, p.Value))
            .ToList());
    }

    /// <summary>Every day in the window with its event count, in the configured time zone.</summary>
    private async Task<List<DayCount>> CalendarAsync(Period period, CancellationToken ct)
    {
        var stamps = await db.Activities.AsNoTracking()
            .Where(a => a.IsMine && a.OccurredAt >= period.From && a.OccurredAt <= period.To)
            .Select(a => a.OccurredAt)
            .ToListAsync(ct);

        var counts = stamps
            .GroupBy(s => periods.ToLocalTime(s).Date)
            .ToDictionary(g => g.Key, g => g.Count());

        var days = new List<DayCount>();
        var cursor = periods.ToLocalTime(period.From).Date;
        var end = periods.ToLocalTime(period.To).Date;
        // A year of daily cells is the most a heatmap can carry legibly.
        while (cursor <= end && days.Count < 400)
        {
            days.Add(new DayCount(
                cursor.ToString("yyyy-MM-dd"),
                counts.GetValueOrDefault(cursor),
                cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday));
            cursor = cursor.AddDays(1);
        }

        return days;
    }

    private List<TimeBucket> Weekly(
        Period period, List<ChangeRecord> changes,
        Func<ChangeRecord, DateTimeOffset> when, Func<ChangeRecord, int> value)
    {
        var buckets = new List<TimeBucket>();
        var cursor = StartOfWeek(periods.ToLocalTime(period.From));
        var end = periods.ToLocalTime(period.To);

        while (cursor <= end)
        {
            var next = cursor.AddDays(7);
            var total = changes
                .Where(c =>
                {
                    var local = periods.ToLocalTime(when(c));
                    return local >= cursor && local < next;
                })
                .Sum(value);
            buckets.Add(new TimeBucket($"{cursor:d MMM}", cursor.ToString("yyyy-MM-dd"), total));
            cursor = next;
        }

        return buckets;
    }

    /// <summary>
    /// Median lead time week by week. A week with no merges gets a null rather than a zero: the
    /// gap in the line is the truth, and a zero would read as "shipped instantly".
    /// </summary>
    private List<TrendPoint> LeadTimeTrend(Period period, List<ChangeRecord> merged)
    {
        var points = new List<TrendPoint>();
        var cursor = StartOfWeek(periods.ToLocalTime(period.From));
        var end = periods.ToLocalTime(period.To);

        while (cursor <= end)
        {
            var next = cursor.AddDays(7);
            var hours = merged
                .Where(c =>
                {
                    var local = periods.ToLocalTime(c.MergedAt!.Value);
                    return local >= cursor && local < next;
                })
                .Select(c => c.LeadTime)
                .Where(t => t is not null)
                .Select(t => t!.Value.TotalHours)
                .OrderBy(h => h)
                .ToList();

            points.Add(new TrendPoint(
                $"{cursor:d MMM}", cursor.ToString("yyyy-MM-dd"),
                Stats.Percentile(hours, 0.50), hours.Count));
            cursor = next;
        }

        return points;
    }

    private static List<Slice> WorkTypeMix(List<ChangeRecord> merged) =>
        WorkTypes.All
            .Select(type => new Slice(
                type,
                WorkTypes.Label(type),
                merged.Count(c => c.WorkType == type),
                merged.Where(c => c.WorkType == type).Sum(c => c.Churn)))
            .Where(s => s.Count > 0)
            .ToList();

    /// <summary>
    /// Batch size. Small changes are the cheapest thing an engineer can do for their reviewers,
    /// and the distribution says more than the mean line count ever does.
    /// </summary>
    private static List<Slice> SizeMix(List<ChangeRecord> merged)
    {
        (string Key, string Label, Func<int, bool> Test)[] buckets =
        [
            ("xs", "Under 10 lines", churn => churn < 10),
            ("s", "10 to 100", churn => churn is >= 10 and < 100),
            ("m", "100 to 500", churn => churn is >= 100 and < 500),
            ("l", "500 to 2,000", churn => churn is >= 500 and < 2000),
            ("xl", "Over 2,000", churn => churn >= 2000)
        ];

        return buckets
            .Select(b => new Slice(b.Key, b.Label, merged.Count(c => b.Test(c.Churn))))
            .Where(s => s.Count > 0)
            .ToList();
    }

    private static List<Slice> RepositoryMix(List<ChangeRecord> merged) =>
        merged
            .GroupBy(c => c.Repository)
            .Select(g => new Slice(g.Key, g.Key, g.Count(), g.Sum(c => c.Churn)))
            .OrderByDescending(s => s.Count)
            .ToList();

    private static DateTimeOffset StartOfWeek(DateTimeOffset value)
    {
        var date = value.Date.AddDays(-(((int)value.DayOfWeek + 6) % 7));
        return new DateTimeOffset(date, value.Offset);
    }

    /// <summary>Board columns that mean finished, matched loosely because column names vary.</summary>
    internal static bool LooksDone(string? status) =>
        status is not null && (status.Contains("done", StringComparison.OrdinalIgnoreCase)
                               || status.Contains("complete", StringComparison.OrdinalIgnoreCase)
                               || status.Contains("closed", StringComparison.OrdinalIgnoreCase)
                               || status.Contains("shipped", StringComparison.OrdinalIgnoreCase)
                               || status.Contains("released", StringComparison.OrdinalIgnoreCase));

    private static string? ReadItemId(string? payload) => ReadString(payload, "ItemId");
    private static string? ReadStatus(string? payload, string property) => ReadString(payload, property);

    private static string? ReadString(string? payload, string property)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.GetStringOrNull(property);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
