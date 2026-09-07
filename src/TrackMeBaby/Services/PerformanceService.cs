using Microsoft.EntityFrameworkCore;
using TrackMeBaby.Data;

namespace TrackMeBaby.Services;

/// <summary>
/// Assembles the evidence package behind "what did I accomplish?".
///
/// Deliberately does no interpretation: it returns counts, the activity rows that produced
/// those counts, and the relevant memories. Claude writes the narrative, which keeps facts
/// and conclusions separate (plan section 12) and every claim traceable (FR-12).
/// </summary>
public class PerformanceService(
    TrackerDbContext db,
    ActivityQueryService activities,
    MemoryService memories,
    PeriodResolver periods)
{
    public async Task<PerformanceReport> BuildAsync(Period period, int detailLimit = 60, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var limit = ActivityQueryService.Clamp(detailLimit);

        var rows = await activities.Mine(period).ToListAsync(ct);
        var span = period.To - period.From;
        var previous = new Period(period.From - span, period.From.AddTicks(-1), "previous period");
        var previousRows = await activities.Mine(previous).ToListAsync(ct);

        var byType = rows
            .GroupBy(a => a.ActivityType)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

        var byWeek = rows
            .GroupBy(a => IsoWeekLabel(a.OccurredAt))
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        var merged = Pick(rows, ActivityTypes.PrMerged, limit, notes, "merged pull requests");
        var reviews = Pick(rows, ActivityTypes.PrReview, limit, notes, "reviews");
        var issuesClosed = Pick(rows, ActivityTypes.IssueClosed, limit, notes, "closed issues");

        var largest = rows
            .Where(a => a.ActivityType is ActivityTypes.PrMerged or ActivityTypes.PrOpened)
            .OrderByDescending(a => (a.Additions ?? 0) + (a.Deletions ?? 0))
            .Take(10)
            .Select(a => activities.ToDto(a))
            .ToList();

        var boardItems = await db.ProjectItems.AsNoTracking()
            .Include(i => i.Project)
            .Where(i => i.IsMine && !i.IsArchived)
            .ToListAsync(ct);

        var completedBoardWork = boardItems
            .Where(i => LooksDone(i.Status) || (i.ClosedAt >= period.From && i.ClosedAt <= period.To))
            .Where(i => i.ItemUpdatedAt >= period.From && i.ItemUpdatedAt <= period.To
                        || (i.ClosedAt >= period.From && i.ClosedAt <= period.To))
            .OrderByDescending(i => i.ClosedAt ?? i.ItemUpdatedAt)
            .Take(limit)
            .Select(i => activities.ToDto(i))
            .ToList();

        var inProgressBoardWork = boardItems
            .Where(i => !LooksDone(i.Status) && i.ClosedAt is null)
            .OrderByDescending(i => i.ItemUpdatedAt)
            .Take(limit)
            .Select(i => activities.ToDto(i))
            .ToList();

        var relevantMemories = await memories.SearchAsync(null, null, null, period, limit, ct);
        if (relevantMemories.Count == 0)
            notes.Add("No memories recorded for this period. Numbers alone understate impact — use save_memory to capture context as it happens.");

        var snapshots = await db.PerformanceSnapshots.AsNoTracking()
            .Where(s => s.PeriodStart >= period.From && s.PeriodEnd <= period.To)
            .OrderBy(s => s.PeriodStart)
            .Select(s => new SnapshotDto(
                s.Id, s.PeriodType, s.PeriodStart.ToString("u"), s.PeriodEnd.ToString("u"),
                s.Title, s.Content, s.CreatedAt.ToString("u")))
            .ToListAsync(ct);

        var syncState = await db.SyncStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Source == Sync.GitHubSyncService.SourceName, ct);
        if (syncState?.LastSuccessfulSync is { } last && DateTimeOffset.UtcNow - last > TimeSpan.FromHours(6))
            notes.Add($"Data may be stale: the last successful GitHub sync was {periods.Local(last)}.");
        if (syncState is null || syncState.LastSuccessfulSync is null)
            notes.Add("GitHub has never synced successfully, so this report is probably empty. Check get_sync_status.");

        return new PerformanceReport(
            period.Label,
            period.From.ToString("u"),
            period.To.ToString("u"),
            BuildMetrics(rows, boardItems, period),
            BuildMetrics(previousRows, [], previous),
            $"{periods.LocalDate(previous.From)} to {periods.LocalDate(previous.To)}",
            byType,
            byWeek,
            await activities.RepositoryStatsAsync(period, ct),
            merged,
            reviews,
            issuesClosed,
            completedBoardWork,
            inProgressBoardWork,
            largest,
            relevantMemories,
            snapshots,
            notes);
    }

    private List<ActivityDto> Pick(
        List<Activity> rows, string type, int limit, List<string> notes, string label)
    {
        var all = rows.Where(a => a.ActivityType == type).OrderByDescending(a => a.OccurredAt).ToList();
        if (all.Count > limit)
            notes.Add($"Showing {limit} of {all.Count} {label}; raise detail_limit or narrow the period to see the rest.");
        return all.Take(limit).Select(a => activities.ToDto(a)).ToList();
    }

    private static PerformanceMetrics BuildMetrics(List<Activity> rows, List<ProjectItem> boardItems, Period period)
    {
        int Count(string type) => rows.Count(a => a.ActivityType == type);

        var mergedPrs = rows.Where(a => a.ActivityType == ActivityTypes.PrMerged).ToList();
        var activeDays = rows.Select(a => a.OccurredAt.Date).Distinct().Count();
        var busiest = rows
            .GroupBy(a => a.OccurredAt.Date)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        return new PerformanceMetrics(
            Count(ActivityTypes.Commit),
            Count(ActivityTypes.PrOpened),
            mergedPrs.Count,
            Count(ActivityTypes.PrClosed),
            Count(ActivityTypes.PrReview),
            Count(ActivityTypes.PrComment),
            Count(ActivityTypes.IssueOpened),
            Count(ActivityTypes.IssueClosed),
            Count(ActivityTypes.IssueComment),
            Count(ActivityTypes.ProjectItemAdded),
            Count(ActivityTypes.ProjectItemMoved),
            mergedPrs.Sum(a => a.Additions ?? 0),
            mergedPrs.Sum(a => a.Deletions ?? 0),
            mergedPrs.Sum(a => a.ChangedFiles ?? 0),
            rows.Where(a => a.RepositoryFullName is not null).Select(a => a.RepositoryFullName).Distinct().Count(),
            activeDays,
            busiest is null ? null : $"{busiest.Key:yyyy-MM-dd} ({busiest.Count()} events)",
            rows.Count);
    }

    /// <summary>Board columns that mean finished. Matched loosely because column names vary.</summary>
    private static bool LooksDone(string? status) =>
        status is not null && (status.Contains("done", StringComparison.OrdinalIgnoreCase)
                               || status.Contains("complete", StringComparison.OrdinalIgnoreCase)
                               || status.Contains("closed", StringComparison.OrdinalIgnoreCase)
                               || status.Contains("shipped", StringComparison.OrdinalIgnoreCase)
                               || status.Contains("released", StringComparison.OrdinalIgnoreCase));

    private static string IsoWeekLabel(DateTimeOffset value)
    {
        var date = value.UtcDateTime;
        return $"{ISOWeek.GetYear(date)}-W{ISOWeek.GetWeekOfYear(date):00}";
    }

    public async Task<SnapshotDto> SaveSnapshotAsync(
        string periodType, Period period, string title, string content, string? metricsJson, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await db.PerformanceSnapshots.FirstOrDefaultAsync(
            s => s.PeriodType == periodType && s.PeriodStart == period.From && s.PeriodEnd == period.To, ct);

        if (existing is null)
        {
            existing = new PerformanceSnapshot
            {
                PeriodType = periodType,
                PeriodStart = period.From,
                PeriodEnd = period.To,
                CreatedAt = now
            };
            db.PerformanceSnapshots.Add(existing);
        }

        existing.Title = title;
        existing.Content = content;
        existing.Metrics = metricsJson;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return new SnapshotDto(existing.Id, existing.PeriodType,
            existing.PeriodStart.ToString("u"), existing.PeriodEnd.ToString("u"),
            existing.Title, existing.Content, existing.CreatedAt.ToString("u"));
    }

    public async Task<List<SnapshotDto>> ListSnapshotsAsync(
        string? periodType, int limit = 25, CancellationToken ct = default)
    {
        var query = db.PerformanceSnapshots.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(periodType))
            query = query.Where(s => s.PeriodType == periodType);

        return await query
            .OrderByDescending(s => s.PeriodStart)
            .Take(ActivityQueryService.Clamp(limit))
            .Select(s => new SnapshotDto(
                s.Id, s.PeriodType, s.PeriodStart.ToString("u"), s.PeriodEnd.ToString("u"),
                s.Title, s.Content, s.CreatedAt.ToString("u")))
            .ToListAsync(ct);
    }
}
