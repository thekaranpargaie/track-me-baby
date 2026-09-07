namespace TrackMeBaby;

public class TrackerOptions
{
    public const string SectionName = "Tracker";

    public GitHubOptions GitHub { get; set; } = new();
    public SyncOptions Sync { get; set; } = new();
    public DeliveryOptions Delivery { get; set; } = new();
    public ReportOptions Reports { get; set; } = new();

    /// <summary>
    /// Windows time zone id (e.g. "India Standard Time") or IANA id. Relative periods like
    /// "last_week" are resolved against this, so "yesterday" means your yesterday.
    /// Empty = the machine's local zone.
    /// </summary>
    public string TimeZone { get; set; } = "";
}

public class GitHubOptions
{
    /// <summary>Personal access token. Put this in appsettings.Local.json or the GITHUB_TOKEN env var.</summary>
    public string Token { get; set; } = "";

    /// <summary>Your GitHub login. Left empty, it is resolved once from the token and cached.</summary>
    public string Login { get; set; } = "";

    /// <summary>Restrict search to these orgs/users. Empty = everything the token can see.</summary>
    public List<string> Organizations { get; set; } = [];

    /// <summary>Optional allow-list of "owner/repo". Empty = no repository filtering.</summary>
    public List<string> IncludeRepositories { get; set; } = [];

    /// <summary>Optional deny-list of "owner/repo", applied after IncludeRepositories.</summary>
    public List<string> ExcludeRepositories { get; set; } = [];

    /// <summary>How far back the very first sync reaches.</summary>
    public int InitialBackfillDays { get; set; } = 180;

    /// <summary>
    /// Re-request this many minutes before the last successful sync. GitHub's "updated"
    /// index lags slightly; upserts make the overlap harmless (plan section 8).
    /// </summary>
    public int OverlapMinutes { get; set; } = 10;

    /// <summary>Search windows are sliced this small so no single query hits the 1000-result cap.</summary>
    public int MaxWindowDays { get; set; } = 7;

    /// <summary>Fetch issue/PR comments. Costs one extra API call per changed item.</summary>
    public bool SyncComments { get; set; } = true;

    /// <summary>Sync GitHub Projects V2 boards (needs read:project on the token).</summary>
    public bool SyncProjects { get; set; } = true;

    /// <summary>
    /// Boards to track as "owner/number" (e.g. "my-org/7"). Empty = auto-discover every
    /// board owned by you and by each entry in Organizations.
    /// </summary>
    public List<string> Projects { get; set; } = [];

    /// <summary>Name of the single-select field treated as the board column.</summary>
    public string StatusFieldName { get; set; } = "Status";

    /// <summary>Repository used by create_ticket when the caller does not name one.</summary>
    public string DefaultRepository { get; set; } = "";

    /// <summary>Board that create_ticket adds new issues to, as "owner/number".</summary>
    public string DefaultProject { get; set; } = "";
}

/// <summary>
/// How "a deployment" is recognised, which is the one thing DORA cannot be computed without.
/// Every team means something different by it, and often something different per environment,
/// so each environment declares its own trigger rather than the system assuming one.
/// </summary>
public class DeliveryOptions
{
    // Every list below starts empty and is filled by ApplyDefaults when configuration leaves it
    // empty. Initialising them inline would be worse than useless: the configuration binder *adds*
    // to an existing collection rather than replacing it, so a list declared in appsettings would
    // arrive appended to the defaults — three configured environments on top of three built-in ones,
    // each reported twice.

    /// <summary>
    /// The environment ladder, promoted-to last. Left unset it describes the common
    /// commit -> merge -> tag arrangement; declare the list to match your own pipeline.
    /// </summary>
    public List<EnvironmentRule> Environments { get; set; } = [];

    /// <summary>
    /// Head-branch prefixes that mark a change as unplanned repair work. Matched
    /// case-insensitively against the PR's head ref, so "hotfix/checkout-500" counts.
    /// </summary>
    public List<string> HotfixBranchPrefixes { get; set; } = [];

    /// <summary>
    /// Title prefixes that mark a restore. GitHub's own revert button produces
    /// <c>Revert "original title"</c>, which is why that is the first default.
    /// </summary>
    public List<string> RevertTitlePrefixes { get; set; } = [];

    /// <summary>Labels that mark a change as repairing a production problem.</summary>
    public List<string> IncidentLabels { get; set; } = [];

    /// <summary>Collect tags and releases. Needed only by tag/release triggers; one call per active repo.</summary>
    public bool SyncTags { get; set; } = true;

    /// <summary>Most recent tags to read per repository. 100 is one GraphQL page.</summary>
    public int TagsPerRepository { get; set; } = 100;

    /// <summary>Repositories to read tags from. Empty = every repository with activity in the window.</summary>
    public List<string> TagRepositories { get; set; } = [];

    /// <summary>
    /// Fills in what configuration left empty. Called once at startup through PostConfigure, so
    /// every reader sees a populated instance and none of them has to know about the fallback.
    /// </summary>
    public void ApplyDefaults()
    {
        if (Environments.Count == 0)
            Environments =
            [
                new() { Name = "dev", Trigger = DeployTriggers.Commit },
                new() { Name = "qa", Trigger = DeployTriggers.Merge, Branches = ["main", "master"] },
                new() { Name = "stage", Trigger = DeployTriggers.Tag, TagPattern = "*" }
            ];

        if (HotfixBranchPrefixes.Count == 0)
            HotfixBranchPrefixes = ["hotfix", "hotfixes", "revert-"];

        if (RevertTitlePrefixes.Count == 0)
            RevertTitlePrefixes = ["revert \"", "revert:", "revert ", "rollback"];

        if (IncidentLabels.Count == 0)
            IncidentLabels = ["incident", "hotfix", "sev1", "sev2", "p0", "p1", "production", "outage"];

        foreach (var rule in Environments.Where(e => string.IsNullOrWhiteSpace(e.Trigger)))
            rule.Trigger = DeployTriggers.Merge;
    }
}

public static class DeployTriggers
{
    /// <summary>Every commit is a deployment to this environment (continuous deployment from push).</summary>
    public const string Commit = "commit";
    /// <summary>A pull request merged into one of <see cref="EnvironmentRule.Branches"/>.</summary>
    public const string Merge = "merge";
    /// <summary>A git tag was cut.</summary>
    public const string Tag = "tag";
    /// <summary>A GitHub release was published.</summary>
    public const string Release = "release";

    public static readonly string[] All = [Commit, Merge, Tag, Release];
}

public class EnvironmentRule
{
    /// <summary>Display name: dev, qa, stage, production.</summary>
    public string Name { get; set; } = "";

    /// <summary>One of <see cref="DeployTriggers"/>.</summary>
    public string Trigger { get; set; } = DeployTriggers.Merge;

    /// <summary>Target branches for the merge trigger. Empty = any branch.</summary>
    public List<string> Branches { get; set; } = [];

    /// <summary>Glob for the tag/release trigger, e.g. "v*" or "release-*". Empty or "*" = every tag.</summary>
    public string TagPattern { get; set; } = "";

    /// <summary>Restrict this environment to these "owner/repo" entries. Empty = all.</summary>
    public List<string> Repositories { get; set; } = [];
}

public class ReportOptions
{
    /// <summary>
    /// Where export_report writes files. Relative paths resolve against the content root;
    /// in Docker this is a mounted volume so the files land on the host.
    /// </summary>
    public string Directory { get; set; } = "exports";

    /// <summary>Your name, used on the cover of exported reports. Empty = the GitHub login.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Optional line under the name on exported reports, e.g. "Senior Engineer, Platform".</summary>
    public string Role { get; set; } = "";
}

public class SyncOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 60;
    /// <summary>Sync immediately at startup rather than waiting a full interval.</summary>
    public bool RunOnStartup { get; set; } = true;
    /// <summary>Delay before the startup sync, giving the database time to accept connections.</summary>
    public int StartupDelaySeconds { get; set; } = 10;
}
