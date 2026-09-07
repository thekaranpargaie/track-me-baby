using Microsoft.EntityFrameworkCore;
using TrackMeBaby.Data;
using TrackMeBaby.Sync;

namespace TrackMeBaby.Services;

/// <summary>
/// One pull request, assembled from the several activity rows that describe it.
///
/// The activity table is deliberately event-shaped — pr_opened, pr_merged and each commit are
/// separate rows — which is right for "what happened when" but useless for "how long did it take".
/// This is the change-shaped view over the same facts, and the only place that reassembly happens.
/// </summary>
public record ChangeRecord
{
    public required string Repository { get; init; }
    public required int Number { get; init; }
    public required string Title { get; init; }
    public string? Url { get; init; }

    public DateTimeOffset OpenedAt { get; init; }
    public DateTimeOffset? MergedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>
    /// Author date of the earliest commit on the branch — where DORA's lead time for changes
    /// starts. Falls back to the open time when no commit rows survived (a squash-merged branch
    /// whose commits were never authored by the tracked user, for instance).
    /// </summary>
    public DateTimeOffset FirstCommitAt { get; init; }
    public int CommitCount { get; init; }

    public string? BaseRef { get; init; }
    public string? HeadRef { get; init; }
    public string[] Labels { get; init; } = [];
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public int ChangedFiles { get; init; }

    /// <summary>Undoes an earlier change: GitHub's revert button, or a manual rollback.</summary>
    public bool IsRevert { get; init; }
    /// <summary>Came off a hotfix branch — unplanned repair rather than planned work.</summary>
    public bool IsHotfix { get; init; }
    /// <summary>Labelled as repairing a production problem.</summary>
    public bool IsIncident { get; init; }
    /// <summary>PR number this one reverts, when the title or body names it.</summary>
    public int? RevertsNumber { get; init; }

    public string WorkType { get; init; } = WorkTypes.Other;

    public bool IsMerged => MergedAt is not null;

    /// <summary>
    /// Whether the branch's commits are actually on record. When they are not, lead time cannot be
    /// measured — and must not be guessed: a pull request opened and self-merged ten seconds later
    /// would otherwise report a lead time of ten seconds, which describes the merge, not the work.
    /// </summary>
    public bool HasCommitData => CommitCount > 0;
    /// <summary>Anything that exists only because something else broke.</summary>
    public bool IsRestore => IsRevert || IsHotfix || IsIncident;
    public int Churn => Additions + Deletions;

    /// <summary>
    /// Commit -> merge. DORA's lead time for changes, for a merge-triggered environment.
    /// Null when the commits were never collected, because the alternative is a made-up number.
    /// </summary>
    public TimeSpan? LeadTime =>
        MergedAt is null || !HasCommitData ? null : MergedAt.Value - FirstCommitAt;
    /// <summary>Open -> merge. How long the change sat in review, which is the part you control.</summary>
    public TimeSpan? ReviewTime => MergedAt is null ? null : MergedAt.Value - OpenedAt;
}

public static class WorkTypes
{
    public const string Feature = "feature";
    public const string Fix = "fix";
    public const string Maintenance = "maintenance";
    public const string Docs = "docs";
    public const string Tests = "tests";
    public const string Other = "other";

    /// <summary>Chart order, fixed so a category keeps its colour as the mix changes.</summary>
    public static readonly string[] All = [Feature, Fix, Maintenance, Docs, Tests, Other];

    public static string Label(string type) => type switch
    {
        Feature => "Feature",
        Fix => "Fix",
        Maintenance => "Maintenance",
        Docs => "Docs",
        Tests => "Tests",
        _ => "Other"
    };
}

/// <summary>
/// Rebuilds <see cref="ChangeRecord"/>s from stored activity. No GitHub calls, and no new
/// collection: the pull request payload already carries the head and base refs, and commit rows
/// already carry the pull request number they belong to.
/// </summary>
public class ChangeLogService(TrackerDbContext db, IOptions<TrackerOptions> options)
{
    private readonly DeliveryOptions _delivery = options.Value.Delivery;

    /// <summary>
    /// How far before the window to reach for context. A revert merged on day one of the period
    /// usually undoes something from the period before it, and the pair is what makes
    /// time-to-restore meaningful, so the earlier half of it has to be in hand.
    /// </summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(60);

    /// <summary>
    /// Every change that was open, merged or closed anywhere in the window, plus the lookback.
    /// Callers filter to what they need; assembling once keeps the several read paths consistent.
    /// </summary>
    public async Task<List<ChangeRecord>> LoadAsync(Period period, CancellationToken ct = default)
    {
        var from = period.From - Lookback;

        string[] prTypes = [ActivityTypes.PrOpened, ActivityTypes.PrMerged, ActivityTypes.PrClosed];

        var rows = await db.Activities.AsNoTracking()
            .Where(a => a.IsMine && a.Number != null && a.RepositoryFullName != null
                        && prTypes.Contains(a.ActivityType)
                        && a.OccurredAt >= from && a.OccurredAt <= period.To)
            .ToListAsync(ct);

        // A pull request opened before the lookback but merged inside it arrives as a merge row
        // with no open row, and the open row is what holds the branch refs. Fetch the strays.
        var keys = rows.Select(r => (Repository: r.RepositoryFullName!, Number: r.Number!.Value)).ToHashSet();
        var haveOpened = rows.Where(r => r.ActivityType == ActivityTypes.PrOpened)
            .Select(r => (Repository: r.RepositoryFullName!, Number: r.Number!.Value)).ToHashSet();
        var missing = keys.Where(k => !haveOpened.Contains(k)).ToList();

        if (missing.Count > 0)
        {
            // Postgres has no cheap tuple IN through EF, so over-select on the two columns
            // independently and discard the cross-product locally. The numbers involved are small.
            var repositories = missing.Select(m => m.Repository).Distinct().ToList();
            var numbers = missing.Select(m => m.Number).Distinct().ToList();
            var strays = await db.Activities.AsNoTracking()
                .Where(a => a.IsMine && a.ActivityType == ActivityTypes.PrOpened
                            && a.RepositoryFullName != null && a.Number != null
                            && repositories.Contains(a.RepositoryFullName)
                            && numbers.Contains(a.Number.Value))
                .ToListAsync(ct);

            var wanted = missing.ToHashSet();
            rows.AddRange(strays.Where(s => wanted.Contains((s.RepositoryFullName!, s.Number!.Value))));
        }

        var commits = await LoadCommitFactsAsync(from, period.To, ct);

        var changes = new List<ChangeRecord>();
        foreach (var group in rows.GroupBy(r => (Repository: r.RepositoryFullName!, Number: r.Number!.Value)))
        {
            var opened = group.FirstOrDefault(r => r.ActivityType == ActivityTypes.PrOpened);
            var merged = group.FirstOrDefault(r => r.ActivityType == ActivityTypes.PrMerged);
            var closed = group.FirstOrDefault(r => r.ActivityType == ActivityTypes.PrClosed);
            var anchor = opened ?? merged ?? closed;
            if (anchor is null) continue;

            var (baseRef, headRef) = ReadRefs(opened ?? merged);
            var labels = anchor.Labels ?? [];
            var title = anchor.Title ?? $"#{group.Key.Number}";
            var openedAt = opened?.OccurredAt ?? merged?.OccurredAt ?? closed!.OccurredAt;
            var commitFacts = commits.GetValueOrDefault(group.Key);

            var isRevert = IsRevert(title, headRef);
            changes.Add(new ChangeRecord
            {
                Repository = group.Key.Repository,
                Number = group.Key.Number,
                Title = title,
                Url = anchor.Url,
                OpenedAt = openedAt,
                MergedAt = merged?.OccurredAt,
                ClosedAt = closed?.OccurredAt ?? merged?.OccurredAt,
                FirstCommitAt = commitFacts?.First ?? openedAt,
                CommitCount = commitFacts?.Count ?? 0,
                BaseRef = baseRef,
                HeadRef = headRef,
                Labels = labels,
                Additions = merged?.Additions ?? opened?.Additions ?? 0,
                Deletions = merged?.Deletions ?? opened?.Deletions ?? 0,
                ChangedFiles = merged?.ChangedFiles ?? opened?.ChangedFiles ?? 0,
                IsRevert = isRevert,
                IsHotfix = IsHotfix(headRef, title),
                IsIncident = labels.Any(l => _delivery.IncidentLabels
                    .Any(i => l.Contains(i, StringComparison.OrdinalIgnoreCase))),
                RevertsNumber = isRevert ? FindRevertedNumber(anchor.Summary, title) : null,
                WorkType = Classify(title, labels, headRef, isRevert)
            });
        }

        return changes.OrderBy(c => c.OpenedAt).ToList();
    }

    private record CommitFacts(DateTimeOffset First, int Count);

    /// <summary>
    /// Earliest commit per pull request. Commit rows carry the pull request number, so this is a
    /// grouped read rather than another GitHub call.
    /// </summary>
    private async Task<Dictionary<(string Repository, int Number), CommitFacts>> LoadCommitFactsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Reach further back than the window: the first commit of a long-lived branch can predate
        // the merge by months, and it is the start of the lead-time clock.
        var commitFrom = from - TimeSpan.FromDays(120);

        var rows = await db.Activities.AsNoTracking()
            .Where(a => a.IsMine && a.ActivityType == ActivityTypes.Commit
                        && a.Number != null && a.RepositoryFullName != null
                        && a.OccurredAt >= commitFrom && a.OccurredAt <= to)
            .Select(a => new { Repository = a.RepositoryFullName!, Number = a.Number!.Value, a.OccurredAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => (r.Repository, r.Number))
            .ToDictionary(g => g.Key, g => new CommitFacts(g.Min(x => x.OccurredAt), g.Count()));
    }

    /// <summary>Head and base branch, read from the pull request payload the sync already stores.</summary>
    private static (string? BaseRef, string? HeadRef) ReadRefs(Activity? row)
    {
        if (string.IsNullOrWhiteSpace(row?.Payload)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(row.Payload);
            return (document.RootElement.GetStringOrNull("BaseRef"),
                    document.RootElement.GetStringOrNull("HeadRef"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private bool IsRevert(string title, string? headRef)
    {
        if (_delivery.RevertTitlePrefixes.Any(p => title.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return true;
        // GitHub's own revert button creates branches named revert-1234-original-branch.
        return headRef is not null && headRef.StartsWith("revert-", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsHotfix(string? headRef, string title)
    {
        if (headRef is not null && _delivery.HotfixBranchPrefixes
                .Any(p => headRef.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return true;
        return _delivery.HotfixBranchPrefixes
            .Any(p => title.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                      || title.StartsWith($"{p}:", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The reverted pull request number, from "Reverts #123" in the body or "(#123)" in the title.
    /// Knowing it turns time-to-restore from a guess into a measurement.
    /// </summary>
    private static int? FindRevertedNumber(string? body, string title)
    {
        foreach (var text in new[] { body, title })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var match = Regex.Match(text, @"#(\d{1,7})");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var number)) return number;
        }
        return null;
    }

    /// <summary>
    /// Buckets a change by what kind of work it was. Conventional-commit prefixes win when
    /// present because they are explicit; labels and branch names are the fallback.
    /// </summary>
    private static string Classify(string title, string[] labels, string? headRef, bool isRevert)
    {
        if (isRevert) return WorkTypes.Fix;

        var head = title.Split(':')[0].Trim().ToLowerInvariant();
        // Strip a conventional-commit scope: "feat(api)" -> "feat".
        var paren = head.IndexOf('(');
        if (paren > 0) head = head[..paren];
        head = head.TrimEnd('!');

        var byPrefix = head switch
        {
            "feat" or "feature" => WorkTypes.Feature,
            "fix" or "bugfix" or "hotfix" or "bug" => WorkTypes.Fix,
            "chore" or "refactor" or "perf" or "build" or "ci" or "style" or "deps" => WorkTypes.Maintenance,
            "docs" or "doc" => WorkTypes.Docs,
            "test" or "tests" => WorkTypes.Tests,
            _ => null
        };
        if (byPrefix is not null) return byPrefix;

        foreach (var label in labels.Select(l => l.ToLowerInvariant()))
        {
            if (label.Contains("bug") || label.Contains("defect") || label.Contains("regression")) return WorkTypes.Fix;
            if (label.Contains("feature") || label.Contains("enhancement")) return WorkTypes.Feature;
            if (label.Contains("doc")) return WorkTypes.Docs;
            if (label.Contains("test")) return WorkTypes.Tests;
            if (label.Contains("chore") || label.Contains("refactor") || label.Contains("dependencies")
                || label.Contains("maintenance") || label.Contains("tech-debt")) return WorkTypes.Maintenance;
        }

        var branch = headRef?.ToLowerInvariant() ?? "";
        if (branch.StartsWith("feat")) return WorkTypes.Feature;
        if (branch.StartsWith("fix") || branch.StartsWith("bug") || branch.StartsWith("hotfix")) return WorkTypes.Fix;
        if (branch.StartsWith("chore") || branch.StartsWith("refactor")) return WorkTypes.Maintenance;
        if (branch.StartsWith("doc")) return WorkTypes.Docs;
        if (branch.StartsWith("test")) return WorkTypes.Tests;

        return WorkTypes.Other;
    }
}
