using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.Core.Engine;
using SIDM.Core.Models;
using SIDM.Core.Persistence;
using SIDM.VideoGrabber;
using SIDM.VideoGrabber.Dash;
using SIDM.VideoGrabber.Ffmpeg;
using SIDM.VideoGrabber.Hls;

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
    private readonly HlsDownloader _hlsDownloader;
    private readonly DashDownloader _dashDownloader;
    private readonly FfmpegRemuxer _ffmpegRemuxer;
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
        HlsDownloader hlsDownloader,
        DashDownloader dashDownloader,
        FfmpegRemuxer ffmpegRemuxer,
        VideoGrabberSettingsService videoGrabberSettings,
        IServiceScopeFactory scopeFactory,
        IDownloadProgressSink progressSink,
        ILogger<DownloadEngine> logger)
    {
        _orchestrator = orchestrator;
        _ytDlpRunner = ytDlpRunner;
        _hlsDownloader = hlsDownloader;
        _dashDownloader = dashDownloader;
        _ffmpegRemuxer = ffmpegRemuxer;
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
        // segment orchestrator; .m3u8 playlists go through the pure-C# HLS
        // downloader.
        if (download.SourceKind == SourceKind.YouTube)
        {
            await RunYouTubeAsync(repo, download, cts);
            return;
        }
        if (download.SourceKind == SourceKind.Hls)
        {
            await RunHlsAsync(repo, download, cts);
            return;
        }
        if (download.SourceKind == SourceKind.Dash)
        {
            await RunDashAsync(repo, download, cts);
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
            // The orchestrator fires this once the planned segment shape is
            // known (after probe, or immediately when resuming). Persist a
            // skeleton row per segment so:
            //   - the UI's chunk grid renders right away (the polling loop
            //     reads them via repo.GetAsync and rebuilds SegmentRows),
            //   - SegmentProgressWriter's per-tick UPDATE statements match
            //     real rows (instead of silently affecting 0 rows because
            //     no skeleton exists yet),
            //   - the bus path's _segmentByIdx fills in from the first poll,
            //     so per-segment Report() events stop being discarded.
            void PersistPlannedSegments(IReadOnlyList<SegmentTask> planned)
            {
                try
                {
                    var skeleton = planned.Select(t => new Segment
                    {
                        Idx = t.Index,
                        StartByte = t.StartByte,
                        EndByte = t.EndByte,
                        BytesDownloaded = t.BytesAlreadyDownloaded,
                        Status = SegmentStatus.Pending,
                    }).ToArray();
                    // Fire-and-forget: the orchestrator continues on the
                    // worker thread while this scope writes to SQLite.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await using var scope = _scopeFactory.CreateAsyncScope();
                            var innerRepo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
                            await innerRepo.ReplaceSegmentsAsync(downloadId, skeleton, cts.Token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to persist planned segments for {Id}", downloadId);
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PersistPlannedSegments inline failure for {Id}", downloadId);
                }
            }

            result = await _orchestrator.ExecuteAsync(request, sink, cts.Token, PersistPlannedSegments);
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
    /// Backfills <see cref="Download.TotalBytes"/> from the actual file on disk
    /// when the downloader couldn't tell us a total up-front (HLS/DASH live
    /// playlists, HTTP responses without Content-Length). Called from each
    /// completion path so a finished row in the UI always shows its real size
    /// instead of "Unknown".
    /// </summary>
    private static void CaptureFinalFileSizeIfMissing(Download download)
    {
        if (download.TotalBytes is > 0) return;
        try
        {
            var fi = new FileInfo(download.TargetPath);
            if (fi.Exists && fi.Length > 0)
            {
                download.TotalBytes = fi.Length;
            }
        }
        catch
        {
            // Best-effort — leave TotalBytes as it was.
        }
    }

    /// <summary>
    /// Writes a single Idx=0 segment row before the yt-dlp / HLS / DASH
    /// runners start, so:
    ///   - <see cref="SIDM.Data.Repositories.SegmentProgressWriter"/>'s
    ///     periodic UPDATE matches a real row instead of affecting 0 rows
    ///     (which was the cause of the "0% all the way through, no speed,
    ///     no ETA" symptom on yt-dlp downloads).
    ///   - The chunks panel + per-segment Percent in the progress dialog
    ///     renders immediately — the row VM rebuilds its segments map from
    ///     the first poll.
    /// EndByte uses the picker-supplied <see cref="Download.TotalBytes"/>
    /// when known; otherwise 0 (the live download path still drives
    /// progress via the model's BytesDownloaded / TotalBytes, not the
    /// segment's Length).
    /// </summary>
    private static async Task EnsureSinglePlaceholderSegmentAsync(
        IDownloadRepository repo,
        Download download,
        CancellationToken cancellationToken)
    {
        // Already persisted by a previous run / resume — leave the bytes
        // already on file intact so resume math stays correct.
        if (download.Segments.Count > 0) return;

        var totalBytes = download.TotalBytes ?? 0;
        var endByte = totalBytes > 0 ? totalBytes - 1 : 0;
        try
        {
            await repo.ReplaceSegmentsAsync(download.Id, new[]
            {
                new Segment
                {
                    Idx = 0,
                    StartByte = 0,
                    EndByte = endByte,
                    BytesDownloaded = 0,
                    Status = SegmentStatus.Pending,
                },
            }, cancellationToken);
        }
        catch
        {
            // Best-effort — if persistence fails, the runner still works,
            // we just lose the chunks-panel/progress-writer plumbing.
        }
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

        // Persist a synthetic single-segment row up front so the progress
        // bus's UPDATE statements match a real row (the writer's UPDATE
        // would otherwise affect 0 rows because the segment is only
        // persisted at completion in PersistResultAsync — the live
        // download then shows 0% / "—" speed / empty chunks panel even
        // though bytes are flowing). The end-byte is the picker's
        // expectedLength when known; updates to the actual total happen
        // at completion. The chunks panel renders one "stream" row
        // immediately so the user sees activity.
        await EnsureSinglePlaceholderSegmentAsync(repo, download, cts.Token);

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
            FfmpegPath: _videoGrabberSettings.ResolveFfmpeg(),
            // User-picked format selector from the browser-overlay format
            // picker (null = let yt-dlp pick its default).
            FormatSelector: download.SelectedYtDlpFormat);

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
            CaptureFinalFileSizeIfMissing(download);
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

    /// <summary>
    /// HLS run path. Reuses the existing ScopedSegmentProgressSink with a
    /// synthetic single-segment row (Idx=0), so the progress dialog and
    /// row VM render exactly as they do for plain HTTP downloads.
    /// </summary>
    private async Task RunHlsAsync(IDownloadRepository repo, Download download, CancellationTokenSource cts)
    {
        // Ensure the output path has a .ts extension — HLS segments are
        // MPEG-TS, and most users won't think to add the suffix themselves.
        var targetPath = download.TargetPath;
        if (!Path.HasExtension(targetPath) ||
            !targetPath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            targetPath = Path.ChangeExtension(targetPath, ".ts");
        }

        // Same skeleton trick as RunYouTubeAsync — without this, the
        // progress writer's UPDATE matches 0 rows and the UI sits at 0%.
        await EnsureSinglePlaceholderSegmentAsync(repo, download, cts.Token);

        var sink = new ScopedSegmentProgressSink(download.Id, _progressSink);
        long observedTotalBytes = 0;
        var lastBytes = 0L;

        var progress = new Progress<HlsDownloadProgress>(sample =>
        {
            sink.Report(0, sample.BytesWritten);
            lastBytes = sample.BytesWritten;
            if (sample.BytesWritten > observedTotalBytes) observedTotalBytes = sample.BytesWritten;
        });

        HlsDownloadResult result;
        try
        {
            result = await _hlsDownloader.DownloadAsync(
                new HlsDownloadRequest(new Uri(download.Url), targetPath, ParallelSegmentDownloads: 4),
                progress,
                cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HLS run crashed for {Id}", download.Id);
            await UpdateStatusAsync(repo, download, DownloadStatus.Failed, ex.Message);
            CleanupActive(download.Id, cts);
            RaiseFinished(download.Id, DownloadStatus.Failed);
            return;
        }

        // Persist a synthetic single-segment row so the UI's progress display
        // has something coherent to render (HLS doesn't expose byte ranges).
        var totalForRow = result.TotalBytes > 0 ? result.TotalBytes : observedTotalBytes;
        if (totalForRow > 0)
        {
            await repo.ReplaceSegmentsAsync(download.Id, new[]
            {
                new Segment
                {
                    Idx = 0,
                    StartByte = 0,
                    EndByte = totalForRow - 1,
                    BytesDownloaded = lastBytes,
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
            download.TotalBytes = result.TotalBytes;
            download.ErrorMessage = null;
            var finalPath = result.FinalFilePath ?? targetPath;

            // Best-effort remux to MP4. If ffmpeg isn't configured, the .ts
            // file stays in place — both formats are playable, and we surface
            // the actual file path the user gets on disk.
            var ffmpegPath = _videoGrabberSettings.ResolveFfmpeg();
            var remux = await _ffmpegRemuxer.RemuxToMp4Async(finalPath, ffmpegPath, cts.Token);
            if (remux.Outcome == RemuxOutcome.Succeeded && !string.IsNullOrWhiteSpace(remux.OutputPath))
            {
                finalPath = remux.OutputPath!;
                _logger.LogInformation("HLS download {Id} remuxed to {Path}", download.Id, finalPath);
            }
            else if (remux.Outcome == RemuxOutcome.Failed)
            {
                // Keep the .ts and the Completed status — partial value is
                // still useful. Log so the user can see why mux didn't happen.
                _logger.LogWarning("HLS remux failed for {Id}: {Reason}", download.Id, remux.FailureMessage);
            }

            download.TargetPath = finalPath;
            download.FileName = Path.GetFileName(finalPath);
            CaptureFinalFileSizeIfMissing(download);
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

    /// <summary>
    /// DASH run path. Same single-segment-row trick as HLS — the engine has no
    /// stable byte-range concept for DASH segments, so progress maps to one
    /// synthetic segment so the existing progress dialog renders normally.
    /// </summary>
    private async Task RunDashAsync(IDownloadRepository repo, Download download, CancellationTokenSource cts)
    {
        var targetPath = Path.ChangeExtension(download.TargetPath, ".mp4");

        // Same skeleton trick as the other single-segment paths.
        await EnsureSinglePlaceholderSegmentAsync(repo, download, cts.Token);

        var sink = new ScopedSegmentProgressSink(download.Id, _progressSink);
        long lastBytes = 0;
        var progress = new Progress<DashDownloadProgress>(sample =>
        {
            sink.Report(0, sample.BytesWritten);
            lastBytes = sample.BytesWritten;
        });

        DashDownloadResult result;
        try
        {
            result = await _dashDownloader.DownloadAsync(
                new DashDownloadRequest(new Uri(download.Url), targetPath, _videoGrabberSettings.ResolveFfmpeg(), ParallelSegmentDownloads: 4),
                progress,
                cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DASH run crashed for {Id}", download.Id);
            await UpdateStatusAsync(repo, download, DownloadStatus.Failed, ex.Message);
            CleanupActive(download.Id, cts);
            RaiseFinished(download.Id, DownloadStatus.Failed);
            return;
        }

        if (result.TotalBytes > 0)
        {
            await repo.ReplaceSegmentsAsync(download.Id, new[]
            {
                new Segment
                {
                    Idx = 0,
                    StartByte = 0,
                    EndByte = result.TotalBytes - 1,
                    BytesDownloaded = lastBytes,
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
            download.TotalBytes = result.TotalBytes;
            // result.FailureMessage may carry an informational note even on
            // success (e.g. "ffmpeg not configured — kept tracks separate").
            download.ErrorMessage = result.FailureMessage;
            if (!string.IsNullOrWhiteSpace(result.FinalFilePath))
            {
                download.TargetPath = result.FinalFilePath!;
                download.FileName = Path.GetFileName(result.FinalFilePath!);
            }
            CaptureFinalFileSizeIfMissing(download);
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
            CaptureFinalFileSizeIfMissing(download);
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
