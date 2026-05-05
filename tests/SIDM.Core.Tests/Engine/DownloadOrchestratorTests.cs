using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SIDM.Core.Abstractions;
using SIDM.Core.Engine;
using SIDM.Core.Http;
using SIDM.Core.Tests.Http;

namespace SIDM.Core.Tests.Engine;

public class DownloadOrchestratorTests : IDisposable
{
    private readonly string _scratchDir;

    public DownloadOrchestratorTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "sidm-orch-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchDir, recursive: true); } catch { }
    }

    private string PathFor(string n) => Path.Combine(_scratchDir, n);

    [Fact]
    public async Task End_to_end_multi_segment_download_writes_complete_file()
    {
        const int total = 64 * 1024;
        var data = new byte[total];
        new Random(42).NextBytes(data);

        var handler = MakeRangeHonoringHandler(data);
        var orchestrator = BuildOrchestrator(handler, totalBytes: total, acceptsRanges: true);

        var target = PathFor("download.bin");
        var result = await orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = new Uri("https://cdn.test/file.bin"),
            TargetPath = target,
            Segments = 4,
            MinSegmentBytes = 1, // allow 4 segments at this small size
        });

        result.Success.Should().BeTrue($"failure: {result.FailureKind} {result.FailureMessage}");
        result.FinalPath.Should().Be(target);
        result.TotalBytes.Should().Be(total);
        result.Segments.Should().HaveCount(4);
        result.Segments.Sum(s => s.BytesDownloaded).Should().Be(total);

        var actual = await File.ReadAllBytesAsync(target);
        actual.Should().Equal(data);
    }

    [Fact]
    public async Task Single_stream_path_used_when_server_advertises_no_range_support()
    {
        const int total = 4096;
        var data = new byte[total];
        new Random(7).NextBytes(data);

        // Server responds with HEAD (no Accept-Ranges) and 200 OK to plain GET (no Range header).
        var handler = new FakeHttpMessageHandler(req => req.Method switch
        {
            { } m when m == HttpMethod.Head => HeadResponse(total, acceptsRanges: false),
            _ => Ok200(data),
        });

        var orchestrator = BuildOrchestrator(handler, total, acceptsRanges: false);

        var target = PathFor("nor.bin");
        var result = await orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = new Uri("https://cdn.test/nor.bin"),
            TargetPath = target,
            Segments = 8,
            MinSegmentBytes = 1,
        });

        result.Success.Should().BeTrue($"single-stream path should succeed; failure: {result.FailureKind} {result.FailureMessage}");
        result.Segments.Should().ContainSingle("single-stream uses one task covering the whole file");
        result.TotalBytes.Should().Be(total);

        var actual = await File.ReadAllBytesAsync(target);
        actual.Should().Equal(data);
    }

    [Fact]
    public async Task Cancellation_aborts_in_flight_workers_and_returns_Canceled()
    {
        const int total = 1024 * 1024;
        var data = new byte[total];
        new Random(9).NextBytes(data);

        using var firstByteServed = new SemaphoreSlim(0, 1);
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
                return HeadResponse(total, acceptsRanges: true);

            // Long, slowly-streaming response so the cancel can interrupt.
            var range = req.Headers.Range!.Ranges.Single();
            var start = (long)range.From!;
            var end = (long)range.To!;
            var slice = data.AsMemory((int)start, (int)(end - start + 1)).ToArray();
            firstByteServed.Release();
            return PartialContentSlow(slice, start, end, total);
        });

        using var cts = new CancellationTokenSource();
        var orchestrator = BuildOrchestrator(handler, total, acceptsRanges: true);

        var target = PathFor("cancel.bin");
        var executeTask = orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = new Uri("https://cdn.test/c.bin"),
            TargetPath = target,
            Segments = 2,
            MinSegmentBytes = 1,
        }, cancellationToken: cts.Token);

        await firstByteServed.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        var result = await executeTask;

        // The exact FailureKind depends on whether cancellation trips the read or
        // surfaces as a stream-interrupted IOException first — both are valid abort
        // states. What matters: download did not succeed, and the partial file is
        // preserved on disk for a future resume.
        result.Success.Should().BeFalse();
        result.FailureKind.Should().BeOneOf(DownloadFailureKind.Canceled, DownloadFailureKind.SegmentFailed);
        File.Exists(target).Should().BeFalse("target should not exist when aborted");
        File.Exists(target + SparseFileWriter.TempSuffix).Should().BeTrue("partial file remains for resume");
    }

    [Fact]
    public async Task Resume_from_persisted_segment_offsets_completes_file()
    {
        const int total = 16 * 1024;
        var data = new byte[total];
        new Random(11).NextBytes(data);

        var target = PathFor("resume.bin");

        // Pre-create a partial file with the first 8 KiB already written.
        await using (var preWriter = SparseFileWriter.Allocate(target, total, NullLogger<SparseFileWriter>.Instance))
        {
            await preWriter.WriteAtAsync(0, data.AsMemory(0, 8 * 1024), CancellationToken.None);
            // Don't finalize — leave the .sidmpart in place.
        }

        var handler = MakeRangeHonoringHandler(data);
        var orchestrator = BuildOrchestrator(handler, total, acceptsRanges: true);

        var result = await orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = new Uri("https://cdn.test/r.bin"),
            TargetPath = target,
            Segments = 2,
            MinSegmentBytes = 1,
            Resume = new[]
            {
                new ResumeSegment(0, StartByte: 0,         EndByte: 8 * 1024 - 1,  BytesAlreadyDownloaded: 8 * 1024),
                new ResumeSegment(1, StartByte: 8 * 1024,  EndByte: 16 * 1024 - 1, BytesAlreadyDownloaded: 0),
            },
        });

        result.Success.Should().BeTrue($"failure: {result.FailureKind} {result.FailureMessage}");
        var actual = await File.ReadAllBytesAsync(target);
        actual.Should().Equal(data);
    }

    [Fact]
    public async Task Hash_verification_succeeds_when_actual_matches_expected()
    {
        const int total = 4096;
        var data = new byte[total];
        new Random(13).NextBytes(data);
        var sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        var handler = MakeRangeHonoringHandler(data);
        var orchestrator = BuildOrchestrator(handler, total, acceptsRanges: true);

        var result = await orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = new Uri("https://cdn.test/h.bin"),
            TargetPath = PathFor("h.bin"),
            Segments = 4,
            MinSegmentBytes = 1,
            ExpectedHash = sha256,
            HashAlgo = "sha256",
        });

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Hash_mismatch_marks_download_failed()
    {
        const int total = 4096;
        var data = new byte[total];
        new Random(15).NextBytes(data);

        var handler = MakeRangeHonoringHandler(data);
        var orchestrator = BuildOrchestrator(handler, total, acceptsRanges: true);

        var result = await orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = new Uri("https://cdn.test/hbad.bin"),
            TargetPath = PathFor("hbad.bin"),
            Segments = 4,
            MinSegmentBytes = 1,
            ExpectedHash = "00".PadRight(64, '0'),
            HashAlgo = "sha256",
        });

        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(DownloadFailureKind.HashMismatch);
    }

    [Fact]
    public async Task Probe_failure_returns_ProbeFailed_kind()
    {
        var probeMock = new Mock<IRangeProbe>();
        probeMock.Setup(p => p.ProbeAsync(It.IsAny<Uri>(), It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("DNS failure"));

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var orchestrator = new DownloadOrchestrator(
            probeMock.Object,
            FakeHttpMessageHandler.ToFactory(DownloadHttpClient.Name, handler),
            new SparseFileWriterFactory(NullLogger<SparseFileWriter>.Instance),
            NullLoggerFactory.Instance);

        var result = await orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = new Uri("https://no.test/x"),
            TargetPath = PathFor("p.bin"),
        });

        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(DownloadFailureKind.ProbeFailed);
        result.FailureMessage.Should().Contain("DNS failure");
    }

    private DownloadOrchestrator BuildOrchestrator(FakeHttpMessageHandler handler, long totalBytes, bool acceptsRanges)
    {
        var factory = FakeHttpMessageHandler.ToFactory(DownloadHttpClient.Name, handler);
        var probe = new RangeProbe(factory, NullLogger<RangeProbe>.Instance);
        var writerFactory = new SparseFileWriterFactory(NullLogger<SparseFileWriter>.Instance);
        return new DownloadOrchestrator(probe, factory, writerFactory, NullLoggerFactory.Instance);
    }

    private static FakeHttpMessageHandler MakeRangeHonoringHandler(byte[] data) =>
        new(req =>
        {
            if (req.Method == HttpMethod.Head)
                return HeadResponse(data.Length, acceptsRanges: true);

            var range = req.Headers.Range!.Ranges.Single();
            var start = (long)range.From!;
            var end = range.To ?? data.Length - 1;
            var slice = data.AsMemory((int)start, (int)(end - start + 1)).ToArray();
            return PartialContent(slice, start, end, data.Length);
        });

    private static HttpResponseMessage HeadResponse(long contentLength, bool acceptsRanges)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        resp.Content.Headers.ContentLength = contentLength;
        if (acceptsRanges) resp.Headers.AcceptRanges.Add("bytes");
        return resp;
    }

    private static HttpResponseMessage Ok200(byte[] body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        resp.Content.Headers.ContentLength = body.Length;
        return resp;
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

    private static HttpResponseMessage PartialContentSlow(byte[] body, long start, long end, long total)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new SlowDripContent(body),
        };
        resp.Content.Headers.ContentLength = body.Length;
        resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, total);
        return resp;
    }

    /// <summary>
    /// Streams data slowly so cancellation tests have time to actually fire mid-stream.
    /// </summary>
    private sealed class SlowDripContent : HttpContent
    {
        private readonly byte[] _data;
        public SlowDripContent(byte[] data) { _data = data; }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var i = 0; i < _data.Length; i += 1024)
            {
                await stream.WriteAsync(_data.AsMemory(i, Math.Min(1024, _data.Length - i)));
                await Task.Delay(50);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;
            return true;
        }
    }
}
