using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using Octokit;
using ProductHeaderValue = Octokit.ProductHeaderValue;

namespace TrackMeBaby.GitHub;

/// <summary>
/// Owns the token. Nothing outside this namespace ever sees it, which is what keeps the
/// credential away from Claude (plan section 24) — MCP tools only ever get typed results.
/// </summary>
public class GitHubAccess
{
    private const string GraphQlEndpoint = "https://api.github.com/graphql";

    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubAccess> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _login;

    public GitHubAccess(IOptions<TrackerOptions> options, ILogger<GitHubAccess> logger, HttpClient http)
    {
        _options = options.Value.GitHub;
        _logger = logger;
        _http = http;

        Rest = new GitHubClient(new ProductHeaderValue("track-me-baby"));
        if (!string.IsNullOrWhiteSpace(_options.Token))
            Rest.Credentials = new Credentials(_options.Token);

        _http.BaseAddress ??= new Uri("https://api.github.com/");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("track-me-baby");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrWhiteSpace(_options.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
    }

    public GitHubClient Rest { get; }

    public bool HasToken => !string.IsNullOrWhiteSpace(_options.Token);

    /// <summary>The tracked user's login. Resolved from the token on first use and cached.</summary>
    public async Task<string> GetLoginAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_options.Login)) return _options.Login;
        if (_login is not null) return _login;

        await _loginLock.WaitAsync(ct);
        try
        {
            if (_login is not null) return _login;
            var user = await Rest.User.Current();
            _login = user.Login;
            _logger.LogInformation("Resolved GitHub login {Login} from token", _login);
            return _login;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>
    /// GitHub allows roughly 30 search requests per minute and enforces an undocumented
    /// "secondary" limit on bursts. A 180-day backfill issues a few hundred search calls, so they
    /// are serialised and spaced instead of fired as fast as the loop can go.
    /// </summary>
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(2.2);

    /// <summary>Longest we will sit waiting for a limit to reset before giving up on this run.</summary>
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromMinutes(3);

    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private DateTimeOffset _nextSearchSlot = DateTimeOffset.MinValue;

    /// <summary>Runs a search request under the throttle, retrying transient rate-limit rejections.</summary>
    public async Task<T> SearchAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
    {
        await _searchGate.WaitAsync(ct);
        try
        {
            var wait = _nextSearchSlot - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

            try
            {
                return await WithRetryAsync(operation, ct);
            }
            finally
            {
                _nextSearchSlot = DateTimeOffset.UtcNow + SearchInterval;
            }
        }
        finally
        {
            _searchGate.Release();
        }
    }

    /// <summary>
    /// Retries rate-limit rejections with backoff. Anything else, and anything that would mean
    /// waiting longer than <see cref="MaxRetryWait"/>, propagates so the sync fails and resumes
    /// from its checkpoint on the next cycle.
    /// </summary>
    public async Task<T> WithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct = default, int maxAttempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (RateLimitExceededException ex) when (attempt < maxAttempts)
            {
                var wait = ex.GetRetryAfterTimeSpan();
                if (wait > MaxRetryWait) throw;
                _logger.LogWarning("Primary rate limit hit; waiting {Seconds:0}s (attempt {Attempt})",
                    wait.TotalSeconds, attempt);
                await Task.Delay(wait + TimeSpan.FromSeconds(1), ct);
            }
            catch (AbuseException ex) when (attempt < maxAttempts)
            {
                // Secondary limits carry no reliable reset header, so back off exponentially.
                var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt) * 15);
                if (wait > MaxRetryWait) throw;
                _logger.LogWarning(ex, "Secondary rate limit hit; waiting {Seconds:0}s (attempt {Attempt})",
                    wait.TotalSeconds, attempt);
                await Task.Delay(wait, ct);
            }
        }
    }

    /// <summary>Remaining core REST rate limit, for the status page.</summary>
    public async Task<(int Remaining, int Limit, DateTimeOffset Reset)> GetRateLimitAsync()
    {
        var limits = await Rest.RateLimit.GetRateLimits();
        var core = limits.Resources.Core;
        return (core.Remaining, core.Limit, core.Reset);
    }

    /// <summary>Posts a GraphQL document and returns the "data" element. Throws on GraphQL errors.</summary>
    public async Task<JsonElement> GraphQlAsync(string query, object? variables = null, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { query, variables });
        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new GitHubGraphQlException($"GitHub GraphQL returned {(int)response.StatusCode}: {Truncate(json)}");

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            // GitHub sometimes returns a null message alongside a typed error, so this must not
            // assume a string — the error reporter throwing would mask the real problem.
            var messages = errors.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null)
                .Where(m => !string.IsNullOrWhiteSpace(m));

            var text = string.Join("; ", messages);
            if (string.IsNullOrWhiteSpace(text)) text = Truncate(errors.ToString());

            // A board with no read:project scope reports as a null node plus an error; the
            // caller can carry on with the rest of the sync instead of aborting.
            throw new GitHubGraphQlException($"GitHub GraphQL error: {text}");
        }

        if (!document.RootElement.TryGetProperty("data", out var data))
            throw new GitHubGraphQlException($"GitHub GraphQL response had no data: {Truncate(json)}");

        // Clone so the value outlives the JsonDocument.
        return data.Clone();
    }

    /// <summary>
    /// Raw REST GET for the few endpoints Octokit does not wrap (commit search).
    /// Returns null on 4xx so a best-effort caller can skip rather than fail the sync.
    /// </summary>
    public async Task<JsonElement?> RestGetAsync(string relativeUrl, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(relativeUrl, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GitHub REST {Url} returned {Status}: {Body}",
                relativeUrl, (int)response.StatusCode, Truncate(json));
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "...";
}

public class GitHubGraphQlException(string message) : Exception(message);
