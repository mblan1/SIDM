using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SIDM.Core.Abstractions;
using SIDM.Core.Http;

namespace SIDM.Core.Engine;

/// <summary>
/// Downloads one segment (byte range) of a file. Issues a ranged GET, validates the
/// server returned <c>206 Partial Content</c>, and streams the response body into the
/// shared <see cref="IDownloadFileWriter"/> at the correct absolute offset.
///
/// Retry of transient HTTP failures is delegated to the named <see cref="HttpClient"/>'s
/// Polly handler (registered in <see cref="HttpClientServiceCollectionExtensions"/>).
/// </summary>
public sealed class SegmentWorker
{
    /// <summary>Bytes copied between progress reports (also drives the persistence cadence).</summary>
    public const int ProgressFlushBytes = 256 * 1024;

    /// <summary>Wall-clock delay between forced progress reports even if the byte threshold isn't hit.</summary>
    public static readonly TimeSpan ProgressFlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDownloadFileWriter _writer;
    private readonly ILogger<SegmentWorker> _logger;

    public SegmentWorker(
        IHttpClientFactory httpClientFactory,
        IDownloadFileWriter writer,
        ILogger<SegmentWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _writer = writer;
        _logger = logger;
    }

    public async Task<SegmentResult> RunAsync(
        SegmentTask task,
        ISegmentProgressSink? progressSink = null,
        CancellationToken cancellationToken = default)
    {
        progressSink ??= NullProgressSink.Instance;

        if (task.IsAlreadyComplete)
        {
            return new SegmentResult(SegmentOutcome.Completed, BytesDownloadedThisRun: 0);
        }

        var client = _httpClientFactory.CreateClient(DownloadHttpClient.Name);
        using var request = BuildRequest(task);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SegmentResult(SegmentOutcome.Canceled, BytesDownloadedThisRun: 0);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Segment {Idx} network error fetching {Url}", task.Index, task.Url);
            return new SegmentResult(SegmentOutcome.NetworkError, 0, ex);
        }

        using (response)
        {
            // Single-stream fallback path: we deliberately did NOT send a Range header,
            // so 200 OK is the expected success status.
            if (!task.RequestRange)
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return new SegmentResult(
                        SegmentOutcome.HttpError, 0,
                        new HttpRequestException($"Single-stream: expected 200 OK, got {(int)response.StatusCode} {response.StatusCode}"));
                }
            }
            else
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    _logger.LogWarning("Segment {Idx}: server returned 200 OK to a Range request — range support is broken on this resource", task.Index);
                    return new SegmentResult(SegmentOutcome.RangeNotHonored, 0);
                }

                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    _logger.LogWarning("Segment {Idx}: unexpected status {Status}", task.Index, response.StatusCode);
                    return new SegmentResult(
                        SegmentOutcome.HttpError, 0,
                        new HttpRequestException($"Expected 206 Partial Content, got {(int)response.StatusCode} {response.StatusCode}"));
                }
            }

            try
            {
                var bytesThisRun = await StreamToFileAsync(response, task, progressSink, cancellationToken);
                return new SegmentResult(SegmentOutcome.Completed, bytesThisRun);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new SegmentResult(SegmentOutcome.Canceled, BytesDownloadedThisRun: 0);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                _logger.LogWarning(ex, "Segment {Idx} stream interrupted", task.Index);
                return new SegmentResult(SegmentOutcome.NetworkError, 0, ex);
            }
        }
    }

    private static HttpRequestMessage BuildRequest(SegmentTask task)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, task.Url);

        if (task.Headers is not null)
        {
            foreach (var (k, v) in task.Headers)
            {
                if (!req.Headers.TryAddWithoutValidation(k, v))
                {
                    req.Content?.Headers.TryAddWithoutValidation(k, v);
                }
            }
        }

        if (task.Cookies is { Count: > 0 })
        {
            var cookieHeader = string.Join("; ", task.Cookies.Select(kv => $"{kv.Key}={kv.Value}"));
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        if (task.RequestRange)
        {
            var range = task.RemainingRange;
            req.Headers.Range = new RangeHeaderValue(range.Start, range.End);
        }
        return req;
    }

    private async Task<long> StreamToFileAsync(
        HttpResponseMessage response,
        SegmentTask task,
        ISegmentProgressSink progressSink,
        CancellationToken cancellationToken)
    {
        // Validate Content-Range matches what we asked for (defense vs. proxies that
        // honor Range but return the wrong window).
        if (response.Content.Headers.ContentRange is { } cr)
        {
            var expected = task.RemainingRange;
            if (cr.From != expected.Start || cr.To != expected.End)
            {
                _logger.LogWarning(
                    "Segment {Idx}: server returned Content-Range {From}-{To} but we asked for {ExpFrom}-{ExpTo}",
                    task.Index, cr.From, cr.To, expected.Start, expected.End);
            }
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            long bytesThisRun = 0;
            long writeOffset = task.StartByte + task.BytesAlreadyDownloaded;
            long bytesSinceFlush = 0;
            var lastFlush = DateTime.UtcNow;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = await stream.ReadAsync(rented.AsMemory(), cancellationToken);
                if (read == 0) break;

                await _writer.WriteAtAsync(writeOffset, rented.AsMemory(0, read), cancellationToken);

                writeOffset += read;
                bytesThisRun += read;
                bytesSinceFlush += read;

                if (bytesSinceFlush >= ProgressFlushBytes ||
                    DateTime.UtcNow - lastFlush >= ProgressFlushInterval)
                {
                    progressSink.Report(task.Index, task.BytesAlreadyDownloaded + bytesThisRun);
                    bytesSinceFlush = 0;
                    lastFlush = DateTime.UtcNow;
                }
            }

            // Final flush regardless of thresholds.
            progressSink.Report(task.Index, task.BytesAlreadyDownloaded + bytesThisRun);
            return bytesThisRun;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
