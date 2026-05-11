using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.Core.Models;
using SIDM.Core.Persistence;

namespace SIDM.App.Services;

/// <summary>
/// Gates how many downloads can run concurrently. Sits between the UI/IPC
/// intake and <see cref="DownloadEngine"/>: callers <see cref="EnqueueAsync"/>
/// a download id; if a slot is free, the engine is kicked immediately,
/// otherwise the id parks in a pending list and is dequeued when an active
/// download finishes (subscribed via <see cref="DownloadEngine.Finished"/>).
///
/// Ordering: FIFO across calls to <see cref="EnqueueAsync"/>. The queue
/// persists nothing — pending downloads are already saved with
/// <see cref="DownloadStatus.Queued"/>, so the in-memory list is a cache.
/// On app start, <see cref="LoadAsync"/> re-builds the configured cap from
/// settings; the auto-resume service re-enqueues persisted Queued/Downloading
/// rows.
///
/// Thread-safety: a single internal lock guards both <c>_running</c> and
/// <c>_pending</c>. The hot operations (Enqueue / Finished) are cheap
/// (HashSet add, list scan with O(N) where N = number of queued items).
/// </summary>
public sealed class DownloadQueue
{
    public const string MaxConcurrentSettingKey = "queue.maxConcurrent";
    public const int DefaultMaxConcurrent = 4;

    private readonly DownloadEngine _engine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DownloadQueue> _logger;

    private readonly object _lock = new();
    private readonly HashSet<long> _running = new();
    private readonly List<long> _pending = new();
    private int _maxConcurrent = DefaultMaxConcurrent;

    public DownloadQueue(
        DownloadEngine engine,
        IServiceScopeFactory scopeFactory,
        ILogger<DownloadQueue> logger)
    {
        _engine = engine;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _engine.Finished += OnEngineFinished;
    }

    /// <summary>
    /// Current cap on simultaneous in-flight downloads. Setting a higher value
    /// drains the pending list immediately (best-effort — actual starts run on
    /// the threadpool). Setting a LOWER value does not pause anything already
    /// running; those finish naturally.
    /// </summary>
    public int MaxConcurrent
    {
        get { lock (_lock) { return _maxConcurrent; } }
        set
        {
            var v = Math.Max(1, value);
            List<long> toStart;
            lock (_lock)
            {
                _maxConcurrent = v;
                toStart = TakeStartableLocked();
            }
            foreach (var id in toStart) _ = StartAsync(id);
        }
    }

    /// <summary>Active (running) download count.</summary>
    public int RunningCount { get { lock (_lock) { return _running.Count; } } }

    /// <summary>Queued (waiting for a slot) download count.</summary>
    public int PendingCount { get { lock (_lock) { return _pending.Count; } } }

    /// <summary>
    /// Loads the configured max-concurrent from settings and re-queues any
    /// downloads persisted as <see cref="DownloadStatus.Queued"/>. Call once
    /// at app startup BEFORE <see cref="DownloadAutoResumeService"/> runs.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        var configured = await settings.GetAsync<int?>(MaxConcurrentSettingKey, cancellationToken);

        lock (_lock)
        {
            _maxConcurrent = configured is > 0 ? configured.Value : DefaultMaxConcurrent;
        }
    }

    /// <summary>
    /// Hands a download id to the queue. If a slot is free it is started
    /// right away (status flips to Downloading via the engine); otherwise the
    /// id is left in Queued state and parked.
    /// </summary>
    public Task EnqueueAsync(long downloadId, CancellationToken cancellationToken = default)
    {
        bool startNow;
        lock (_lock)
        {
            if (_running.Contains(downloadId))
            {
                return Task.CompletedTask;
            }

            if (_running.Count < _maxConcurrent)
            {
                _running.Add(downloadId);
                _pending.Remove(downloadId);
                startNow = true;
            }
            else
            {
                if (!_pending.Contains(downloadId)) _pending.Add(downloadId);
                startNow = false;
            }
        }

        if (startNow) return StartAsync(downloadId, cancellationToken);
        _logger.LogInformation("Queued download {Id} (concurrency at cap of {Max})", downloadId, MaxConcurrent);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pauses the download. If it was running, the engine cancels it and the
    /// Finished event will release the slot. If it was only queued (waiting),
    /// we remove it from the pending list and mark the row Paused directly.
    /// </summary>
    public async Task PauseAsync(long downloadId, CancellationToken cancellationToken = default)
    {
        bool wasRunning;
        bool wasPending;
        lock (_lock)
        {
            wasRunning = _running.Contains(downloadId);
            wasPending = _pending.Remove(downloadId);
        }

        if (wasRunning)
        {
            _engine.Pause(downloadId);
            return;
        }

        if (wasPending)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var d = await repo.GetAsync(downloadId, cancellationToken);
            if (d is null) return;
            d.Status = DownloadStatus.Paused;
            await repo.UpdateAsync(d, cancellationToken);
        }
    }

    /// <summary>Drops a download from the queue. The engine cancels it if running.</summary>
    public void Remove(long downloadId)
    {
        lock (_lock)
        {
            _pending.Remove(downloadId);
        }
        _engine.Pause(downloadId);
    }

    private async Task StartAsync(long downloadId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _engine.StartAsync(downloadId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Engine refused to start {Id}", downloadId);
            // Release the slot we reserved — pretend the run finished.
            OnEngineFinished(downloadId, DownloadStatus.Failed);
        }
    }

    private void OnEngineFinished(long downloadId, DownloadStatus status)
    {
        List<long> toStart;
        lock (_lock)
        {
            _running.Remove(downloadId);
            toStart = TakeStartableLocked();
        }

        if (toStart.Count > 0)
        {
            _logger.LogDebug("Engine finished {Id} ({Status}); dispatching {Count} pending", downloadId, status, toStart.Count);
            foreach (var id in toStart) _ = StartAsync(id);
        }
    }

    /// <summary>
    /// Moves as many ids as possible from <c>_pending</c> to <c>_running</c>
    /// while honoring the cap. Caller MUST hold <c>_lock</c>. Returned ids are
    /// to be started outside the lock to avoid reentrancy.
    /// </summary>
    private List<long> TakeStartableLocked()
    {
        // FIFO drain. The Download.Priority field exists in the schema; once
        // there is UI to set it, replace the head pop with a min-heap pop on
        // (Priority desc, CreatedUtc asc).
        var result = new List<long>();
        while (_running.Count < _maxConcurrent && _pending.Count > 0)
        {
            var id = _pending[0];
            _pending.RemoveAt(0);
            _running.Add(id);
            result.Add(id);
        }
        return result;
    }
}
