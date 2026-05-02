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
/// Phase 1 scope: happy path + cancellation + integrity check. Range-fallback
/// (server returns 200 to a Range request mid-download) and work-stealing on
/// segment failure are TODO and will be added before Phase 1 acceptance.
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

        // 1. Probe (skip if we're resuming — caller knows the geometry).
        long totalBytes;
        IReadOnlyList<ByteRange> ranges;
        IReadOnlyList<SegmentTask> tasks;

        if (request.Resume is { Count: > 0 })
        {
            totalBytes = request.Resume[^1].EndByte + 1;
            ranges = request.Resume
                .Select(r => new ByteRange(r.StartByte, r.EndByte))
                .ToArray();
            tasks = request.Resume
                .Select(r => new SegmentTask(
                    Url: request.Url,
                    Index: r.Index,
                    StartByte: r.StartByte,
                    EndByte: r.EndByte,
                    BytesAlreadyDownloaded: r.BytesAlreadyDownloaded,
                    Headers: request.Headers,
                    Cookies: request.Cookies))
                .ToArray();
            _logger.LogInformation("Resuming {Url} with {Count} segments", request.Url, tasks.Count);
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
            var requestedSegments = probe.AcceptsRanges ? request.Segments : 1;
            ranges = SegmentSplitter.Split(totalBytes, requestedSegments, request.MinSegmentBytes);
            tasks = ranges
                .Select((r, i) => new SegmentTask(
                    Url: probe.EffectiveUrl,
                    Index: i,
                    StartByte: r.Start,
                    EndByte: r.End,
                    BytesAlreadyDownloaded: 0,
                    Headers: request.Headers,
                    Cookies: request.Cookies))
                .ToArray();

            _logger.LogInformation(
                "Probe ok for {Url}: {Bytes} bytes, ranges={Ranges}, splitting into {Segments} segments",
                request.Url, totalBytes, probe.AcceptsRanges, tasks.Count);
        }

        // 2. Allocate sparse file (resumes existing .sidmpart automatically).
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
            // 3. Run all workers in parallel and collect results.
            var workerLogger = _loggerFactory.CreateLogger<SegmentWorker>();
            var worker = new SegmentWorker(_httpClientFactory, writer, workerLogger);

            var workerTasks = tasks.Select(t => worker.RunAsync(t, progressSink, cancellationToken)).ToArray();

            SegmentResult[] results;
            try
            {
                results = await Task.WhenAll(workerTasks);
            }
            catch (Exception)
            {
                // Individual SegmentWorkers translate exceptions into SegmentResult, so
                // an exception here means the awaiter itself faulted (rare). Fall through
                // to per-result inspection.
                results = await Task.WhenAll(workerTasks.Select(WaitNoThrow));
            }

            var snapshots = BuildSnapshots(tasks, results);

            // Inspect outcomes
            if (cancellationToken.IsCancellationRequested ||
                results.Any(r => r.Outcome == SegmentOutcome.Canceled))
            {
                return new DownloadResult(false, null, totalBytes, DownloadFailureKind.Canceled,
                    "Download canceled (paused).", snapshots);
            }

            if (results.Any(r => r.Outcome == SegmentOutcome.RangeNotHonored))
            {
                // TODO Phase 1.G+: fall back to single-stream automatically.
                return new DownloadResult(false, null, totalBytes, DownloadFailureKind.RangeNotHonored,
                    "Server stopped honoring Range headers mid-download.", snapshots);
            }

            var firstFail = results.FirstOrDefault(r => !r.IsSuccess);
            if (firstFail is { Outcome: var outcome and not SegmentOutcome.Completed })
            {
                return new DownloadResult(false, null, totalBytes, DownloadFailureKind.SegmentFailed,
                    $"A segment failed with {outcome}: {firstFail.Exception?.Message}", snapshots, firstFail.Exception);
            }

            // 4. Finalize: rename .sidmpart -> target (collision-safe).
            string finalPath;
            try
            {
                finalPath = await writer.FinalizeAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(DownloadFailureKind.IoError, ex.Message, snapshots, ex);
            }

            // 5. Optional integrity check.
            if (request.ExpectedHash is { Length: > 0 } expected && request.HashAlgo is { Length: > 0 } algo)
            {
                var actual = await ComputeHashAsync(finalPath, algo, cancellationToken);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return new DownloadResult(false, finalPath, totalBytes, DownloadFailureKind.HashMismatch,
                        $"{algo} mismatch: expected {expected}, got {actual}", snapshots);
                }
            }

            _logger.LogInformation("Download complete: {Path} ({Bytes} bytes)", finalPath, totalBytes);
            return new DownloadResult(true, finalPath, totalBytes, DownloadFailureKind.None, null, snapshots);
        }
        finally
        {
            await writer.DisposeAsync();
        }
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
