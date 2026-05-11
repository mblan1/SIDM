using Microsoft.Extensions.Logging;
using SIDM.VideoGrabber.Ffmpeg;
using SIDM.VideoGrabber.Hls;

namespace SIDM.VideoGrabber.Dash;

public sealed record DashDownloadRequest(
    Uri ManifestUrl,
    string OutputFilePath,
    string? FfmpegPath = null,
    int ParallelSegmentDownloads = 4);

public sealed record DashDownloadProgress(int SegmentsCompleted, int SegmentsTotal, long BytesWritten);

public sealed record DashDownloadResult(bool Success, string? FinalFilePath, long TotalBytes, string? FailureMessage);

/// <summary>
/// Pure-C# DASH downloader. Reuses <see cref="IHlsHttpClient"/> for HTTP so
/// it picks up the same retry policy as HLS + segment downloads.
///
/// Flow:
///   1. Fetch + parse the MPD,
///   2. Pick the highest-bandwidth video Representation and (if present) the
///      highest-bandwidth audio Representation,
///   3. Download init + media segments for each track in parallel, writing
///      to <c>&lt;output&gt;.video.mp4</c> and <c>&lt;output&gt;.audio.mp4</c>
///      (or only the video file if no audio track exists),
///   4. If <see cref="FfmpegRemuxer"/> can mux + ffmpeg is configured, fuse
///      video + audio into a single MP4 at the requested output path. The
///      per-track temp files are deleted on success.
///   5. If audio is present but ffmpeg is missing, leave both files behind
///      with a clear status — the user can mux manually.
///
/// Refused with clear messages: dynamic / live manifests, DRM-protected
/// manifests, and manifests with no usable representations.
/// </summary>
public sealed class DashDownloader
{
    private readonly IHlsHttpClient _http;
    private readonly FfmpegRemuxer _ffmpegRemuxer;
    private readonly ILogger<DashDownloader> _logger;

    public DashDownloader(IHlsHttpClient http, FfmpegRemuxer ffmpegRemuxer, ILogger<DashDownloader> logger)
    {
        _http = http;
        _ffmpegRemuxer = ffmpegRemuxer;
        _logger = logger;
    }

    public async Task<DashDownloadResult> DownloadAsync(
        DashDownloadRequest request,
        IProgress<DashDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        // ---- 1. Fetch + parse manifest ----
        string xml;
        try
        {
            xml = await _http.GetStringAsync(request.ManifestUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DashDownloadResult(false, null, 0, $"Failed to fetch manifest: {ex.Message}");
        }

        DashManifest manifest;
        try
        {
            manifest = MpdParser.Parse(xml, request.ManifestUrl);
        }
        catch (Exception ex)
        {
            return new DashDownloadResult(false, null, 0, $"Failed to parse MPD: {ex.Message}");
        }

        if (manifest.IsDynamic)
            return new DashDownloadResult(false, null, 0, "Live (dynamic) DASH manifests are not supported.");
        if (manifest.HasDrm)
            return new DashDownloadResult(false, null, 0, "DRM-protected DASH manifests are not supported.");
        if (manifest.Representations.Count == 0)
            return new DashDownloadResult(false, null, 0, "Manifest contains no representations.");

        var video = manifest.Representations
            .Where(r => r.ContentKind == DashContentKind.Video && r.MediaSegmentUrls.Count > 0)
            .OrderByDescending(r => r.Bandwidth)
            .FirstOrDefault();
        var audio = manifest.Representations
            .Where(r => r.ContentKind == DashContentKind.Audio && r.MediaSegmentUrls.Count > 0)
            .OrderByDescending(r => r.Bandwidth)
            .FirstOrDefault();

        if (video is null)
        {
            // No video — try the highest-bandwidth non-audio rep as a fallback.
            video = manifest.Representations
                .Where(r => r.ContentKind != DashContentKind.Audio && r.MediaSegmentUrls.Count > 0)
                .OrderByDescending(r => r.Bandwidth)
                .FirstOrDefault();
            if (video is null)
                return new DashDownloadResult(false, null, 0, "Manifest contains no downloadable video stream.");
        }

        _logger.LogInformation("DASH: chose video {Bw} bps {W}x{H}, audio {AudioBw} bps",
            video.Bandwidth, video.Width, video.Height, audio?.Bandwidth);

        // ---- 2. Decide output paths ----
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputFilePath)!);
        var basePathNoExt = Path.Combine(
            Path.GetDirectoryName(request.OutputFilePath)!,
            Path.GetFileNameWithoutExtension(request.OutputFilePath));
        var videoTemp = basePathNoExt + ".video.mp4";
        var audioTemp = basePathNoExt + ".audio.mp4";
        var finalPath = Path.ChangeExtension(request.OutputFilePath, ".mp4");

        // ---- 3. Download tracks ----
        int totalSegments = video.MediaSegmentUrls.Count + (audio?.MediaSegmentUrls.Count ?? 0);
        int completedSegments = 0;
        long bytesWritten = 0;

        void ReportProgress() =>
            progress?.Report(new DashDownloadProgress(
                Interlocked.CompareExchange(ref completedSegments, 0, 0),
                totalSegments,
                Interlocked.Read(ref bytesWritten)));

        try
        {
            await DownloadTrackAsync(video, videoTemp, request.ParallelSegmentDownloads,
                () => { Interlocked.Increment(ref completedSegments); ReportProgress(); },
                bytes => Interlocked.Add(ref bytesWritten, bytes),
                cancellationToken).ConfigureAwait(false);

            if (audio is not null)
            {
                await DownloadTrackAsync(audio, audioTemp, request.ParallelSegmentDownloads,
                    () => { Interlocked.Increment(ref completedSegments); ReportProgress(); },
                    bytes => Interlocked.Add(ref bytesWritten, bytes),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(videoTemp);
            TryDelete(audioTemp);
            return new DashDownloadResult(false, null, 0, "Canceled");
        }
        catch (Exception ex)
        {
            TryDelete(videoTemp);
            TryDelete(audioTemp);
            return new DashDownloadResult(false, null, 0, $"Track download failed: {ex.Message}");
        }

        // ---- 4. Merge tracks ----
        if (audio is null)
        {
            // Video-only: just rename the video temp to the final path.
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(videoTemp, finalPath);
            return new DashDownloadResult(true, finalPath, new FileInfo(finalPath).Length, null);
        }

        var muxResult = await _ffmpegRemuxer
            .MuxVideoAudioAsync(videoTemp, audioTemp, finalPath, request.FfmpegPath, cancellationToken)
            .ConfigureAwait(false);

        if (muxResult.Outcome == RemuxOutcome.Succeeded)
        {
            TryDelete(videoTemp);
            TryDelete(audioTemp);
            return new DashDownloadResult(true, finalPath, new FileInfo(finalPath).Length, null);
        }

        // ffmpeg missing or failed: keep both track files; tell the user.
        var detail = muxResult.Outcome == RemuxOutcome.NotConfigured
            ? "ffmpeg is not configured — video and audio were saved as separate .mp4 files; configure ffmpeg in Settings to auto-merge."
            : $"ffmpeg mux failed: {muxResult.FailureMessage}";
        return new DashDownloadResult(true, videoTemp, new FileInfo(videoTemp).Length + new FileInfo(audioTemp).Length, detail);
    }

    private async Task DownloadTrackAsync(
        DashRepresentation rep,
        string outputPath,
        int parallelism,
        Action onSegmentCompleted,
        Action<long> onBytesWritten,
        CancellationToken cancellationToken)
    {
        var initBytes = rep.InitSegmentUrl is null
            ? Array.Empty<byte>()
            : await _http.GetBytesAsync(rep.InitSegmentUrl, cancellationToken).ConfigureAwait(false);

        var total = rep.MediaSegmentUrls.Count;
        var fetched = new byte[total][];
        var degree = Math.Max(1, parallelism);
        using var semaphore = new SemaphoreSlim(degree);

        var fetchTasks = new List<Task>(total);
        for (var i = 0; i < total; i++)
        {
            var idx = i;
            var url = rep.MediaSegmentUrls[idx];
            fetchTasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fetched[idx] = await _http.GetBytesAsync(url, cancellationToken).ConfigureAwait(false);
                    onSegmentCompleted();
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        if (initBytes.Length > 0)
        {
            await output.WriteAsync(initBytes, cancellationToken).ConfigureAwait(false);
            onBytesWritten(initBytes.Length);
        }

        var nextToWrite = 0;
        while (nextToWrite < total)
        {
            if (fetched[nextToWrite] is { } payload)
            {
                await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                onBytesWritten(payload.Length);
                fetched[nextToWrite] = null!;
                nextToWrite++;
            }
            else
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(fetchTasks).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
