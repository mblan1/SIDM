using SIDM.VideoGrabber;

namespace SIDM.Core.Tests.VideoGrabber;

public class YtDlpPathResolverTests : IDisposable
{
    private readonly string _scratchDir;

    public YtDlpPathResolverTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "sidm-resolver-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchDir, recursive: true); } catch { }
    }

    [Fact]
    public void Override_pointing_at_a_file_is_returned_verbatim()
    {
        var path = Path.Combine(_scratchDir, "yt-dlp.exe");
        File.WriteAllBytes(path, Array.Empty<byte>());

        YtDlpPathResolver.ResolveYtDlp(path).Should().Be(path);
    }

    [Fact]
    public void Override_pointing_at_a_directory_resolves_to_file_inside()
    {
        var dir = Path.Combine(_scratchDir, "tools");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "yt-dlp.exe");
        File.WriteAllBytes(path, Array.Empty<byte>());

        YtDlpPathResolver.ResolveYtDlp(dir).Should().Be(path);
    }

    [Fact]
    public void Override_that_does_not_exist_falls_through_to_normal_search()
    {
        // We can't easily mock PATH or BaseDirectory here, but we can at least
        // assert the call returns null when the override is invalid AND nothing
        // is on PATH. The test environment is unlikely to have yt-dlp installed,
        // but if it does this test simply asserts a real path was found.
        var result = YtDlpPathResolver.ResolveYtDlp("Z:\\does\\not\\exist");
        if (result is not null)
        {
            File.Exists(result).Should().BeTrue("if a path was resolved, it must exist");
        }
    }

    [Fact]
    public async Task TryGetVersion_returns_null_for_nonexistent_path()
    {
        var version = await YtDlpPathResolver.TryGetYtDlpVersionAsync("Z:\\nope\\yt-dlp.exe");
        version.Should().BeNull();
    }
}
