using Microsoft.EntityFrameworkCore;
using TrackMeBaby.Data;

namespace TrackMeBaby.Services;

/// <summary>
/// The four DORA metrics, computed per environment.
///
/// "A deployment" means something different at every gate — a commit reaching dev is not a tag
/// reaching stage — so each environment declares its own trigger in configuration and gets its own
/// figures. Computing one blended number across a ladder like that would be arithmetic without
/// meaning.
///
/// Two honest limits travel with the output, and are surfaced wherever it is displayed:
/// the DORA bands are team benchmarks applied here to one person's slice of the work, and a
/// deployment is only ever inferred from git, never observed from a deploy pipeline.
/// </summary>
public class DeliveryService(
    TrackerDbContext db,
    ChangeLogService changeLog,
    PeriodResolver periods,
    IOptions<TrackerOptions> options)
{
    private readonly DeliveryOptions _delivery = options.Value.Delivery;

    /// <summary>A single deployment, whatever the trigger decided that means.</summary>
    private record Deployment
    {
        public required string Repository { get; init; }
        public required string Reference { get; init; }
        public string? Title { get; init; }
        public string? Url { get; init; }
        public required DateTimeOffset At { get; init; }
        /// <summary>Changes riding on this deployment, earliest commit first.</summary>
        public List<ChangeRecord> Changes { get; init; } = [];
        /// <summary>This deployment itself carried repair work.</summary>
        public bool Restore { get; set; }
        /// <summary>Something after this deployment had to undo or repair it.</summary>
        public bool Failed { get; set; }
    }

    public async Task<DeliveryReport> BuildAsync(Period period, int recentLimit = 15, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var changes = await changeLog.LoadAsync(period, ct);
        var tags = await LoadTagsAsync(period, ct);
        // Loaded once for every environment rather than per rule: the commit-triggered
        // environments all read the same rows and only differ by repository filter.
        var commits = _delivery.Environments.Any(e => e.Trigger == DeployTriggers.Commit)
            ? await CommitRowsAsync(period, ct)
            : [];

        if (_delivery.Environments.Count == 0)
            notes.Add("No environments configured. Set Tracker:Delivery:Environments so DORA knows what a deployment is.");

        var reports = new List<EnvironmentReport>();
        foreach (var rule in _delivery.Environments)
            reports.Add(BuildEnvironment(rule, period, changes, tags, commits, recentLimit));

        notes.Add("DORA bands are published team-level benchmarks. Applied to one person's work they place "
                  + "your delivery pattern in context; they do not score you against your team.");
        notes.Add("Deployments are inferred from git activity, not read from a deploy pipeline, so they are "
                  + "as accurate as the configured triggers.");

        return new DeliveryReport(
            period.Label, period.From.ToString("u"), period.To.ToString("u"), reports, notes);
    }

    private async Task<List<ReleaseTag>> LoadTagsAsync(Period period, CancellationToken ct) =>
        await db.ReleaseTags.AsNoTracking()
            .Where(t => t.OccurredAt >= period.From - ChangeLogService.Lookback && t.OccurredAt <= period.To)
            .OrderBy(t => t.OccurredAt)
            .ToListAsync(ct);

    private EnvironmentReport BuildEnvironment(
        EnvironmentRule rule, Period period, List<ChangeRecord> changes, List<ReleaseTag> tags,
        List<CommitRow> commits, int recentLimit)
    {
        var notes = new List<string>();
        var deployments = rule.Trigger switch
        {
            DeployTriggers.Commit => FromCommits(rule, changes, commits),
            DeployTriggers.Merge => FromMerges(rule, changes, notes),
            DeployTriggers.Tag => FromTags(rule, tags, changes, releasesOnly: false, notes),
            DeployTriggers.Release => FromTags(rule, tags, changes, releasesOnly: true, notes),
            _ => Unknown(rule, notes)
        };

        MarkFailures(deployments);

        // Only deployments inside the window are reported; the ones before it exist so that a
        // restore early in the period has something to point back at.
        var inWindow = deployments.Where(d => d.At >= period.From && d.At <= period.To)
            .OrderBy(d => d.At).ToList();

        var days = Math.Max((period.To - period.From).TotalDays, 1);
        var perDay = inWindow.Count / days;

        // Only changes whose commits are on record can contribute a lead time. Falling back to the
        // open time would report a self-merged pull request as shipping in seconds.
        var measurable = inWindow.SelectMany(d => d.Changes.Where(c => c.HasCommitData).Select(c => d.At - c.FirstCommitAt))
            .Where(t => t > TimeSpan.Zero).ToList();
        var leadTimes = rule.Trigger == DeployTriggers.Commit ? [] : measurable;

        if (rule.Trigger == DeployTriggers.Commit)
            notes.Add("Lead time is not meaningful when the commit is itself the deployment — there is no "
                      + "interval between writing the code and shipping it to this environment.");
        else
        {
            var carried = inWindow.Sum(d => d.Changes.Count);
            var unmeasurable = carried - inWindow.Sum(d => d.Changes.Count(c => c.HasCommitData));
            if (unmeasurable > 0)
                notes.Add($"{unmeasurable} of {carried} changes carried by these deployments have no commits on "
                          + "record, so they are excluded from lead time rather than counted as instant.");
        }

        var failed = inWindow.Count(d => d.Failed);
        double? changeFailureRate = inWindow.Count == 0 ? null : (double)failed / inWindow.Count;

        var restoreTimes = RestoreTimes(deployments, period);

        var leadStats = Stats.Durations(leadTimes);
        var restoreStats = Stats.Durations(restoreTimes);

        if (inWindow.Count == 0)
            notes.Add(rule.Trigger switch
            {
                DeployTriggers.Tag => "No tags found in this period. Tag collection needs one sync after "
                                      + "Tracker:Delivery:SyncTags was enabled; check the tag pattern too.",
                DeployTriggers.Release => "No published releases found in this period.",
                DeployTriggers.Merge => "No merges into "
                                        + (rule.Branches.Count == 0 ? "any branch" : string.Join(", ", rule.Branches))
                                        + " in this period.",
                _ => "No deployments found in this period."
            });

        return new EnvironmentReport(
            rule.Name,
            rule.Trigger,
            Describe(rule),
            inWindow.Count,
            perDay * 7,
            perDay,
            Stats.FrequencyLabel(perDay),
            Stats.FrequencyBand(perDay, inWindow.Count),
            leadStats,
            rule.Trigger == DeployTriggers.Commit ? DoraBand.Unknown : Stats.LeadTimeBand(leadStats.MedianHours),
            failed,
            changeFailureRate,
            Stats.ChangeFailureBand(changeFailureRate),
            restoreStats,
            Stats.RestoreBand(restoreStats.MedianHours),
            Weekly(inWindow, period),
            inWindow.OrderByDescending(d => d.At).Take(recentLimit).Select(d => ToDto(rule.Name, d)).ToList(),
            notes);
    }

    private static List<Deployment> Unknown(EnvironmentRule rule, List<string> notes)
    {
        notes.Add($"Unknown trigger '{rule.Trigger}'. Use one of: {string.Join(", ", DeployTriggers.All)}.");
        return [];
    }

    /// <summary>
    /// Continuous deployment from push: every commit is a deployment. Commits are grouped by the
    /// hour so a burst of six pushes in ten minutes is not counted as six separate deployments,
    /// which would flatter the frequency figure.
    /// </summary>
    private static List<Deployment> FromCommits(
        EnvironmentRule rule, List<ChangeRecord> changes, List<CommitRow> commits)
    {
        var byChange = changes.ToDictionary(c => (c.Repository, c.Number));

        return commits
            .Where(c => Allowed(rule, c.Repository))
            .GroupBy(c => (c.Repository, Hour: new DateTimeOffset(
                c.At.UtcDateTime.Date.AddHours(c.At.UtcDateTime.Hour), TimeSpan.Zero)))
            .Select(g =>
            {
                var last = g.OrderBy(c => c.At).Last();
                var related = g.Select(c => c.Number is null
                        ? null
                        : byChange.GetValueOrDefault((c.Repository, c.Number.Value)))
                    .Where(c => c is not null).Select(c => c!).Distinct().ToList();

                return new Deployment
                {
                    Repository = g.Key.Repository,
                    Reference = g.Count() == 1 ? Short(last.Sha) : $"{g.Count()} commits",
                    Title = last.Title,
                    Url = last.Url,
                    At = last.At,
                    Changes = related,
                    Restore = related.Any(c => c.IsRestore) || g.Any(c => LooksLikeRevert(c.Title))
                };
            })
            .OrderBy(d => d.At)
            .ToList();
    }

    private record CommitRow(string Repository, string? Sha, string? Title, string? Url, DateTimeOffset At, int? Number);

    private async Task<List<CommitRow>> CommitRowsAsync(Period period, CancellationToken ct)
    {
        var from = period.From - ChangeLogService.Lookback;
        var rows = await db.Activities.AsNoTracking()
            .Where(a => a.IsMine && a.ActivityType == ActivityTypes.Commit
                        && a.RepositoryFullName != null
                        && a.OccurredAt >= from && a.OccurredAt <= period.To)
            .Select(a => new { a.RepositoryFullName, a.SourceId, a.Title, a.Url, a.OccurredAt, a.Number })
            .ToListAsync(ct);

        return rows
            .Select(r => new CommitRow(
                r.RepositoryFullName!,
                r.SourceId.StartsWith("commit:") ? r.SourceId[7..] : r.SourceId,
                r.Title, r.Url, r.OccurredAt, r.Number))
            .ToList();
    }

    /// <summary>One merge into a target branch is one deployment.</summary>
    private List<Deployment> FromMerges(EnvironmentRule rule, List<ChangeRecord> changes, List<string> notes)
    {
        var matched = changes
            .Where(c => c.IsMerged && Allowed(rule, c.Repository) && MatchesBranch(rule, c.BaseRef))
            .ToList();

        var unknownBase = matched.Count(c => c.BaseRef is null);
        if (rule.Branches.Count > 0 && unknownBase > 0)
            notes.Add($"{unknownBase} merge(s) had no recorded base branch and were counted anyway; "
                      + "they were collected before branch refs were stored.");

        return matched
            .Select(c => new Deployment
            {
                Repository = c.Repository,
                Reference = $"#{c.Number}",
                Title = c.Title,
                Url = c.Url,
                At = c.MergedAt!.Value,
                Changes = [c],
                Restore = c.IsRestore
            })
            .OrderBy(d => d.At)
            .ToList();
    }

    /// <summary>
    /// A tag cut is one deployment, carrying every change merged since the previous cut in the
    /// same repository. That attribution is what lets lead time span the wait for a release.
    /// </summary>
    private List<Deployment> FromTags(
        EnvironmentRule rule, List<ReleaseTag> tags, List<ChangeRecord> changes, bool releasesOnly, List<string> notes)
    {
        var matched = tags
            .Where(t => Allowed(rule, t.RepositoryFullName))
            .Where(t => !releasesOnly || t.IsRelease)
            .Where(t => MatchesPattern(rule.TagPattern, t.Name))
            .OrderBy(t => t.OccurredAt)
            .ToList();

        if (!_delivery.SyncTags)
            notes.Add("Tracker:Delivery:SyncTags is false, so no tags are being collected for this environment.");

        var merges = changes.Where(c => c.IsMerged).OrderBy(c => c.MergedAt).ToList();
        var deployments = new List<Deployment>();

        foreach (var group in matched.GroupBy(t => t.RepositoryFullName))
        {
            DateTimeOffset? previous = null;
            foreach (var tag in group)
            {
                var carried = merges
                    .Where(c => c.Repository == group.Key
                                && c.MergedAt <= tag.OccurredAt
                                && (previous is null || c.MergedAt > previous))
                    .ToList();

                deployments.Add(new Deployment
                {
                    Repository = group.Key,
                    Reference = tag.Name,
                    Title = carried.Count switch
                    {
                        0 => "no changes of yours in this cut",
                        1 => carried[0].Title,
                        _ => $"{carried.Count} of your changes"
                    },
                    Url = tag.Url,
                    At = tag.OccurredAt,
                    Changes = carried,
                    Restore = carried.Any(c => c.IsRestore)
                });

                previous = tag.OccurredAt;
            }
        }

        return deployments.OrderBy(d => d.At).ToList();
    }

    /// <summary>
    /// Marks the deployments that had to be repaired. A restore points at the deployment that
    /// carried the change it undoes when the revert names a pull request number; otherwise at the
    /// deployment immediately before it, which is the best available guess.
    /// </summary>
    private static void MarkFailures(List<Deployment> deployments)
    {
        var ordered = deployments.OrderBy(d => d.At).ToList();

        foreach (var restore in ordered.Where(d => d.Restore))
        {
            var target = FindCause(ordered, restore);
            if (target is not null) target.Failed = true;
        }
    }

    /// <summary>The deployment a restore was undoing, or null when nothing plausible precedes it.</summary>
    private static Deployment? FindCause(List<Deployment> ordered, Deployment restore)
    {
        var named = restore.Changes
            .Select(c => c.RevertsNumber)
            .Where(n => n is not null)
            .Select(n => n!.Value)
            .ToHashSet();

        if (named.Count > 0)
        {
            var carrying = ordered.LastOrDefault(d =>
                d.At < restore.At && d.Changes.Any(c => named.Contains(c.Number)));
            if (carrying is not null) return carrying;
        }

        return ordered.LastOrDefault(d => d.At < restore.At && !ReferenceEquals(d, restore));
    }

    /// <summary>
    /// Time to restore service: from the failed deployment to the one that repaired it. Only pairs
    /// whose repair landed inside the window are counted, so the figure describes the period.
    /// </summary>
    private static List<TimeSpan> RestoreTimes(List<Deployment> deployments, Period period)
    {
        var ordered = deployments.OrderBy(d => d.At).ToList();
        var times = new List<TimeSpan>();

        foreach (var restore in ordered.Where(d => d.Restore && d.At >= period.From && d.At <= period.To))
        {
            var cause = FindCause(ordered, restore);
            if (cause is null) continue;
            var elapsed = restore.At - cause.At;
            if (elapsed > TimeSpan.Zero) times.Add(elapsed);
        }

        return times;
    }

    private List<TimeBucket> Weekly(List<Deployment> deployments, Period period)
    {
        var buckets = new List<TimeBucket>();
        var cursor = StartOfWeek(periods.ToLocalTime(period.From));
        var end = periods.ToLocalTime(period.To);

        while (cursor <= end)
        {
            var next = cursor.AddDays(7);
            var count = deployments.Count(d =>
            {
                var local = periods.ToLocalTime(d.At);
                return local >= cursor && local < next;
            });
            buckets.Add(new TimeBucket($"{cursor:d MMM}", cursor.ToString("yyyy-MM-dd"), count));
            cursor = next;
        }

        return buckets;
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset value)
    {
        var date = value.Date.AddDays(-(((int)value.DayOfWeek + 6) % 7));
        return new DateTimeOffset(date, value.Offset);
    }

    private DeployEventDto ToDto(string environment, Deployment d)
    {
        var leadTimes = d.Changes.Where(c => c.HasCommitData)
            .Select(c => (d.At - c.FirstCommitAt).TotalHours).Where(h => h > 0).ToList();
        return new DeployEventDto(
            environment,
            d.Repository,
            d.Reference,
            d.Title,
            d.Url,
            d.At.ToString("u"),
            periods.LocalDate(d.At),
            d.Changes.Count,
            leadTimes.Count == 0 ? null : leadTimes.Average(),
            d.Failed,
            d.Restore);
    }

    private static bool Allowed(EnvironmentRule rule, string repository) =>
        rule.Repositories.Count == 0
        || rule.Repositories.Any(r => string.Equals(r, repository, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesBranch(EnvironmentRule rule, string? baseRef)
    {
        if (rule.Branches.Count == 0) return true;
        // A merge with no recorded base branch is counted rather than silently dropped; the note
        // on the environment says how many.
        if (baseRef is null) return true;
        return rule.Branches.Any(b => string.Equals(b, baseRef, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Glob matching for tag patterns — "v*", "release-*", "*" or empty for everything.</summary>
    internal static bool MatchesPattern(string? pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return true;
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeRevert(string? title) =>
        title is not null && title.StartsWith("revert", StringComparison.OrdinalIgnoreCase);

    private static string Short(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? "commit" : sha.Length <= 7 ? sha : sha[..7];

    private static string Describe(EnvironmentRule rule) => rule.Trigger switch
    {
        DeployTriggers.Commit => "every commit (grouped by hour)",
        DeployTriggers.Merge => rule.Branches.Count == 0
            ? "any pull request merge"
            : $"merge into {string.Join(" or ", rule.Branches)}",
        DeployTriggers.Tag => string.IsNullOrWhiteSpace(rule.TagPattern) || rule.TagPattern == "*"
            ? "any tag cut"
            : $"tag matching {rule.TagPattern}",
        DeployTriggers.Release => "published release",
        _ => rule.Trigger
    };
}
