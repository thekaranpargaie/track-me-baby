using Microsoft.EntityFrameworkCore;
using TrackMeBaby.Data;

namespace TrackMeBaby.Services;

public class MemoryService(TrackerDbContext db, PeriodResolver periods)
{
    public async Task<MemoryDto> SaveAsync(
        string kind, string title, string content, string[]? tags, DateTimeOffset? occurredAt,
        string? repository, int? number, long[]? relatedActivityIds, int importance,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var memory = new Memory
        {
            Kind = NormalizeKind(kind),
            Title = title.Trim(),
            Content = content.Trim(),
            Tags = tags ?? [],
            OccurredAt = occurredAt ?? now,
            RelatedRepository = repository,
            RelatedNumber = number,
            RelatedActivityIds = relatedActivityIds ?? [],
            Importance = Math.Clamp(importance, 1, 5),
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Memories.Add(memory);
        await db.SaveChangesAsync(ct);
        return ToDto(memory);
    }

    public async Task<MemoryDto?> UpdateAsync(
        long id, string? kind, string? title, string? content, string[]? tags,
        DateTimeOffset? occurredAt, string? repository, int? number, int? importance,
        CancellationToken ct = default)
    {
        var memory = await db.Memories.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (memory is null) return null;

        if (kind is not null) memory.Kind = NormalizeKind(kind);
        if (title is not null) memory.Title = title.Trim();
        if (content is not null) memory.Content = content.Trim();
        if (tags is not null) memory.Tags = tags;
        if (occurredAt is not null) memory.OccurredAt = occurredAt.Value;
        if (repository is not null) memory.RelatedRepository = repository;
        if (number is not null) memory.RelatedNumber = number;
        if (importance is not null) memory.Importance = Math.Clamp(importance.Value, 1, 5);
        memory.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return ToDto(memory);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var memory = await db.Memories.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (memory is null) return false;
        db.Memories.Remove(memory);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<MemoryDto?> GetAsync(long id, CancellationToken ct = default)
    {
        var memory = await db.Memories.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        return memory is null ? null : ToDto(memory);
    }

    /// <summary>
    /// Full-text search over title + content, ranked. Falls back to a substring match when the
    /// text search finds nothing, so a lookup for a partial token still returns something useful.
    /// </summary>
    public async Task<List<MemoryDto>> SearchAsync(
        string? query, string? kind, string[]? tags, Period? period, int limit = 25, CancellationToken ct = default)
    {
        var baseQuery = db.Memories.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(kind))
        {
            var normalized = NormalizeKind(kind);
            baseQuery = baseQuery.Where(m => m.Kind == normalized);
        }

        if (tags is { Length: > 0 })
            baseQuery = baseQuery.Where(m => m.Tags.Any(t => tags.Contains(t)));

        if (period is not null)
            baseQuery = baseQuery.Where(m => m.OccurredAt >= period.From && m.OccurredAt <= period.To);

        var take = ActivityQueryService.Clamp(limit);

        if (string.IsNullOrWhiteSpace(query))
        {
            var recent = await baseQuery
                .OrderByDescending(m => m.OccurredAt)
                .Take(take)
                .ToListAsync(ct);
            return recent.Select(ToDto).ToList();
        }

        // Three passes, narrowest first. websearch_to_tsquery ANDs its terms, which is precise
        // but brittle in conversation ("failure" does not stem to "failed"), so a miss retries
        // with OR semantics before giving up on the text index entirely.
        var text = query.Trim();
        var matches = await RankedAsync(baseQuery, text, take, ct);

        if (matches.Count == 0)
        {
            var anyTerm = string.Join(" or ", text
                .Split([' ', ',', ';', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2 && !Noise.Contains(t.ToLowerInvariant())));

            if (!string.IsNullOrWhiteSpace(anyTerm) && anyTerm != text)
                matches = await RankedAsync(baseQuery, anyTerm, take, ct);
        }

        if (matches.Count == 0)
        {
            var pattern = $"%{text}%";
            matches = await baseQuery
                .Where(m => EF.Functions.ILike(m.Title, pattern) || EF.Functions.ILike(m.Content, pattern))
                .OrderByDescending(m => m.OccurredAt)
                .Take(take)
                .ToListAsync(ct);
        }

        return matches.Select(ToDto).ToList();
    }

    /// <summary>
    /// Words that would match nearly every memory, dropped from the OR fallback so it does not
    /// degenerate into "return everything".
    /// </summary>
    private static readonly HashSet<string> Noise =
        ["the", "and", "for", "was", "were", "with", "that", "this", "what", "when", "why",
         "how", "did", "any", "about", "from", "into", "work", "or", "not"];

    private static async Task<List<Memory>> RankedAsync(
        IQueryable<Memory> source, string text, int take, CancellationToken ct) =>
        await source
            .Where(m => m.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("english", text)))
            .OrderByDescending(m => m.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery("english", text)))
            .ThenByDescending(m => m.OccurredAt)
            .Take(take)
            .ToListAsync(ct);

    public static string NormalizeKind(string kind)
    {
        var normalized = kind.Trim().ToLowerInvariant().Replace(' ', '_');
        return MemoryKinds.All.Contains(normalized) ? normalized : MemoryKinds.Note;
    }

    public MemoryDto ToDto(Memory m) => new(
        m.Id, m.Kind, m.Title, m.Content, m.Tags,
        periods.LocalDate(m.OccurredAt), m.RelatedRepository, m.RelatedNumber,
        m.Importance, m.RelatedActivityIds, periods.Local(m.CreatedAt));
}
