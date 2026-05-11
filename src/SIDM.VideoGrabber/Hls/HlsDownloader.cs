using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SIDM.VideoGrabber.Hls;

/// <summary>
/// What to download and where.
/// </summary>
/// <param name="PlaylistUrl">Master or media playlist URL.</param>
/// <param name="OutputFilePath">Final file path (absolute). The downloader writes the concatenated stream to a <c>.part</c> file and renames on success.</param>
/// <param name="ParallelSegmentDownloads">How many segments to fetch in parallel. Default 4.</param>
public sealed record HlsDownloadRequest(
    Uri PlaylistUrl,
    string OutputFilePath,
    int ParallelSegmentDownloads = 4);

/// <summary>
/// Live progress sample emitted while an HLS download is in flight.
/// </summary>
/// <param name="SegmentsCompleted">Number of segments fully downloaded + (decrypted) + written.</param>
/// <param name="SegmentsTotal">Total segment count from the media playlist.</param>
/// <param name="BytesWritten">Cumulative bytes written to the output file so far.</param>
public sealed record HlsDownloadProgress(int SegmentsCompleted, int SegmentsTotal, long BytesWritten);

/// <summary>Final result.</summary>
/// <param name="Success">Exit status.</param>
/// <param name="FinalFilePath">Resolved output path on success; null otherwise.</param>
/// <param name="TotalBytes">Bytes written to the output file.</param>
/// <param name="FailureMessage">Human-readable reason on failure.</param>
public sealed record HlsDownloadResult(bool Success, string? FinalFilePath, long TotalBytes, string? FailureMessage);

/// <summary>
/// Pure-C# HLS downloader. Implements the simple-but-popular slice of RFC 8216:
/// master playlist → highest-bandwidth variant → media playlist → ordered
/// segment download (parallel within a window) → optional AES-128-CBC decrypt
/// → concatenate to a single MPEG-TS file.
///
/// Out of scope for v1 (will be added when real users hit them):
/// - live streams (no EXT-X-ENDLIST) — refused with a clear error,
/// - fragmented MP4 (#EXT-X-MAP) — refused (Phase 4.B.2 will fold in init segments),
/// - SAMPLE-AES / FairPlay / Widevine,
/// - byte-range segment requests (EXT-X-BYTERANGE),
/// - sub-second playback timing (it's playable, not frame-accurate).
/// </summary>
public sealed class HlsDownloader
{
    private readonly IHlsHttpClient _http;
    private readonly ILogger<HlsDownloader> _logger;

    public HlsDownloader(IHlsHttpClient http, ILogger<HlsDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<HlsDownloadResult> DownloadAsync(
        HlsDownloadRequest request,
        IProgress<HlsDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        // ---- 1. Fetch the (possibly master) playlist ----
        string text;
        try
        {
            text = await _http.GetStringAsync(request.PlaylistUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new HlsDownloadResult(false, null, 0, $"Failed to fetch playlist: {ex.Message}");
        }

        Uri mediaUrl = request.PlaylistUrl;
        if (M3U8Parser.IsMasterPlaylist(text))
        {
            var master = M3U8Parser.ParseMaster(text, request.PlaylistUrl);
            if (master.Variants.Count == 0)
            {
                return new HlsDownloadResult(false, null, 0, "Master playlist had no variants.");
            }
            // Pick highest bandwidth (most common UX expectation).
            var chosen = master.Variants.OrderByDescending(v => v.Bandwidth).First();
            _logger.LogInformation("HLS: chose variant {Bw} bps {Res}", chosen.Bandwidth, chosen.Resolution ?? "?");
            mediaUrl = chosen.Url;

            try
            {
                text = await _http.GetStringAsync(mediaUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new HlsDownloadResult(false, null, 0, $"Failed to fetch variant playlist: {ex.Message}");
            }
        }

        // ---- 2. Parse media playlist ----
        var media = M3U8Parser.ParseMedia(text, mediaUrl);

        if (media.IsLive)
        {
            return new HlsDownloadResult(false, null, 0, "Live streams are not supported (no EXT-X-ENDLIST).");
        }
        if (media.IsFmp4)
        {
            return new HlsDownloadResult(false, null, 0, "Fragmented MP4 streams (EXT-X-MAP) are not supported in this build.");
        }
        if (media.Segments.Count == 0)
        {
            return new HlsDownloadResult(false, null, 0, "Media playlist had no segments.");
        }

        // ---- 3. Fetch every distinct AES key once ----
        var keyCache = new ConcurrentDictionary<Uri, byte[]>();
        try
        {
            foreach (var key in media.Segments.Select(s => s.Key).OfType<HlsKey>().DistinctBy(k => k.KeyUrl))
            {
                if (!key.Method.Equals("AES-128", StringComparison.OrdinalIgnoreCase))
                {
                    return new HlsDownloadResult(false, null, 0,
                        $"Unsupported HLS encryption method: {key.Method}");
                }
                var bytes = await _http.GetBytesAsync(key.KeyUrl, cancellationToken).ConfigureAwait(false);
                if (bytes.Length != 16)
                {
                    return new HlsDownloadResult(false, null, 0,
                        $"AES key at {key.KeyUrl} was {bytes.Length} bytes, expected 16.");
                }
                keyCache[key.KeyUrl] = bytes;
            }
        }
        catch (Exception ex)
        {
            return new HlsDownloadResult(false, null, 0, $"Failed to fetch encryption key: {ex.Message}");
        }

        // ---- 4. Fetch + decrypt segments in parallel, write in order ----
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputFilePath)!);
        var tempPath = request.OutputFilePath + ".part";

        var totalSegments = media.Segments.Count;
        var fetched = new byte[totalSegments][];
        var degree = Math.Max(1, request.ParallelSegmentDownloads);

        using var semaphore = new SemaphoreSlim(degree);
        var fetchTasks = new List<Task>(totalSegments);
        long completedSegments = 0;

        for (var i = 0; i < totalSegments; i++)
        {
            var idx = i;
            var seg = media.Segments[idx];
            fetchTasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var raw = await _http.GetBytesAsync(seg.Url, cancellationToken).ConfigureAwait(false);

                    if (seg.Key is { } key)
                    {
                        var iv = key.ExplicitIv ?? HlsCrypto.DeriveIvFromSequence(seg.MediaSequenceNumber);
                        raw = HlsCrypto.DecryptAes128(raw, keyCache[key.KeyUrl], iv);
                    }
                    fetched[idx] = raw;
                    Interlocked.Increment(ref completedSegments);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        try
        {
            // Coordinator: poll for completed-in-order segments and stream them
            // to disk so we don't have to keep all of them in memory at once.
            long bytesWritten = 0;
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20))
            {
                var nextToWrite = 0;
                while (nextToWrite < totalSegments)
                {
                    if (fetched[nextToWrite] is { } payload)
                    {
                        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                        bytesWritten += payload.Length;
                        fetched[nextToWrite] = null!; // Release.
                        nextToWrite++;
                        progress?.Report(new HlsDownloadProgress((int)Interlocked.Read(ref completedSegments), totalSegments, bytesWritten));
                    }
                    else
                    {
                        // The next-in-order segment is still in flight. Poll
                        // briefly; segment downloads typically take seconds, so
                        // 100 ms granularity is plenty and avoids burning CPU.
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            await Task.WhenAll(fetchTasks).ConfigureAwait(false);

            // ---- 5. Rename on success ----
            if (File.Exists(request.OutputFilePath)) File.Delete(request.OutputFilePath);
            File.Move(tempPath, request.OutputFilePath);

            return new HlsDownloadResult(true, request.OutputFilePath, bytesWritten, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(tempPath);
            return new HlsDownloadResult(false, null, 0, "Canceled");
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            _logger.LogError(ex, "HLS download crashed for {Url}", request.PlaylistUrl);
            return new HlsDownloadResult(false, null, 0, ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
