using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using SIDM.Core.Engine;
using SIDM.Core.Http;
using SIDM.Core.Tests.Http;

namespace SIDM.Core.Tests.Engine;

public class SegmentWorkerTests : IDisposable
{
    private readonly string _scratchDir;

    public SegmentWorkerTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "sidm-worker-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchDir, recursive: true); }
        catch { }
    }

    private string PathFor(string name) => Path.Combine(_scratchDir, name);

    [Fact]
    public async Task Run_streams_206_response_to_correct_offset()
    {
        var fullData = new byte[4096];
        new Random(1).NextBytes(fullData);

        // Worker is responsible for byte range [1024, 2047] (1024 bytes).
        var segmentBytes = fullData.AsMemory(1024, 1024).ToArray();

        var handler = new FakeHttpMessageHandler(req =>
        {
            req.Headers.Range!.ToString().Should().Be("bytes=1024-2047");
            return PartialContent(segmentBytes, start: 1024, end: 2047, total: 4096);
        });

        var target = PathFor("range.bin");
        await using var writer = SparseFileWriter.Allocate(target, totalBytes: 4096, NullLogger<SparseFileWriter>.Instance);

        var worker = new SegmentWorker(
            FakeHttpMessageHandler.ToFactory(DownloadHttpClient.Name, handler),
            writer,
            NullLogger<SegmentWorker>.Instance);

        var task = new SegmentTask(
            Url: new Uri("https://example.test/file.bin"),
            Index: 1,
            StartByte: 1024,
            EndByte: 2047,
            BytesAlreadyDownloaded: 0,
            Headers: null,
            Cookies: null);

        var result = await worker.RunAsync(task);
        result.Outcome.Should().Be(SegmentOutcome.Completed);
        result.BytesDownloadedThisRun.Should().Be(1024);

        await writer.FinalizeAsync(CancellationToken.None);
        var actual = await File.ReadAllBytesAsync(target);
        actual.AsSpan(1024, 1024).ToArray().Should().Equal(segmentBytes);
        // Bytes outside our range should still be all zeros.
        actual.AsSpan(0, 1024).ToArray().Should().AllBeEquivalentTo((byte)0);
        actual.AsSpan(2048, 2048).ToArray().Should().AllBeEquivalentTo((byte)0);
    }

    [Fact]
    public async Task Run_returns_RangeNotHonored_when_server_responds_200_to_Range_request()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[1024]),
        });

        await using var writer = SparseFileWriter.Allocate(PathFor("nope.bin"), 1024, NullLogger<SparseFileWriter>.Instance);
        var worker = new SegmentWorker(
            FakeHttpMessageHandler.ToFactory(DownloadHttpClient.Name, handler),
            writer,
            NullLogger<SegmentWorker>.Instance);

        var result = await worker.RunAsync(new SegmentTask(
            new Uri("https://example.test/x"), 0, 0, 1023, 0, null, null));

        result.Outcome.Should().Be(SegmentOutcome.RangeNotHonored);
        result.BytesDownloadedThisRun.Should().Be(0);
    }

    [Fact]
    public async Task Run_returns_HttpError_for_non_206_non_200_status()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await using var writer = SparseFileWriter.Allocate(PathFor("404.bin"), 1024, NullLogger<SparseFileWriter>.Instance);
        var worker = new SegmentWorker(
            FakeHttpMessageHandler.ToFactory(DownloadHttpClient.Name, handler),
            writer,
            NullLogger<SegmentWorker>.Instance);

        var result = await worker.RunAsync(new SegmentTask(
            new Uri("https://example.test/x"), 0, 0, 1023, 0, null, null));

        result.Outcome.Should().Be(SegmentOutcome.HttpError);
        result.Exception.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task Run_resumes_from_BytesAlreadyDownloaded_with_correct_Range_header()
    {
        var fullData = new byte[2048];
        new Random(2).NextBytes(fullData);

        // Segment owns [0, 2047]; pretend we already have 1024 bytes — only fetch the rest.
        var remainingBytes = fullData.AsMemory(1024, 1024).ToArray();

        var capturedRange = "";
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedRange = req.Headers.Range!.ToString();
            return PartialContent(remainingBytes, start: 1024, end: 2047, total: 2048);
        });

        var target = PathFor("resume.bin");
        await using var writer = SparseFileWriter.Allocate(target, 2048, NullLogger<SparseFileWriter>.Instance);
        // Pre-populate first half so the final file matches.
        await writer.WriteAtAsync(0, fullData.AsMemory(0, 1024), CancellationToken.None);

        var worker = new SegmentWorker(
            FakeHttpMessageHandler.ToFactory(DownloadHttpClient.Name, handler),
            writer,
            NullLogger<SegmentWorker>.Instance);

        var task = new SegmentTask(
            Url: new Uri("https://example.test/r"),
            Index: 0, StartByte: 0, EndByte: 2047, BytesAlreadyDownloaded: 1024,
            Headers: null, Cookies: null);

        var result = await worker.RunAsync(task);
        result.Outcome.Should().Be(SegmentOutcome.Completed);
        result.BytesDownloadedThisRun.Should().Be(1024);
        capturedRange.Should().Be("bytes=1024-2047");

        await writer.FinalizeAsync(CancellationToken.None);
        (await File.ReadAllBytesAsync(target)).Should().Equal(fullData);
    }

    [Fact]
    public async Task Run_short_circuits_when_segment_already_complete()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called"));

        await using var writer = SparseFileWriter.Allocate(PathFor("done.bin"), 100, NullLogger<SparseFileWriter>.Instance);
        var worker = new SegmentWorker(
            FakeHttpMessageHandler.ToFactory(DownloadHttpClient.Name, handler),
            writer,
            NullLogger<SegmentWorker>.Instance);

        var task = new SegmentTask(
            Url: new Uri("https://example.test/x"),
            Index: 0, StartByte: 0, EndByte: 99, BytesAlreadyDownloaded: 100,
            Headers: null, Cookies: null);

        var result = await worker.RunAsync(task);
        result.Outcome.Should().Be(SegmentOutcome.Completed);
        result.BytesDownloadedThisRun.Should().Be(0);
    }

    [Fact]
    public async Task Run_reports_progress_via_sink()
    {
        var data = new byte[ProgressFlushBytesPlusABit];
        new Random(3).NextBytes(data);

        var handler = new FakeHttpMessageHandler(_ =>
            PartialContent(data, 0, data.Length - 1, total: data.Length));

        await using var writer = SparseFileWriter.Allocate(PathFor("progress.bin"), data.Length, NullLogger<SparseFileWriter>.Instance);
        var worker = new SegmentWorker(
            FakeHttpMessageHandler.ToFactory(DownloadHttpClient.Name, handler),
            writer,
            NullLogger<SegmentWorker>.Instance);

        var sink = new RecordingProgressSink();

        var task = new SegmentTask(
            Url: new Uri("https://example.test/p"),
            Index: 5, StartByte: 0, EndByte: data.Length - 1, BytesAlreadyDownloaded: 0,
            Headers: null, Cookies: null);

        var result = await worker.RunAsync(task, sink);
        result.Outcome.Should().Be(SegmentOutcome.Completed);

        sink.Reports.Should().NotBeEmpty();
        sink.Reports.Should().AllSatisfy(r => r.SegmentIndex.Should().Be(5));
        sink.Reports[^1].Bytes.Should().Be(data.Length, "final report must cover all bytes");
        sink.Reports.Should().BeInAscendingOrder(r => r.Bytes);
    }

    private const int ProgressFlushBytesPlusABit = SegmentWorker.ProgressFlushBytes * 3 + 7;

    [Fact]
    public async Task Run_returns_Canceled_when_token_canceled_before_send()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(new byte[1024]),
        });

        await using var writer = SparseFileWriter.Allocate(PathFor("cancel.bin"), 1024, NullLogger<SparseFileWriter>.Instance);
        var worker = new SegmentWorker(
            FakeHttpMessageHandler.ToFactory(DownloadHttpClient.Name, handler),
            writer,
            NullLogger<SegmentWorker>.Instance);

        var result = await worker.RunAsync(
            new SegmentTask(new Uri("https://example.test/c"), 0, 0, 1023, 0, null, null),
            cancellationToken: cts.Token);

        result.Outcome.Should().Be(SegmentOutcome.Canceled);
    }

    private static HttpResponseMessage PartialContent(byte[] body, long start, long end, long total)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(body),
        };
        resp.Content.Headers.ContentLength = body.Length;
        resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, total);
        return resp;
    }

    private sealed record ProgressEvent(int SegmentIndex, long Bytes);
    private sealed class RecordingProgressSink : ISegmentProgressSink
    {
        public List<ProgressEvent> Reports { get; } = new();
        public void Report(int segmentIndex, long bytesDownloaded) =>
            Reports.Add(new ProgressEvent(segmentIndex, bytesDownloaded));
    }
}
