using NpgsqlTypes;

namespace TrackMeBaby.Data;

/// <summary>Well-known activity type values. Kept as strings in the database so new
/// sources can introduce their own types without a schema change.</summary>
public static class ActivityTypes
{
    public const string Commit = "commit";
    public const string PrOpened = "pr_opened";
    public const string PrMerged = "pr_merged";
    public const string PrClosed = "pr_closed";
    public const string PrReview = "pr_review";
    public const string PrComment = "pr_comment";
    public const string IssueOpened = "issue_opened";
    public const string IssueClosed = "issue_closed";
    public const string IssueComment = "issue_comment";
    public const string ProjectItemAdded = "project_item_added";
    public const string ProjectItemMoved = "project_item_moved";

    public static readonly string[] All =
    [
        Commit, PrOpened, PrMerged, PrClosed, PrReview, PrComment,
        IssueOpened, IssueClosed, IssueComment, ProjectItemAdded, ProjectItemMoved
    ];

    /// <summary>Types that represent finished work, used by get_my_completed_work.</summary>
    public static readonly string[] Completion = [PrMerged, IssueClosed];
}

public static class MemoryKinds
{
    public const string Decision = "decision";
    public const string Problem = "problem";
    public const string Learning = "learning";
    public const string Accomplishment = "accomplishment";
    public const string Goal = "goal";
    public const string Context = "context";
    public const string Note = "note";

    public static readonly string[] All =
        [Decision, Problem, Learning, Accomplishment, Goal, Context, Note];
}

public static class SyncStatuses
{
    public const string Never = "never_run";
    public const string Running = "running";
    public const string Success = "success";
    public const string Failed = "failed";
}

/// <summary>A system we collect activity from. Only "github" exists in V1; the table
/// exists so Jira/Slack/etc. can be added without touching the activity schema.</summary>
public class Source
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public class Repository
{
    /// <summary>GitHub's numeric repository id.</summary>
    public long Id { get; set; }
    public string FullName { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsPrivate { get; set; }
    public string? Description { get; set; }
    public string? Language { get; set; }
    public string? HtmlUrl { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One factual event. Raw, never edited by Claude. The (Source, SourceId, ActivityType)
/// triple is unique so overlapping/retried syncs upsert instead of duplicating (FR-06).
/// </summary>
public class Activity
{
    public long Id { get; set; }
    public string Source { get; set; } = "github";
    /// <summary>Stable natural key from the source, e.g. "pr:123456" or "commit:abc123".</summary>
    public string SourceId { get; set; } = "";
    public string ActivityType { get; set; } = "";

    public long? RepositoryId { get; set; }
    public string? RepositoryFullName { get; set; }

    /// <summary>Login of whoever performed the event.</summary>
    public string Actor { get; set; } = "";
    /// <summary>True when Actor is the tracked user. Everything the MCP read tools return is mine.</summary>
    public bool IsMine { get; set; }

    /// <summary>When the event actually happened (merge time for a merge, not sync time).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public string? Title { get; set; }
    /// <summary>Body excerpt: commit message body, review body, comment text.</summary>
    public string? Summary { get; set; }
    public string? Url { get; set; }
    /// <summary>PR or issue number, when applicable.</summary>
    public int? Number { get; set; }
    public string? State { get; set; }

    public int? Additions { get; set; }
    public int? Deletions { get; set; }
    public int? ChangedFiles { get; set; }

    public string[] Labels { get; set; } = [];
    public string? Milestone { get; set; }

    /// <summary>Original source JSON, kept so future questions can be answered without a re-sync.</summary>
    public string? Payload { get; set; }

    /// <summary>
    /// The source item's own updated-at. Lets a later sync recognise that nothing has changed and
    /// skip re-fetching the details, which is the difference between a cheap sync and a slow one.
    /// </summary>
    public DateTimeOffset? SourceUpdatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A GitHub Projects V2 board.</summary>
public class Project
{
    /// <summary>GraphQL node id.</summary>
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string OwnerLogin { get; set; } = "";
    /// <summary>"organization" or "user".</summary>
    public string OwnerType { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    public bool Closed { get; set; }
    /// <summary>Node id of the single-select field used as Status, resolved by name.</summary>
    public string? StatusFieldId { get; set; }
    /// <summary>JSON map of status option name -> option id, used by move_ticket.</summary>
    public string? StatusOptions { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }

    public List<ProjectItem> Items { get; set; } = [];
}

public class ProjectItem
{
    /// <summary>GraphQL node id of the project item (not the issue).</summary>
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public Project? Project { get; set; }

    /// <summary>"Issue", "PullRequest" or "DraftIssue".</summary>
    public string ContentType { get; set; } = "";
    public string? ContentNodeId { get; set; }
    public int? ContentNumber { get; set; }
    public string? RepositoryFullName { get; set; }

    public string Title { get; set; } = "";
    public string? Url { get; set; }
    public string? Status { get; set; }
    public string? PreviousStatus { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }
    public string[] Assignees { get; set; } = [];
    public string? AuthorLogin { get; set; }
    public bool IsMine { get; set; }
    public bool IsArchived { get; set; }
    public string? ContentState { get; set; }

    public DateTimeOffset ItemCreatedAt { get; set; }
    public DateTimeOffset ItemUpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Synchronization bookmark per source. LastSuccessfulSync only advances on success (FR-05),
/// so a failed run is retried from the last known-good point on the next cycle.
/// </summary>
public class SyncState
{
    public string Source { get; set; } = "";
    public DateTimeOffset? LastSuccessfulSync { get; set; }
    public DateTimeOffset? LastAttemptedSync { get; set; }
    public string Status { get; set; } = SyncStatuses.Never;
    public string? Error { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int LastItemsProcessed { get; set; }
    public long LastDurationMs { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Context that GitHub cannot tell us: why work mattered, what was learned.</summary>
public class Memory
{
    public long Id { get; set; }
    public string Kind { get; set; } = MemoryKinds.Note;
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string[] Tags { get; set; } = [];
    /// <summary>The date the memory is *about*, which is often not the date it was written.</summary>
    public DateTimeOffset OccurredAt { get; set; }
    public string? RelatedRepository { get; set; }
    public int? RelatedNumber { get; set; }
    public long[] RelatedActivityIds { get; set; } = [];
    /// <summary>1 (minor) to 5 (career-defining). Used to rank appraisal content.</summary>
    public int Importance { get; set; } = 3;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Generated by Postgres from Title + Content; powers search_memory.</summary>
    public NpgsqlTsVector? SearchVector { get; set; }
}

/// <summary>
/// A summary Claude wrote, stored separately from facts so an interpretation is never
/// mistaken for evidence (plan section 12).
/// </summary>
public class PerformanceSnapshot
{
    public long Id { get; set; }
    /// <summary>"weekly", "monthly", "six_month", "custom", ...</summary>
    public string PeriodType { get; set; } = "";
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Markdown written by Claude.</summary>
    public string Content { get; set; } = "";
    /// <summary>The metric block the summary was based on, so it can be audited later.</summary>
    public string? Metrics { get; set; }
    public string CreatedBy { get; set; } = "claude";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A tag or release, collected only because DORA needs to know when a cut happened.
/// Kept out of <see cref="Activity"/> because it is not something the tracked user did —
/// a tag is a state of the repository, and several people's work rides on one.
/// </summary>
public class ReleaseTag
{
    public long Id { get; set; }
    public string RepositoryFullName { get; set; } = "";
    /// <summary>Tag name, e.g. "v2.4.0".</summary>
    public string Name { get; set; } = "";
    public string? Sha { get; set; }
    /// <summary>Commit date of the tagged commit — when the code was written.</summary>
    public DateTimeOffset? CommittedAt { get; set; }
    /// <summary>When the tag or release came into existence. This is the deployment moment.</summary>
    public DateTimeOffset OccurredAt { get; set; }
    /// <summary>True when a GitHub release object exists, not just a git tag.</summary>
    public bool IsRelease { get; set; }
    public bool IsPrerelease { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Append-only log of every write tool invocation (plan section 24).</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public string Tool { get; set; } = "";
    public string? Arguments { get; set; }
    public bool Success { get; set; }
    public string? Result { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
