using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SIDM.Core.Abstractions;
using SIDM.Core.Http;
using SIDM.Core.Models;

namespace SIDM.Core.Engine;

/// <summary>
/// The conductor for one download. Probes the URL, allocates the file, splits the
/// total length into byte ranges, runs N <see cref="SegmentWorker"/>s in parallel,
/// finalizes the file, and (optionally) verifies the hash.
///
/// Implemented:
/// - Multi-segment Range download with parallel workers + sparse-file writes.
/// - Single-stream fallback when the probe says the server doesn't accept ranges
///   *and* mid-flight when a worker discovers the server lying (200 to a Range).
/// - Resume from caller-supplied per-segment offsets.
/// - Cancellation (pause) preserves the partial file.
/// - Optional hash verification (md5 / sha1 / sha256 / sha512) post-finalize.
///
/// Deferred to Phase 2 (documented limitation):
/// - Work-stealing on permanent segment failure. Today, if a single segment fails
///   after Polly's 5 retries, the whole download fails. The "right" fix is to
///   redistribute the failed range across surviving workers so the download
///   degrades gracefully. In practice this is rare — Polly already absorbs all
///   transient HTTP errors — so it's deferred until we have telemetry showing
///   non-trivial real-world hit rate.
/// </summary>
public sealed class DownloadOrchestrator
{
    private readonly IRangeProbe _rangeProbe;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDownloadFileWriterFactory _writerFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DownloadOrchestrator> _logger;

    public DownloadOrchestrator(
        IRangeProbe rangeProbe,
        IHttpClientFactory httpClientFactory,
        IDownloadFileWriterFactory writerFactory,
        ILoggerFactory loggerFactory)
    {
        _rangeProbe = rangeProbe;
        _httpClientFactory = httpClientFactory;
        _writerFactory = writerFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DownloadOrchestrator>();
    }

    public async Task<DownloadResult> ExecuteAsync(
        DownloadRequest request,
        ISegmentProgressSink? progressSink = null,
        CancellationToken cancellationToken = default)
    {
        progressSink ??= NullProgressSink.Instance;

        // ---- Plan the download (probe or use caller-supplied resume layout) ----

        long totalBytes;
        IReadOnlyList<SegmentTask> initialTasks;
        Uri effectiveUrl;
        bool serverAdvertisesRanges;

        if (request.Resume is { Count: > 0 })
        {
            totalBytes = request.Resume[^1].EndByte + 1;
            initialTasks = request.Resume
                .Select(r => new SegmentTask(
                    Url: request.Url,
                    Index: r.Index,
                    StartByte: r.StartByte,
                    EndByte: r.EndByte,
                    BytesAlreadyDownloaded: r.BytesAlreadyDownloaded,
                    Headers: request.Headers,
                    Cookies: request.Cookies))
                .ToArray();
            effectiveUrl = request.Url;
            serverAdvertisesRanges = initialTasks.Count > 1;
            _logger.LogInformation("Resuming {Url} with {Count} segments", request.Url, initialTasks.Count);
        }
        else
        {
            ProbeResult probe;
            try
            {
                probe = await _rangeProbe.ProbeAsync(request.Url, request.Headers, request.Cookies, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failed(DownloadFailureKind.Canceled, "Canceled before probe completed", []);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Probe failed for {Url}", request.Url);
                return Failed(DownloadFailureKind.ProbeFailed, ex.Message, [], ex);
            }

            if (probe.ContentLength is not { } len)
            {
                return Failed(DownloadFailureKind.UnknownContentLength,
                    "Server did not provide a Content-Length; cannot allocate or split.", []);
            }

            totalBytes = len;
            effectiveUrl = probe.EffectiveUrl;
            serverAdvertisesRanges = probe.AcceptsRanges;

            if (serverAdvertisesRanges)
            {
                var ranges = SegmentSplitter.Split(totalBytes, request.Segments, request.MinSegmentBytes);
                initialTasks = ranges
                    .Select((r, i) => new SegmentTask(
                        Url: effectiveUrl, Index: i,
                        StartByte: r.Start, EndByte: r.End,
                        BytesAlreadyDownloaded: 0,
                        Headers: request.Headers, Cookies: request.Cookies))
                    .ToArray();
                _logger.LogInformation("Probe ok for {Url}: {Bytes} bytes, splitting into {Segments} segments",
                    request.Url, totalBytes, initialTasks.Count);
            }
            else
            {
                initialTasks = new[] { BuildSingleStreamTask(effectiveUrl, totalBytes, request) };
                _logger.LogInformation("Probe says no ranges for {Url}: single-stream {Bytes} bytes",
                    request.Url, totalBytes);
            }
        }

        // ---- Allocate the sparse file (resumes existing .sidmpart automatically) ----

        IDownloadFileWriter writer;
        try
        {
            writer = _writerFactory.Allocate(request.TargetPath, totalBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(DownloadFailureKind.IoError, ex.Message, [], ex);
        }

        try
        {
            // ---- Attempt 1: as planned ----
            var attempt = await RunWorkersAsync(writer, initialTasks, progressSink, cancellationToken);

            // ---- Range-fallback: if any worker saw the server lie about Range, ----
            // ---- cancel everything and retry as a single stream from scratch.    ----
            if (!attempt.IsTerminal && attempt.SawRangeNotHonored && serverAdvertisesRanges)
            {
                _logger.LogWarning("Falling back to single-stream for {Url} after server returned 200 to a Range request", effectiveUrl);
                var fallbackTask = BuildSingleStreamTask(effectiveUrl, totalBytes, request);
                attempt = await RunWorkersAsync(writer, new[] { fallbackTask }, progressSink, cancellationToken);
            }

            // ---- Inspect final outcome ----
            if (cancellationToken.IsCancellationRequested || attempt.AnyCanceled)
            {
                return new DownloadResult(false, null, totalBytes, DownloadFailureKind.Canceled,
                    "Download canceled (paused).", attempt.Snapshots);
            }

            if (attempt.SawRangeNotHonored)
            {
                return new DownloadResult(false, null, totalBytes, DownloadFailureKind.RangeNotHonored,
                    "Server did not honor Range requests and single-stream fallback also failed.", attempt.Snapshots);
            }

            if (attempt.FirstFailure is { Outcome: var outcome and not SegmentOutcome.Completed } fail)
            {
                return new DownloadResult(false, null, totalBytes, DownloadFailureKind.SegmentFailed,
                    $"A segment failed with {outcome}: {fail.Exception?.Message}", attempt.Snapshots, fail.Exception);
            }

            // ---- Finalize ----
            string finalPath;
            try
            {
                finalPath = await writer.FinalizeAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(DownloadFailureKind.IoError, ex.Message, attempt.Snapshots, ex);
            }

            // ---- Optional integrity check ----
            if (request.ExpectedHash is { Length: > 0 } expected && request.HashAlgo is { Length: > 0 } algo)
            {
                var actual = await ComputeHashAsync(finalPath, algo, cancellationToken);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return new DownloadResult(false, finalPath, totalBytes, DownloadFailureKind.HashMismatch,
                        $"{algo} mismatch: expected {expected}, got {actual}", attempt.Snapshots);
                }
            }

            _logger.LogInformation("Download complete: {Path} ({Bytes} bytes)", finalPath, totalBytes);
            return new DownloadResult(true, finalPath, totalBytes, DownloadFailureKind.None, null, attempt.Snapshots);
        }
        finally
        {
            await writer.DisposeAsync();
        }
    }

    private static SegmentTask BuildSingleStreamTask(Uri url, long totalBytes, DownloadRequest request) => new(
        Url: url,
        Index: 0,
        StartByte: 0,
        EndByte: totalBytes - 1,
        BytesAlreadyDownloaded: 0,
        Headers: request.Headers,
        Cookies: request.Cookies)
    { RequestRange = false };

    private async Task<AttemptResult> RunWorkersAsync(
        IDownloadFileWriter writer,
        IReadOnlyList<SegmentTask> tasks,
        ISegmentProgressSink progressSink,
        CancellationToken cancellationToken)
    {
        var workerLogger = _loggerFactory.CreateLogger<SegmentWorker>();
        var worker = new SegmentWorker(_httpClientFactory, writer, workerLogger);
        var workerTasks = tasks.Select(t => worker.RunAsync(t, progressSink, cancellationToken)).ToArray();

        SegmentResult[] results;
        try { results = await Task.WhenAll(workerTasks); }
        catch (Exception) { results = await Task.WhenAll(workerTasks.Select(WaitNoThrow)); }

        return new AttemptResult(tasks, results);
    }

    private sealed record AttemptResult(IReadOnlyList<SegmentTask> Tasks, IReadOnlyList<SegmentResult> Results)
    {
        public IReadOnlyList<SegmentSnapshot> Snapshots => BuildSnapshots(Tasks, Results);
        public bool AnyCanceled => Results.Any(r => r.Outcome == SegmentOutcome.Canceled);
        public bool SawRangeNotHonored => Results.Any(r => r.Outcome == SegmentOutcome.RangeNotHonored);
        public SegmentResult? FirstFailure => Results.FirstOrDefault(r => !r.IsSuccess);

        /// <summary>True if the result is so bad it shouldn't trigger a fallback retry.</summary>
        public bool IsTerminal => AnyCanceled;
    }

    private static async Task<SegmentResult> WaitNoThrow(Task<SegmentResult> task)
    {
        try { return await task; }
        catch (Exception ex) { return new SegmentResult(SegmentOutcome.NetworkError, 0, ex); }
    }

    private static IReadOnlyList<SegmentSnapshot> BuildSnapshots(IReadOnlyList<SegmentTask> tasks, IReadOnlyList<SegmentResult> results) =>
        tasks.Zip(results, (t, r) => new SegmentSnapshot(
            Index: t.Index,
            StartByte: t.StartByte,
            EndByte: t.EndByte,
            BytesDownloaded: t.BytesAlreadyDownloaded + r.BytesDownloadedThisRun,
            LastOutcome: r.Outcome)).ToArray();

    private static DownloadResult Failed(
        DownloadFailureKind kind,
        string message,
        IReadOnlyList<SegmentSnapshot> snapshots,
        Exception? ex = null) =>
        new(Success: false, FinalPath: null, TotalBytes: 0, FailureKind: kind, FailureMessage: message,
            Segments: snapshots, Exception: ex);

    private static async Task<string> ComputeHashAsync(string path, string algo, CancellationToken ct)
    {
        using HashAlgorithm hasher = algo.ToLowerInvariant() switch
        {
            "md5" => MD5.Create(),
            "sha1" => SHA1.Create(),
            "sha256" => SHA256.Create(),
            "sha512" => SHA512.Create(),
            _ => throw new NotSupportedException($"Unsupported hash algorithm: {algo}"),
        };

        await using var stream = File.OpenRead(path);
        var hash = await hasher.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
