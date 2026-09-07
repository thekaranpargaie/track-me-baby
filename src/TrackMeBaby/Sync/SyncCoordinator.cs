using Microsoft.EntityFrameworkCore;
using TrackMeBaby.Data;

namespace TrackMeBaby.Sync;

/// <summary>
/// Single entry point for running a sync, whether triggered by the timer or by the sync_now
/// tool. Serialises runs in-process, and uses the sync_states row as a cross-process lease so
/// an HTTP host and a stdio host started side by side do not both hammer GitHub.
/// </summary>
public class SyncCoordinator(IServiceScopeFactory scopeFactory, ILogger<SyncCoordinator> logger)
{
    /// <summary>
    /// How stale the heartbeat may get before we assume the holder died. A running sync touches
    /// UpdatedAt after every window, so a killed process releases the lease within one window.
    /// </summary>
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning { get; private set; }

    public async Task<SyncResult> RunAsync(DateTimeOffset? since = null, bool force = false, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(force ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, ct))
        {
            var now = DateTimeOffset.UtcNow;
            return new SyncResult(false, 0, now, now, 0,
                "A sync is already running in this process.", []);
        }

        try
        {
            IsRunning = true;
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();

            if (!force)
            {
                var state = await db.SyncStates.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Source == GitHubSyncService.SourceName, ct);
                // Compare against UpdatedAt, not the start time: a long backfill keeps the lease
                // alive by checkpointing, while a crashed run goes quiet and loses it.
                if (state is { Status: SyncStatuses.Running }
                    && DateTimeOffset.UtcNow - state.UpdatedAt < LeaseTimeout)
                {
                    logger.LogInformation("Another process is syncing (heartbeat {Beat:u}); skipping this run", state.UpdatedAt);
                    var now = DateTimeOffset.UtcNow;
                    return new SyncResult(false, 0, now, now, 0,
                        "Another process holds the sync lease. Pass force to override.", []);
                }
            }

            var sync = scope.ServiceProvider.GetRequiredService<GitHubSyncService>();
            return await sync.SyncAsync(since, ct);
        }
        finally
        {
            IsRunning = false;
            _gate.Release();
        }
    }
}

/// <summary>Runs a sync on a fixed interval (plan section 22). Polling only; no webhooks in V1.</summary>
public class SyncWorker(
    SyncCoordinator coordinator,
    IOptionsMonitor<TrackerOptions> options,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.CurrentValue.Sync;
        if (!settings.Enabled)
        {
            logger.LogInformation("Background sync is disabled (Tracker:Sync:Enabled = false)");
            return;
        }

        if (settings.RunOnStartup)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.StartupDelaySeconds), stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, settings.IntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        logger.LogInformation("Background sync scheduled every {Minutes} minute(s)", interval.TotalMinutes);

        while (await SafeWaitAsync(timer, stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var result = await coordinator.RunAsync(ct: ct);
            if (!result.Success && result.Error is not null)
                logger.LogWarning("Scheduled sync did not complete: {Error}", result.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a bad cycle kill the worker; the next tick retries from the same bookmark.
            logger.LogError(ex, "Scheduled sync threw");
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
