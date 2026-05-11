using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIDM.Core.Abstractions;
using SIDM.Core.Models;
using SIDM.Core.Persistence;
using SIDM.Core.Scheduling;

namespace SIDM.App.Services;

/// <summary>
/// Background service that evaluates the user's <see cref="ScheduleRule"/>s
/// every <see cref="TickInterval"/> and applies the resulting
/// <see cref="ScheduleDecision"/> to the live queue + governor.
///
/// State transitions:
/// - <b>Allowed → Blocked</b>: every active download is paused and remembered
///   in <c>_pausedByScheduler</c>. New downloads still arrive on the queue but
///   are parked because the cap is set to 0… actually we keep the user's cap
///   and just empty the running set by pausing.
/// - <b>Blocked → Allowed</b>: every id in <c>_pausedByScheduler</c> is
///   re-enqueued. Caps are restored to either the user's persisted values OR
///   the matching rule's overrides (most-restrictive wins).
///
/// In-memory state only — on app restart, the set of "paused by scheduler"
/// downloads is lost. Those rows are simply Paused in the DB; if the window
/// later reopens the scheduler will NOT auto-resume them (the user can do so
/// manually). This is intentional: it avoids surprising resume of work the
/// user paused themselves before quitting.
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DownloadQueue _queue;
    private readonly BandwidthSettingsService _bandwidth;
    private readonly IBandwidthGovernor _governor;
    private readonly ILogger<SchedulerService> _logger;

    private readonly object _stateLock = new();
    private bool _lastAllowed = true;
    private readonly HashSet<long> _pausedByScheduler = new();

    private int _userMaxConcurrent;
    private long _userBytesPerSecond;

    public SchedulerService(
        IServiceScopeFactory scopeFactory,
        DownloadQueue queue,
        BandwidthSettingsService bandwidth,
        IBandwidthGovernor governor,
        ILogger<SchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _bandwidth = bandwidth;
        _governor = governor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Capture the user's persisted caps so we can restore them after a
        // scheduler-imposed override expires. These are read from the live
        // queue/governor — DownloadAutoResumeService loads them at startup
        // before the SchedulerService begins ticking.
        _userMaxConcurrent = _queue.MaxConcurrent;
        _userBytesPerSecond = _governor.BytesPerSecond;

        // Tick once immediately so newly-launched apps in a blocked window
        // don't have to wait the full interval to be paused.
        try { await TickAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogError(ex, "Scheduler initial tick failed"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler tick failed; will retry next interval");
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ScheduleRule> rules;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IScheduleRuleRepository>();
            rules = await repo.GetAllAsync(cancellationToken);
        }

        var decision = ScheduleEvaluator.Evaluate(rules, DateTimeOffset.Now);

        bool transitionedToAllowed;
        bool transitionedToBlocked;
        lock (_stateLock)
        {
            transitionedToAllowed = decision.Allowed && !_lastAllowed;
            transitionedToBlocked = !decision.Allowed && _lastAllowed;
            _lastAllowed = decision.Allowed;
        }

        // Suspend / unsuspend the queue so newly-enqueued downloads honor the
        // current window without needing the scheduler to react to each enqueue.
        _queue.Suspended = !decision.Allowed;

        // Apply caps every tick (idempotent) so a rule edit during a window
        // takes effect within one interval.
        ApplyCaps(decision);

        if (transitionedToBlocked)
        {
            _logger.LogInformation("Schedule window closed; pausing active downloads");
            await PauseAllActiveAsync(cancellationToken);
        }
        else if (transitionedToAllowed)
        {
            _logger.LogInformation("Schedule window opened; resuming scheduler-paused downloads");
            await ResumePausedByScheduler(cancellationToken);
        }
    }

    private void ApplyCaps(ScheduleDecision decision)
    {
        // Effective max-concurrent: most restrictive of (user setting, rule override).
        var effectiveMax = decision.MaxConcurrent > 0
            ? Math.Min(_userMaxConcurrent, decision.MaxConcurrent)
            : _userMaxConcurrent;
        if (_queue.MaxConcurrent != effectiveMax)
        {
            _queue.MaxConcurrent = effectiveMax;
        }

        var effectiveBytes = decision.BandwidthBytesPerSecond > 0
            ? (_userBytesPerSecond > 0
                ? Math.Min(_userBytesPerSecond, decision.BandwidthBytesPerSecond)
                : decision.BandwidthBytesPerSecond)
            : _userBytesPerSecond;
        if (_governor.BytesPerSecond != effectiveBytes)
        {
            // Apply directly to the live governor — do NOT persist via
            // BandwidthSettingsService, since this is a temporary override.
            _governor.BytesPerSecond = effectiveBytes;
        }
    }

    private async Task PauseAllActiveAsync(CancellationToken cancellationToken)
    {
        long[] ids;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var active = await repo.GetByStatusAsync(DownloadStatus.Downloading, cancellationToken);
            var queued = await repo.GetByStatusAsync(DownloadStatus.Queued, cancellationToken);
            ids = active.Concat(queued).Select(d => d.Id).ToArray();
        }

        foreach (var id in ids)
        {
            try
            {
                await _queue.PauseAsync(id, cancellationToken);
                lock (_stateLock) { _pausedByScheduler.Add(id); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to pause download {Id} on window close", id);
            }
        }
    }

    private async Task ResumePausedByScheduler(CancellationToken cancellationToken)
    {
        long[] toResume;
        lock (_stateLock)
        {
            toResume = _pausedByScheduler.ToArray();
            _pausedByScheduler.Clear();
        }

        foreach (var id in toResume)
        {
            try
            {
                // Mark Queued in DB so MonitorAsync in the VM keeps the row live.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
                var d = await repo.GetAsync(id, cancellationToken);
                if (d is null || d.Status == DownloadStatus.Completed) continue;
                if (d.Status == DownloadStatus.Paused)
                {
                    d.Status = DownloadStatus.Queued;
                    d.ErrorMessage = null;
                    await repo.UpdateAsync(d, cancellationToken);
                }

                await _queue.EnqueueAsync(id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resume scheduler-paused download {Id}", id);
            }
        }
    }

    /// <summary>
    /// Called by the settings dialog after the user changes their persisted
    /// max-concurrent or bandwidth-cap so the scheduler's "user baseline" stays
    /// in sync. Without this, the next tick would see the scheduler-imposed
    /// override as the baseline and refuse to lift restrictions.
    /// </summary>
    public void SetUserBaseline(int maxConcurrent, long bytesPerSecond)
    {
        _userMaxConcurrent = Math.Max(1, maxConcurrent);
        _userBytesPerSecond = Math.Max(0, bytesPerSecond);
    }
}
