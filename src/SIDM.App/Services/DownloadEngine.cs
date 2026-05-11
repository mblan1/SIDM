using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.Core.Engine;
using SIDM.Core.Models;
using SIDM.Core.Persistence;
using SIDM.VideoGrabber;

namespace SIDM.App.Services;

/// <summary>
/// Owns the lifetime of all in-flight downloads. Bridges the UI's start/pause/cancel
/// gestures to the <see cref="DownloadOrchestrator"/> and persists status changes
/// back to the repository.
/// </summary>
public sealed class DownloadEngine
{
    private readonly DownloadOrchestrator _orchestrator;
    private readonly IYtDlpRunner _ytDlpRunner;
    private readonly VideoGrabberSettingsService _videoGrabberSettings;
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
        IYtDlpRunner ytDlpRunner,
        VideoGrabberSettingsService videoGrabberSettings,
        IServiceScopeFactory scopeFactory,
        IDownloadProgressSink progressSink,
        ILogger<DownloadEngine> logger)
    {
        _orchestrator = orchestrator;
        _ytDlpRunner = ytDlpRunner;
        _videoGrabberSettings = videoGrabberSettings;
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

        // Route by source: video sites go through yt-dlp instead of the HTTP
        // segment orchestrator.
        if (download.SourceKind == SourceKind.YouTube)
        {
            await RunYouTubeAsync(repo, download, cts);
            return;
        }

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

    /// <summary>
    /// yt-dlp run path. Spawns the binary, streams progress through the same
    /// <see cref="IDownloadProgressSink"/> the HTTP engine uses (mapped to a
    /// single synthetic segment with Idx=0 because yt-dlp does not expose
    /// segment-level byte counts), and updates the row with the final file
    /// path that yt-dlp emitted via <c>--print after_move:filepath</c>.
    /// </summary>
    private async Task RunYouTubeAsync(IDownloadRepository repo, Download download, CancellationTokenSource cts)
    {
        var ytDlpPath = _videoGrabberSettings.ResolveYtDlp();
        if (string.IsNullOrEmpty(ytDlpPath))
        {
            _logger.LogWarning("yt-dlp not found; failing download {Id}", download.Id);
            await UpdateStatusAsync(repo, download, DownloadStatus.Failed,
                "yt-dlp.exe is not configured. Open Settings → Video downloader to set its path.");
            CleanupActive(download.Id, cts);
            RaiseFinished(download.Id, DownloadStatus.Failed);
            return;
        }

        var outputDir = Path.GetDirectoryName(download.TargetPath);
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            await UpdateStatusAsync(repo, download, DownloadStatus.Failed,
                "Target path has no directory component.");
            CleanupActive(download.Id, cts);
            RaiseFinished(download.Id, DownloadStatus.Failed);
            return;
        }

        var sink = new ScopedSegmentProgressSink(download.Id, _progressSink);
        long observedTotalBytes = 0;
        long lastReportedBytes = 0;

        var progress = new Progress<YtDlpProgress>(sample =>
        {
            // The HTTP engine reports cumulative bytes per segment; we map all
            // yt-dlp progress to segment 0.
            sink.Report(0, sample.DownloadedBytes);
            lastReportedBytes = sample.DownloadedBytes;
            if (sample.TotalBytes is { } total && total > observedTotalBytes)
            {
                observedTotalBytes = total;
            }
        });

        var request = new YtDlpRunRequest(
            Url: download.Url,
            OutputDirectory: outputDir!,
            YtDlpPath: ytDlpPath,
            FfmpegPath: _videoGrabberSettings.ResolveFfmpeg());

        YtDlpRunResult result;
        try
        {
            result = await _ytDlpRunner.RunAsync(request, progress, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp run crashed for {Id}", download.Id);
            await UpdateStatusAsync(repo, download, DownloadStatus.Failed, ex.Message);
            CleanupActive(download.Id, cts);
            RaiseFinished(download.Id, DownloadStatus.Failed);
            return;
        }

        // Persist a synthetic single-segment row so the UI's progress display
        // and the resume math both have something coherent to work with.
        var totalForRow = observedTotalBytes > 0 ? observedTotalBytes : lastReportedBytes;
        if (totalForRow > 0)
        {
            await repo.ReplaceSegmentsAsync(download.Id, new[]
            {
                new Segment
                {
                    Idx = 0,
                    StartByte = 0,
                    EndByte = totalForRow - 1,
                    BytesDownloaded = lastReportedBytes,
                    Status = result.Success ? SegmentStatus.Completed : SegmentStatus.Pending,
                },
            });
        }

        if (cts.IsCancellationRequested)
        {
            download.Status = DownloadStatus.Paused;
        }
        else if (result.Success)
        {
            download.Status = DownloadStatus.Completed;
            download.CompletedUtc = DateTimeOffset.UtcNow;
            download.TotalBytes = lastReportedBytes > 0 ? lastReportedBytes : download.TotalBytes;
            download.ErrorMessage = null;
            if (!string.IsNullOrWhiteSpace(result.FinalFilePath))
            {
                download.TargetPath = result.FinalFilePath!;
                download.FileName = Path.GetFileName(result.FinalFilePath!);
            }
        }
        else
        {
            download.Status = DownloadStatus.Failed;
            download.ErrorMessage = result.FailureMessage;
        }

        await repo.UpdateAsync(download);
        CleanupActive(download.Id, cts);
        RaiseFinished(download.Id, download.Status);
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
