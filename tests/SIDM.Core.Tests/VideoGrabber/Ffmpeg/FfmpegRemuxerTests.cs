using Microsoft.Extensions.Logging.Abstractions;
using SIDM.VideoGrabber.Ffmpeg;

namespace SIDM.Core.Tests.VideoGrabber.Ffmpeg;

public class FfmpegRemuxerTests : IDisposable
{
    private readonly string _scratchDir;

    public FfmpegRemuxerTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "sidm-remux-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Returns_NotConfigured_when_ffmpeg_path_is_null()
    {
        var muxer = new FfmpegRemuxer(NullLogger<FfmpegRemuxer>.Instance);
        var input = Path.Combine(_scratchDir, "in.ts");
        await File.WriteAllBytesAsync(input, new byte[16]);

        var result = await muxer.RemuxToMp4Async(input, ffmpegPath: null, CancellationToken.None);

        result.Outcome.Should().Be(RemuxOutcome.NotConfigured);
        File.Exists(input).Should().BeTrue("the source must be left intact when no remux happens");
    }

    [Fact]
    public async Task Returns_NotConfigured_when_ffmpeg_path_does_not_exist()
    {
        var muxer = new FfmpegRemuxer(NullLogger<FfmpegRemuxer>.Instance);
        var input = Path.Combine(_scratchDir, "in.ts");
        await File.WriteAllBytesAsync(input, new byte[16]);

        var result = await muxer.RemuxToMp4Async(input, "Z:\\nope\\ffmpeg.exe", CancellationToken.None);

        result.Outcome.Should().Be(RemuxOutcome.NotConfigured);
        File.Exists(input).Should().BeTrue();
    }
}
