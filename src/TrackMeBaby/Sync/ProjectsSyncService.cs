using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TrackMeBaby.Data;
using TrackMeBaby.GitHub;

namespace TrackMeBaby.Sync;

/// <summary>
/// Syncs GitHub Projects V2 boards, which are GraphQL-only.
///
/// GitHub does not expose a "card moved" event we can poll cheaply, so status changes are
/// detected by diffing the stored status against the current one. That turns each board
/// column change into a project_item_moved activity with a timestamp.
/// </summary>
public class ProjectsSyncService(
    TrackerDbContext db,
    GitHubAccess github,
    IOptions<TrackerOptions> options,
    ILogger<ProjectsSyncService> logger)
{
    private readonly GitHubOptions _options = options.Value.GitHub;

    public async Task<int> SyncAsync(string login, CancellationToken ct = default)
    {
        var boards = await ResolveBoardsAsync(login, ct);
        if (boards.Count == 0)
        {
            logger.LogInformation("No Projects V2 boards found to sync");
            return 0;
        }

        var count = 0;
        foreach (var board in boards)
        {
            ct.ThrowIfCancellationRequested();
            count += await SyncBoardAsync(board, login, ct);
        }

        return count;
    }

    private record BoardRef(string NodeId, int Number, string OwnerLogin, string OwnerType, string Title, string? Url, bool Closed, DateTimeOffset UpdatedAt);

    private async Task<List<BoardRef>> ResolveBoardsAsync(string login, CancellationToken ct)
    {
        var boards = new List<BoardRef>();

        if (_options.Projects.Count > 0)
        {
            foreach (var reference in _options.Projects)
            {
                var parts = reference.Split('/');
                if (parts.Length != 2 || !int.TryParse(parts[1], out var number))
                {
                    logger.LogWarning("Ignoring malformed project reference '{Reference}' (expected owner/number)", reference);
                    continue;
                }

                var board = await LookupBoardAsync(parts[0], number, ct);
                if (board is not null) boards.Add(board);
            }

            return boards;
        }

        // Auto-discover: my own boards plus every configured org's boards.
        boards.AddRange(await DiscoverAsync("viewer", null, ct));
        foreach (var org in _options.Organizations)
            boards.AddRange(await DiscoverAsync("organization", org, ct));

        return boards.DistinctBy(b => b.NodeId).Where(b => !b.Closed).ToList();
    }

    private async Task<List<BoardRef>> DiscoverAsync(string ownerKind, string? login, CancellationToken ct)
    {
        var selector = ownerKind switch
        {
            "viewer" => "viewer",
            "organization" => "organization(login: $login)",
            _ => "user(login: $login)"
        };

        var query = $$"""
            query($login: String!) {
              {{selector}} {
                login
                projectsV2(first: 50, orderBy: {field: UPDATED_AT, direction: DESC}) {
                  nodes { id number title url closed updatedAt }
                }
              }
            }
            """;

        // The viewer query takes no variable, so keep the signature valid with a dummy value.
        if (ownerKind == "viewer")
            query = query.Replace("query($login: String!)", "query");

        var result = new List<BoardRef>();
        try
        {
            var data = await github.GraphQlAsync(query, login is null ? null : new { login }, ct);
            var owner = data.EnumerateObject().FirstOrDefault().Value;
            if (owner.ValueKind != JsonValueKind.Object) return result;

            var ownerLogin = owner.GetStringOrNull("login") ?? login ?? "";
            if (!owner.TryGetProperty("projectsV2", out var projects)
                || !projects.TryGetProperty("nodes", out var nodes)) return result;

            foreach (var node in nodes.EnumerateArray())
            {
                var id = node.GetStringOrNull("id");
                if (id is null) continue;
                result.Add(new BoardRef(
                    id,
                    node.TryGetProperty("number", out var n) ? n.GetInt32() : 0,
                    ownerLogin,
                    ownerKind == "organization" ? "organization" : "user",
                    node.GetStringOrNull("title") ?? "",
                    node.GetStringOrNull("url"),
                    node.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True,
                    node.GetDateOrNull("updatedAt") ?? DateTimeOffset.UtcNow));
            }
        }
        catch (GitHubGraphQlException ex)
        {
            logger.LogWarning("Could not list projects for {Owner}: {Message}", login ?? "viewer", ex.Message);
        }

        return result;
    }

    private async Task<BoardRef?> LookupBoardAsync(string owner, int number, CancellationToken ct)
    {
        const string query = """
            query($owner: String!, $number: Int!) {
              organization(login: $owner) {
                login
                projectV2(number: $number) { id number title url closed updatedAt }
              }
              user(login: $owner) {
                login
                projectV2(number: $number) { id number title url closed updatedAt }
              }
            }
            """;

        JsonElement data;
        try
        {
            data = await github.GraphQlAsync(query, new { owner, number }, ct);
        }
        catch (GitHubGraphQlException ex)
        {
            // A user login makes the organization branch error out, and vice versa; retry each alone.
            logger.LogDebug("Combined project lookup failed for {Owner}/{Number}: {Message}", owner, number, ex.Message);
            return await LookupBoardSingleAsync("organization", owner, number, ct)
                   ?? await LookupBoardSingleAsync("user", owner, number, ct);
        }

        foreach (var kind in (string[])["organization", "user"])
        {
            if (!data.TryGetProperty(kind, out var ownerNode) || ownerNode.ValueKind != JsonValueKind.Object) continue;
            if (!ownerNode.TryGetProperty("projectV2", out var project) || project.ValueKind != JsonValueKind.Object) continue;
            var board = ToBoardRef(project, ownerNode.GetStringOrNull("login") ?? owner, kind);
            if (board is not null) return board;
        }

        logger.LogWarning("Project {Owner}/{Number} not found or not visible to the token", owner, number);
        return null;
    }

    private async Task<BoardRef?> LookupBoardSingleAsync(string kind, string owner, int number, CancellationToken ct)
    {
        var query = $$"""
            query($owner: String!, $number: Int!) {
              {{kind}}(login: $owner) {
                login
                projectV2(number: $number) { id number title url closed updatedAt }
              }
            }
            """;

        try
        {
            var data = await github.GraphQlAsync(query, new { owner, number }, ct);
            if (!data.TryGetProperty(kind, out var ownerNode) || ownerNode.ValueKind != JsonValueKind.Object) return null;
            if (!ownerNode.TryGetProperty("projectV2", out var project) || project.ValueKind != JsonValueKind.Object) return null;
            return ToBoardRef(project, ownerNode.GetStringOrNull("login") ?? owner, kind);
        }
        catch (GitHubGraphQlException)
        {
            return null;
        }
    }

    private static BoardRef? ToBoardRef(JsonElement project, string ownerLogin, string ownerKind)
    {
        var id = project.GetStringOrNull("id");
        if (id is null) return null;
        return new BoardRef(
            id,
            project.TryGetProperty("number", out var n) ? n.GetInt32() : 0,
            ownerLogin,
            ownerKind,
            project.GetStringOrNull("title") ?? "",
            project.GetStringOrNull("url"),
            project.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True,
            project.GetDateOrNull("updatedAt") ?? DateTimeOffset.UtcNow);
    }

    private const string ItemsQuery = """
        query($projectId: ID!, $after: String, $statusField: String!) {
          node(id: $projectId) {
            ... on ProjectV2 {
              id
              fields(first: 50) {
                nodes {
                  ... on ProjectV2SingleSelectField { id name options { id name } }
                  ... on ProjectV2Field { id name }
                  ... on ProjectV2IterationField { id name }
                }
              }
              items(first: 50, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id
                  createdAt
                  updatedAt
                  isArchived
                  fieldValueByName(name: $statusField) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name optionId }
                  }
                  content {
                    __typename
                    ... on Issue {
                      id number title url state closedAt createdAt updatedAt
                      repository { nameWithOwner }
                      author { login }
                      assignees(first: 10) { nodes { login } }
                    }
                    ... on PullRequest {
                      id number title url state merged closedAt createdAt updatedAt
                      repository { nameWithOwner }
                      author { login }
                      assignees(first: 10) { nodes { login } }
                    }
                    ... on DraftIssue {
                      id title createdAt updatedAt
                      creator { login }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private async Task<int> SyncBoardAsync(BoardRef board, string login, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var project = await db.Projects.FindAsync([board.NodeId], ct);
        if (project is null)
        {
            project = new Project { Id = board.NodeId };
            db.Projects.Add(project);
        }

        project.Number = board.Number;
        project.OwnerLogin = board.OwnerLogin;
        project.OwnerType = board.OwnerType;
        project.Title = board.Title;
        project.Url = board.Url;
        project.Closed = board.Closed;
        project.UpdatedAt = board.UpdatedAt;
        project.LastSyncedAt = now;

        var count = 0;
        string? after = null;
        var fieldsCaptured = false;

        do
        {
            var data = await github.GraphQlAsync(ItemsQuery,
                new { projectId = board.NodeId, after, statusField = _options.StatusFieldName }, ct);

            if (!data.TryGetProperty("node", out var node) || node.ValueKind != JsonValueKind.Object)
                break;

            if (!fieldsCaptured)
            {
                CaptureStatusField(project, node);
                fieldsCaptured = true;
            }

            if (!node.TryGetProperty("items", out var items)) break;

            foreach (var itemNode in items.GetProperty("nodes").EnumerateArray())
                count += await SyncItemAsync(project, itemNode, login, ct);

            var pageInfo = items.GetProperty("pageInfo");
            after = pageInfo.TryGetProperty("hasNextPage", out var hasNext) && hasNext.ValueKind == JsonValueKind.True
                ? pageInfo.GetStringOrNull("endCursor")
                : null;
        } while (after is not null && !ct.IsCancellationRequested);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Synced board '{Title}' ({Count} item changes)", board.Title, count);
        return count;
    }

    /// <summary>Records the Status field id and its options so move_ticket can set a column by name.</summary>
    private void CaptureStatusField(Project project, JsonElement node)
    {
        if (!node.TryGetProperty("fields", out var fields)
            || !fields.TryGetProperty("nodes", out var fieldNodes)) return;

        foreach (var field in fieldNodes.EnumerateArray())
        {
            var name = field.GetStringOrNull("name");
            if (!string.Equals(name, _options.StatusFieldName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!field.TryGetProperty("options", out var optionNodes)) continue;

            project.StatusFieldId = field.GetStringOrNull("id");
            var map = new Dictionary<string, string>();
            foreach (var option in optionNodes.EnumerateArray())
            {
                var optionName = option.GetStringOrNull("name");
                var optionId = option.GetStringOrNull("id");
                if (optionName is not null && optionId is not null) map[optionName] = optionId;
            }

            project.StatusOptions = JsonSerializer.Serialize(map);
            return;
        }
    }

    private async Task<int> SyncItemAsync(Project project, JsonElement itemNode, string login, CancellationToken ct)
    {
        var itemId = itemNode.GetStringOrNull("id");
        if (itemId is null) return 0;

        var content = itemNode.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        var contentType = content.ValueKind == JsonValueKind.Object ? content.GetStringOrNull("__typename") ?? "Unknown" : "Unknown";

        var assignees = content.ValueKind == JsonValueKind.Object
                        && content.TryGetProperty("assignees", out var a)
                        && a.TryGetProperty("nodes", out var assigneeNodes)
            ? assigneeNodes.EnumerateArray().Select(x => x.GetStringOrNull("login")).Where(x => x is not null).Select(x => x!).ToArray()
            : [];

        var author = content.ValueKind == JsonValueKind.Object
            ? (content.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object ? au.GetStringOrNull("login") : null)
              ?? (content.TryGetProperty("creator", out var cr) && cr.ValueKind == JsonValueKind.Object ? cr.GetStringOrNull("login") : null)
            : null;

        var isMine = assignees.Any(x => x.Equals(login, StringComparison.OrdinalIgnoreCase))
                     || string.Equals(author, login, StringComparison.OrdinalIgnoreCase);

        var status = itemNode.TryGetProperty("fieldValueByName", out var statusValue)
                     && statusValue.ValueKind == JsonValueKind.Object
            ? statusValue.GetStringOrNull("name")
            : null;

        var now = DateTimeOffset.UtcNow;
        var existing = await db.ProjectItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        var isNew = existing is null;
        var previousStatus = existing?.Status;

        existing ??= new ProjectItem { Id = itemId, ProjectId = project.Id };
        if (isNew) db.ProjectItems.Add(existing);

        existing.ContentType = contentType;
        existing.ContentNodeId = content.ValueKind == JsonValueKind.Object ? content.GetStringOrNull("id") : null;
        existing.ContentNumber = content.ValueKind == JsonValueKind.Object && content.TryGetProperty("number", out var num) && num.TryGetInt32(out var parsedNumber)
            ? parsedNumber : null;
        existing.RepositoryFullName = content.ValueKind == JsonValueKind.Object
                                      && content.TryGetProperty("repository", out var repo)
            ? repo.GetStringOrNull("nameWithOwner") : null;
        existing.Title = (content.ValueKind == JsonValueKind.Object ? content.GetStringOrNull("title") : null) ?? "(untitled)";
        existing.Url = content.ValueKind == JsonValueKind.Object ? content.GetStringOrNull("url") : null;
        existing.Assignees = assignees;
        existing.AuthorLogin = author;
        existing.IsMine = isMine;
        existing.IsArchived = itemNode.TryGetProperty("isArchived", out var arch) && arch.ValueKind == JsonValueKind.True;
        existing.ContentState = content.ValueKind == JsonValueKind.Object ? content.GetStringOrNull("state") : null;
        existing.ItemCreatedAt = ReadDate(itemNode, "createdAt") ?? now;
        existing.ItemUpdatedAt = ReadDate(itemNode, "updatedAt") ?? now;
        existing.ClosedAt = content.ValueKind == JsonValueKind.Object ? ReadDate(content, "closedAt") : null;
        existing.UpdatedAt = now;
        existing.Status = status;

        var changes = 0;

        if (isNew && isMine)
        {
            existing.StatusChangedAt = existing.ItemUpdatedAt;
            await RecordProjectActivityAsync(project, existing, ActivityTypes.ProjectItemAdded,
                null, status, existing.ItemCreatedAt, login, ct);
            changes++;
        }
        else if (!isNew && isMine && !string.Equals(previousStatus, status, StringComparison.Ordinal))
        {
            existing.PreviousStatus = previousStatus;
            existing.StatusChangedAt = existing.ItemUpdatedAt;
            await RecordProjectActivityAsync(project, existing, ActivityTypes.ProjectItemMoved,
                previousStatus, status, existing.ItemUpdatedAt, login, ct);
            changes++;
        }

        return changes;
    }

    private async Task RecordProjectActivityAsync(
        Project project, ProjectItem item, string activityType,
        string? fromStatus, string? toStatus, DateTimeOffset occurredAt, string login, CancellationToken ct)
    {
        // The status is part of the key so a card moving back and forth keeps every hop.
        var sourceId = activityType == ActivityTypes.ProjectItemAdded
            ? $"projectitem:{item.Id}:added"
            : $"projectitem:{item.Id}:{toStatus ?? "none"}:{occurredAt.ToUnixTimeSeconds()}";

        var title = activityType == ActivityTypes.ProjectItemAdded
            ? $"Added to {project.Title}: {item.Title}"
            : $"Moved to {toStatus ?? "(no status)"}: {item.Title}";

        var exists = await db.Activities.AnyAsync(
            a => a.Source == GitHubSyncService.SourceName
                 && a.SourceId == sourceId
                 && a.ActivityType == activityType, ct);
        if (exists) return;

        var now = DateTimeOffset.UtcNow;
        db.Activities.Add(new Activity
        {
            Source = GitHubSyncService.SourceName,
            SourceId = sourceId,
            ActivityType = activityType,
            RepositoryFullName = item.RepositoryFullName,
            Actor = login,
            IsMine = true,
            OccurredAt = occurredAt,
            Title = title,
            Summary = fromStatus is null ? null : $"{fromStatus} -> {toStatus}",
            Url = item.Url,
            Number = item.ContentNumber,
            State = toStatus,
            Payload = JsonSerializer.Serialize(new
            {
                ProjectTitle = project.Title,
                ProjectNumber = project.Number,
                ItemId = item.Id,
                item.Title,
                item.ContentType,
                item.ContentNumber,
                Repository = item.RepositoryFullName,
                FromStatus = fromStatus,
                ToStatus = toStatus,
                item.Assignees,
                item.Url
            }),
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        element.GetDateOrNull(property);
}
