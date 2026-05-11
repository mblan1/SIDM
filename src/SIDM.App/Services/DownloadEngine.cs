using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.Core.Engine;
using SIDM.Core.Models;
using SIDM.Core.Persistence;

namespace SIDM.App.Services;

/// <summary>
/// Owns the lifetime of all in-flight downloads. Bridges the UI's start/pause/cancel
/// gestures to the <see cref="DownloadOrchestrator"/> and persists status changes
/// back to the repository.
/// </summary>
public sealed class DownloadEngine
{
    private readonly DownloadOrchestrator _orchestrator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDownloadProgressSink _progressSink;
    private readonly ILogger<DownloadEngine> _logger;

    private readonly ConcurrentDictionary<long, CancellationTokenSource> _active = new();

    /// <summary>
    /// Fires after a download run terminates — successfully (Completed),
    /// canceled (Paused), or with an error (Failed). The <see cref="DownloadQueue"/>
    /// subscribes to release a concurrency slot and dispatch the next pending
    /// download. Handlers run on a threadpool worker, never on the UI thread.
    /// </summary>
    public event Action<long, DownloadStatus>? Finished;

    public DownloadEngine(
        DownloadOrchestrator orchestrator,
        IServiceScopeFactory scopeFactory,
        IDownloadProgressSink progressSink,
        ILogger<DownloadEngine> logger)
    {
        _orchestrator = orchestrator;
        _scopeFactory = scopeFactory;
        _progressSink = progressSink;
        _logger = logger;
    }

    /// <summary>Starts (or resumes) the given download in the background.</summary>
    public Task StartAsync(long downloadId, CancellationToken cancellationToken = default)
    {
        if (_active.ContainsKey(downloadId))
        {
            _logger.LogDebug("Download {Id} is already running; ignoring StartAsync", downloadId);
            return Task.CompletedTask;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_active.TryAdd(downloadId, cts))
        {
            cts.Dispose();
            return Task.CompletedTask;
        }

        _ = Task.Run(() => RunAsync(downloadId, cts), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Cancels an in-flight download (treated as a pause — partial file is preserved).</summary>
    public bool Pause(long downloadId)
    {
        if (_active.TryRemove(downloadId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    public bool IsActive(long downloadId) => _active.ContainsKey(downloadId);

    private async Task RunAsync(long downloadId, CancellationTokenSource cts)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();

        var download = await repo.GetAsync(downloadId, cts.Token);
        if (download is null)
        {
            _logger.LogWarning("DownloadEngine.Start: id {Id} not found", downloadId);
            CleanupActive(downloadId, cts);
            return;
        }

        // Mark started.
        download.Status = DownloadStatus.Downloading;
        download.StartedUtc ??= DateTimeOffset.UtcNow;
        download.ErrorMessage = null;
        await repo.UpdateAsync(download, CancellationToken.None);

        var headers = ParseDictionary(download.HeadersJson);
        var cookies = ParseDictionary(download.CookiesJson);

        var resume = download.Segments
            .Where(s => s.BytesDownloaded > 0 || s.Status != SegmentStatus.Pending)
            .OrderBy(s => s.Idx)
            .Select(s => new ResumeSegment(s.Idx, s.StartByte, s.EndByte, s.BytesDownloaded))
            .ToArray();

        var request = new DownloadRequest
        {
            Url = new Uri(download.Url),
            TargetPath = download.TargetPath,
            Segments = download.SegmentCount > 0 ? download.SegmentCount : 8,
            Headers = headers,
            Cookies = cookies,
            ExpectedHash = download.ExpectedHash,
            HashAlgo = download.HashAlgo,
            Resume = resume.Length > 0 ? resume : null,
        };

        var sink = new ScopedSegmentProgressSink(downloadId, _progressSink);

        DownloadResult result;
        try
        {
            result = await _orchestrator.ExecuteAsync(request, sink, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download {Id} crashed", downloadId);
            await UpdateStatusAsync(repo, download, DownloadStatus.Failed, ex.Message);
            CleanupActive(downloadId, cts);
            RaiseFinished(downloadId, DownloadStatus.Failed);
            return;
        }

        await PersistResultAsync(repo, download, result);
        CleanupActive(downloadId, cts);
        RaiseFinished(downloadId, download.Status);
    }

    private void RaiseFinished(long downloadId, DownloadStatus status)
    {
        try { Finished?.Invoke(downloadId, status); }
        catch (Exception ex) { _logger.LogWarning(ex, "Finished handler threw for {Id}", downloadId); }
    }

    private async Task PersistResultAsync(IDownloadRepository repo, Download download, DownloadResult result)
    {
        // Persist final segment offsets for accurate resume.
        if (result.Segments.Count > 0)
        {
            var segments = result.Segments.Select(s => new Segment
            {
                Idx = s.Index,
                StartByte = s.StartByte,
                EndByte = s.EndByte,
                BytesDownloaded = s.BytesDownloaded,
                Status = s.LastOutcome == SegmentOutcome.Completed
                    ? SegmentStatus.Completed
                    : SegmentStatus.Pending,
            }).ToArray();
            await repo.ReplaceSegmentsAsync(download.Id, segments);
        }

        if (result.Success)
        {
            download.Status = DownloadStatus.Completed;
            download.CompletedUtc = DateTimeOffset.UtcNow;
            download.TotalBytes = result.TotalBytes;
            download.ErrorMessage = null;
        }
        else if (result.FailureKind == DownloadFailureKind.Canceled)
        {
            download.Status = DownloadStatus.Paused;
        }
        else
        {
            download.Status = DownloadStatus.Failed;
            download.ErrorMessage = result.FailureMessage;
        }

        await repo.UpdateAsync(download);
    }

    private static async Task UpdateStatusAsync(IDownloadRepository repo, Download download, DownloadStatus status, string? error)
    {
        download.Status = status;
        download.ErrorMessage = error;
        await repo.UpdateAsync(download);
    }

    private void CleanupActive(long downloadId, CancellationTokenSource cts)
    {
        _active.TryRemove(downloadId, out _);
        cts.Dispose();
    }

    private static IReadOnlyDictionary<string, string>? ParseDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }
}
