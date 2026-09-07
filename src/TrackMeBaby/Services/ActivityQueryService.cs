using Microsoft.EntityFrameworkCore;
using TrackMeBaby.Data;

namespace TrackMeBaby.Services;

/// <summary>Read-side queries over the stored facts. No GitHub calls happen here.</summary>
public class ActivityQueryService(TrackerDbContext db, PeriodResolver periods)
{
    public const int MaxLimit = 500;

    public IQueryable<Activity> Mine(Period period, IEnumerable<string>? types = null, string? repository = null)
    {
        var query = db.Activities.AsNoTracking()
            .Where(a => a.IsMine && a.OccurredAt >= period.From && a.OccurredAt <= period.To);

        if (types is not null)
        {
            var list = types.ToArray();
            if (list.Length > 0) query = query.Where(a => list.Contains(a.ActivityType));
        }

        if (!string.IsNullOrWhiteSpace(repository))
        {
            var pattern = $"%{repository}%";
            query = query.Where(a => a.RepositoryFullName != null && EF.Functions.ILike(a.RepositoryFullName, pattern));
        }

        return query;
    }

    public async Task<List<ActivityDto>> ListAsync(
        Period period, IEnumerable<string>? types = null, string? repository = null,
        int limit = 100, CancellationToken ct = default)
    {
        var activities = await Mine(period, types, repository)
            .OrderByDescending(a => a.OccurredAt)
            .Take(Clamp(limit))
            .ToListAsync(ct);

        return activities.Select(ToDto).ToList();
    }

    public async Task<List<ProjectItemDto>> ListProjectItemsAsync(
        bool mineOnly = true, string? status = null, bool includeArchived = false,
        int limit = 200, CancellationToken ct = default)
    {
        var query = db.ProjectItems.AsNoTracking().Include(i => i.Project).AsQueryable();
        if (mineOnly) query = query.Where(i => i.IsMine);
        if (!includeArchived) query = query.Where(i => !i.IsArchived);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(i => i.Status != null && EF.Functions.ILike(i.Status, status));

        var items = await query
            .OrderByDescending(i => i.ItemUpdatedAt)
            .Take(Clamp(limit))
            .ToListAsync(ct);

        return items.Select(ToDto).ToList();
    }

    public async Task<List<ProjectDto>> ListProjectsAsync(CancellationToken ct = default)
    {
        var projects = await db.Projects.AsNoTracking().OrderByDescending(p => p.UpdatedAt).ToListAsync(ct);
        var counts = await db.ProjectItems.AsNoTracking()
            .Where(i => i.IsMine && !i.IsArchived)
            .GroupBy(i => i.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, ct);

        return projects.Select(p => new ProjectDto(
            p.Id, p.Number, p.OwnerLogin, p.Title, p.Url, p.Closed,
            ReadStatusColumns(p.StatusOptions),
            counts.GetValueOrDefault(p.Id),
            p.LastSyncedAt is null ? null : periods.Local(p.LastSyncedAt.Value))).ToList();
    }

    public static string[] ReadStatusColumns(string? statusOptionsJson)
    {
        if (string.IsNullOrWhiteSpace(statusOptionsJson)) return [];
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(statusOptionsJson);
            return map?.Keys.ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<List<RepositoryStatDto>> RepositoryStatsAsync(Period period, CancellationToken ct = default)
    {
        var rows = await Mine(period)
            .Where(a => a.RepositoryFullName != null)
            .GroupBy(a => a.RepositoryFullName!)
            .Select(g => new
            {
                Repository = g.Key,
                Total = g.Count(),
                Commits = g.Count(a => a.ActivityType == ActivityTypes.Commit),
                Merged = g.Count(a => a.ActivityType == ActivityTypes.PrMerged)
            })
            .OrderByDescending(x => x.Total)
            .ToListAsync(ct);

        return rows.Select(r => new RepositoryStatDto(r.Repository, r.Total, r.Commits, r.Merged)).ToList();
    }

    public ActivityDto ToDto(Activity a) => new(
        a.Id,
        a.ActivityType,
        a.RepositoryFullName,
        a.Number,
        a.Title,
        a.Summary,
        a.Url,
        a.OccurredAt.ToString("u"),
        periods.LocalDate(a.OccurredAt),
        a.State,
        a.Additions,
        a.Deletions,
        a.ChangedFiles,
        a.Labels);

    public ProjectItemDto ToDto(ProjectItem i) => new(
        i.Id,
        i.Project?.Title ?? "",
        i.Project?.Number ?? 0,
        i.Title,
        i.ContentType,
        i.ContentNumber,
        i.RepositoryFullName,
        i.Status,
        i.PreviousStatus,
        i.StatusChangedAt is null ? null : periods.LocalDate(i.StatusChangedAt.Value),
        i.Url,
        i.Assignees,
        i.ContentState);

    public static int Clamp(int limit) => limit <= 0 ? 50 : Math.Min(limit, MaxLimit);
}
