namespace TrackMeBaby.Services;

public record ActivityDto(
    long Id,
    string Type,
    string? Repository,
    int? Number,
    string? Title,
    string? Details,
    string? Url,
    string At,
    string Date,
    string? State,
    int? Additions,
    int? Deletions,
    int? ChangedFiles,
    string[] Labels);

public record ProjectItemDto(
    string Id,
    string Project,
    int ProjectNumber,
    string Title,
    string ContentType,
    int? Number,
    string? Repository,
    string? Status,
    string? PreviousStatus,
    string? StatusChangedOn,
    string? Url,
    string[] Assignees,
    string? State);

public record ProjectDto(
    string Id,
    int Number,
    string Owner,
    string Title,
    string? Url,
    bool Closed,
    string[] StatusColumns,
    int MyItemCount,
    string? LastSyncedAt);

public record MemoryDto(
    long Id,
    string Kind,
    string Title,
    string Content,
    string[] Tags,
    string OccurredOn,
    string? Repository,
    int? Number,
    int Importance,
    long[] RelatedActivityIds,
    string CreatedAt);

public record RepositoryStatDto(string Repository, int Activities, int Commits, int MergedPullRequests);

public record SnapshotDto(
    long Id,
    string PeriodType,
    string PeriodStart,
    string PeriodEnd,
    string Title,
    string Content,
    string CreatedAt);

public record SyncStatusDto(
    string Source,
    string Status,
    string? LastSuccessfulSync,
    string? LastAttemptedSync,
    string? Error,
    int ConsecutiveFailures,
    int LastItemsProcessed,
    long LastDurationMs,
    bool IsRunningNow,
    string? NextScheduledRun,
    long TotalActivities,
    string? OldestActivity,
    string? NewestActivity);

public record PerformanceMetrics(
    int Commits,
    int PullRequestsOpened,
    int PullRequestsMerged,
    int PullRequestsClosedWithoutMerge,
    int ReviewsGiven,
    int PullRequestComments,
    int IssuesOpened,
    int IssuesClosed,
    int IssueComments,
    int BoardItemsAdded,
    int BoardItemsMoved,
    int LinesAdded,
    int LinesRemoved,
    int FilesChanged,
    int RepositoriesTouched,
    int ActiveDays,
    string? BusiestDay,
    int TotalActivities);

public record PerformanceReport(
    string Period,
    string From,
    string To,
    PerformanceMetrics Metrics,
    PerformanceMetrics PreviousPeriodMetrics,
    string PreviousPeriodRange,
    Dictionary<string, int> ActivityByType,
    Dictionary<string, int> ActivityByWeek,
    List<RepositoryStatDto> Repositories,
    List<ActivityDto> MergedPullRequests,
    List<ActivityDto> ReviewsGiven,
    List<ActivityDto> IssuesClosed,
    List<ProjectItemDto> BoardWorkCompleted,
    List<ProjectItemDto> BoardWorkInProgress,
    List<ActivityDto> LargestChanges,
    List<MemoryDto> Memories,
    List<SnapshotDto> ExistingSnapshots,
    List<string> Notes);
