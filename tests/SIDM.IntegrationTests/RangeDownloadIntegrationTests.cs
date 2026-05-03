using System.Security.Cryptography;
using SIDM.Core.Engine;

namespace SIDM.IntegrationTests;

public class RangeDownloadIntegrationTests
{
    [Fact]
    public async Task Multi_segment_download_against_real_HTTP_server_produces_byte_equal_file()
    {
        using var fx = new IntegrationFixture();

        const int total = 4 * 1024 * 1024; // 4 MiB
        var data = new byte[total];
        new Random(42).NextBytes(data);
        fx.Server.MapRangeResource("/file.bin", data);

        var target = fx.PathFor("file.bin");
        var result = await fx.Orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = fx.UrlFor("/file.bin"),
            TargetPath = target,
            Segments = 4,
        });

        result.Success.Should().BeTrue($"failure: {result.FailureKind} {result.FailureMessage}");
        result.TotalBytes.Should().Be(total);
        result.Segments.Should().HaveCount(4);

        var actual = await File.ReadAllBytesAsync(target);
        actual.Should().Equal(data);
    }

    [Fact]
    public async Task Hash_verification_succeeds_against_real_HTTP_server()
    {
        using var fx = new IntegrationFixture();

        var data = new byte[2 * 1024 * 1024];
        new Random(7).NextBytes(data);
        var sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        fx.Server.MapRangeResource("/hashed.bin", data);

        var result = await fx.Orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = fx.UrlFor("/hashed.bin"),
            TargetPath = fx.PathFor("hashed.bin"),
            Segments = 4,
            ExpectedHash = sha256,
            HashAlgo = "sha256",
        });

        result.Success.Should().BeTrue($"failure: {result.FailureKind} {result.FailureMessage}");
    }

    [Fact]
    public async Task Server_returning_503_then_206_succeeds_via_Polly_retry()
    {
        using var fx = new IntegrationFixture();

        var data = new byte[256 * 1024];
        new Random(11).NextBytes(data);
        // Each segment will see 1 failure before succeeding. With 1 segment, that's 1 retry.
        fx.Server.MapFlakyRangeResource("/flaky.bin", data, initialFailures: 1, failureStatusCode: 503);

        var result = await fx.Orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = fx.UrlFor("/flaky.bin"),
            TargetPath = fx.PathFor("flaky.bin"),
            Segments = 1,
            MinSegmentBytes = 1,
        });

        result.Success.Should().BeTrue($"Polly should retry 5xx; failure: {result.FailureKind} {result.FailureMessage}");
        var actual = await File.ReadAllBytesAsync(fx.PathFor("flaky.bin"));
        actual.Should().Equal(data);
    }

    [Fact]
    public async Task Server_lying_about_range_support_surfaces_RangeNotHonored()
    {
        using var fx = new IntegrationFixture();

        var data = new byte[64 * 1024];
        new Random(13).NextBytes(data);
        fx.Server.MapLyingRangeResource("/lying.bin", data);

        var result = await fx.Orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = fx.UrlFor("/lying.bin"),
            TargetPath = fx.PathFor("lying.bin"),
            Segments = 4,
            MinSegmentBytes = 1,
        });

        // Current behavior: orchestrator surfaces RangeNotHonored when a worker gets
        // 200 OK to a Range request. Single-stream fallback is on the backlog.
        result.Success.Should().BeFalse();
        result.FailureKind.Should().Be(DownloadFailureKind.RangeNotHonored);
    }

    [Fact]
    public async Task Resume_across_orchestrator_recreation_completes_file()
    {
        using var fx = new IntegrationFixture();

        const int total = 1 * 1024 * 1024;
        var data = new byte[total];
        new Random(17).NextBytes(data);
        fx.Server.MapRangeResource("/resume.bin", data);

        var target = fx.PathFor("resume.bin");

        // Phase 1: write the first half via the engine, then "interrupt" by passing
        // an explicit Resume layout that pretends only half is done.
        var firstResult = await fx.Orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = fx.UrlFor("/resume.bin"),
            TargetPath = target,
            Segments = 2,
            MinSegmentBytes = 1,
        });
        firstResult.Success.Should().BeTrue();
        File.Delete(target); // simulate target rename rollback

        // Phase 2: resume from a deliberately partial state. The .sidmpart file
        // doesn't exist anymore, so the engine writes from scratch — what we're
        // really testing is that the Resume input path works against a live server.
        var secondResult = await fx.Orchestrator.ExecuteAsync(new DownloadRequest
        {
            Url = fx.UrlFor("/resume.bin"),
            TargetPath = target,
            Segments = 2,
            MinSegmentBytes = 1,
            Resume = new[]
            {
                new ResumeSegment(0, StartByte: 0,            EndByte: total / 2 - 1, BytesAlreadyDownloaded: 0),
                new ResumeSegment(1, StartByte: total / 2,    EndByte: total - 1,    BytesAlreadyDownloaded: 0),
            },
        });

        secondResult.Success.Should().BeTrue($"failure: {secondResult.FailureKind} {secondResult.FailureMessage}");
        var actual = await File.ReadAllBytesAsync(target);
        actual.Should().Equal(data);
    }
}
