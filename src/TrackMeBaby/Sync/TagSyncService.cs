using Microsoft.EntityFrameworkCore;
using TrackMeBaby.Data;
using TrackMeBaby.GitHub;

namespace TrackMeBaby.Sync;

/// <summary>
/// Collects tags and releases, which is the only thing DORA needs that the activity sync does not
/// already hold. A tag is not something the user *did*, so it does not become an activity — it is a
/// state of the repository that a deployment rule can point at.
///
/// One GraphQL call per repository per sync, and only for repositories the user has actually touched,
/// so this costs a couple of dozen calls an hour against a 5000/hour budget.
/// </summary>
public class TagSyncService(
    TrackerDbContext db,
    GitHubAccess github,
    IOptions<TrackerOptions> options,
    ILogger<TagSyncService> logger)
{
    private readonly DeliveryOptions _delivery = options.Value.Delivery;

    /// <summary>
    /// Reads the most recent tags for every repository that a tag- or release-triggered
    /// environment could care about. Returns the number of tags written or updated.
    /// </summary>
    public async Task<int> SyncAsync(List<string> notes, CancellationToken ct = default)
    {
        if (!_delivery.SyncTags) return 0;

        var wantsTags = _delivery.Environments.Any(e =>
            e.Trigger is DeployTriggers.Tag or DeployTriggers.Release);
        if (!wantsTags) return 0;

        var repositories = await ResolveRepositoriesAsync(ct);
        if (repositories.Count == 0) return 0;

        var count = 0;
        foreach (var fullName in repositories)
        {
            ct.ThrowIfCancellationRequested();
            var location = RepoLocation.FromFullName(fullName);
            if (location is null) continue;

            try
            {
                count += await SyncRepositoryAsync(location, ct);
            }
            catch (GitHubGraphQlException ex)
            {
                // A repository we can read activity for but not refs (or one that has since been
                // archived) must not take the whole sync down with it.
                logger.LogWarning("Could not read tags for {Repository}: {Message}", fullName, ex.Message);
                notes.Add($"Tags unavailable for {fullName}: {ex.Message}");
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Tag sync touched {Count} tags across {Repositories} repositories",
            count, repositories.Count);
        return count;
    }

    /// <summary>
    /// Which repositories to ask about: the explicit list if configured, otherwise every
    /// repository the tag-triggered environments allow that has a merge on record.
    /// </summary>
    private async Task<List<string>> ResolveRepositoriesAsync(CancellationToken ct)
    {
        if (_delivery.TagRepositories.Count > 0)
            return _delivery.TagRepositories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var scoped = _delivery.Environments
            .Where(e => e.Trigger is DeployTriggers.Tag or DeployTriggers.Release)
            .SelectMany(e => e.Repositories)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (scoped.Count > 0) return scoped;

        return await db.Activities.AsNoTracking()
            .Where(a => a.IsMine && a.RepositoryFullName != null
                        && a.ActivityType == ActivityTypes.PrMerged)
            .Select(a => a.RepositoryFullName!)
            .Distinct()
            .ToListAsync(ct);
    }

    private const string TagQuery = """
        query($owner: String!, $name: String!, $count: Int!) {
          repository(owner: $owner, name: $name) {
            refs(refPrefix: "refs/tags/", first: $count,
                 orderBy: {field: TAG_COMMIT_DATE, direction: DESC}) {
              nodes {
                name
                target {
                  __typename
                  ... on Commit { oid committedDate }
                  ... on Tag {
                    tagger { date }
                    target { ... on Commit { oid committedDate } }
                  }
                }
              }
            }
            releases(first: $count, orderBy: {field: CREATED_AT, direction: DESC}) {
              nodes { tagName name publishedAt createdAt isPrerelease isDraft url }
            }
          }
        }
        """;

    private async Task<int> SyncRepositoryAsync(RepoLocation location, CancellationToken ct)
    {
        var count = Math.Clamp(_delivery.TagsPerRepository, 1, 100);
        var data = await github.GraphQlAsync(
            TagQuery, new { owner = location.Owner, name = location.Name, count }, ct);

        if (!data.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.Object)
            return 0;

        var existing = await db.ReleaseTags
            .Where(t => t.RepositoryFullName == location.FullName)
            .ToDictionaryAsync(t => t.Name, StringComparer.Ordinal, ct);

        // Releases first, so a tag that also has a release keeps the release's publish time —
        // the moment a human pressed publish is closer to "deployed" than the commit date.
        var releases = new Dictionary<string, (DateTimeOffset When, bool Prerelease, string? Url)>(StringComparer.Ordinal);
        if (repository.TryGetProperty("releases", out var releaseNode)
            && releaseNode.TryGetProperty("nodes", out var releaseNodes))
        {
            foreach (var node in releaseNodes.EnumerateArray())
            {
                if (node.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                var tagName = node.GetStringOrNull("tagName");
                if (tagName is null) continue;
                var when = node.GetDateOrNull("publishedAt") ?? node.GetDateOrNull("createdAt");
                if (when is null) continue;
                releases[tagName] = (
                    when.Value,
                    node.TryGetProperty("isPrerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                    node.GetStringOrNull("url"));
            }
        }

        var touched = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (repository.TryGetProperty("refs", out var refs) && refs.TryGetProperty("nodes", out var refNodes))
        {
            foreach (var node in refNodes.EnumerateArray())
            {
                var name = node.GetStringOrNull("name");
                if (name is null) continue;
                seen.Add(name);

                var (sha, committedAt, taggedAt) = ReadTarget(node);
                var release = releases.TryGetValue(name, out var r) ? r : default;
                var hasRelease = releases.ContainsKey(name);

                // Lightweight tags carry no creation timestamp of their own — GitHub simply does not
                // record one — so the tagged commit's date stands in. Annotated tags and releases do
                // carry a real cut time, and it wins when present.
                var occurredAt = hasRelease ? release.When : taggedAt ?? committedAt;
                if (occurredAt is null) continue;

                touched += Upsert(existing, location.FullName, name, sha, committedAt, occurredAt.Value,
                    hasRelease, hasRelease && release.Prerelease,
                    release.Url ?? $"https://github.com/{location.FullName}/releases/tag/{Uri.EscapeDataString(name)}");
            }
        }

        // A release whose tag fell outside the refs page still counts as a deployment.
        foreach (var (tagName, release) in releases.Where(kv => !seen.Contains(kv.Key)))
            touched += Upsert(existing, location.FullName, tagName, null, null, release.When,
                true, release.Prerelease, release.Url);

        return touched;
    }

    /// <summary>
    /// Unwraps the two shapes a tag can point at: a lightweight tag targets the commit directly,
    /// an annotated tag targets a Tag object which in turn targets the commit.
    /// </summary>
    private static (string? Sha, DateTimeOffset? CommittedAt, DateTimeOffset? TaggedAt) ReadTarget(JsonElement node)
    {
        if (!node.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object)
            return (null, null, null);

        var kind = target.GetStringOrNull("__typename");
        if (kind == "Tag")
        {
            var taggedAt = target.TryGetProperty("tagger", out var tagger) ? tagger.GetDateOrNull("date") : null;
            var inner = target.TryGetProperty("target", out var innerTarget) && innerTarget.ValueKind == JsonValueKind.Object
                ? innerTarget
                : default;
            return (inner.ValueKind == JsonValueKind.Object ? inner.GetStringOrNull("oid") : null,
                    inner.ValueKind == JsonValueKind.Object ? inner.GetDateOrNull("committedDate") : null,
                    taggedAt);
        }

        return (target.GetStringOrNull("oid"), target.GetDateOrNull("committedDate"), null);
    }

    private int Upsert(
        Dictionary<string, ReleaseTag> existing, string repository, string name, string? sha,
        DateTimeOffset? committedAt, DateTimeOffset occurredAt, bool isRelease, bool isPrerelease, string? url)
    {
        var now = DateTimeOffset.UtcNow;
        if (existing.TryGetValue(name, out var row))
        {
            var changed = row.OccurredAt != occurredAt || row.IsRelease != isRelease
                          || row.IsPrerelease != isPrerelease
                          || (sha is not null && row.Sha != sha);
            if (!changed) return 0;

            row.Sha = sha ?? row.Sha;
            row.CommittedAt = committedAt ?? row.CommittedAt;
            row.OccurredAt = occurredAt;
            row.IsRelease = isRelease;
            row.IsPrerelease = isPrerelease;
            row.Url = url ?? row.Url;
            row.UpdatedAt = now;
            return 1;
        }

        var created = new ReleaseTag
        {
            RepositoryFullName = repository,
            Name = name,
            Sha = sha,
            CommittedAt = committedAt,
            OccurredAt = occurredAt,
            IsRelease = isRelease,
            IsPrerelease = isPrerelease,
            Url = url,
            UpdatedAt = now
        };
        db.ReleaseTags.Add(created);
        existing[name] = created;
        return 1;
    }
}
