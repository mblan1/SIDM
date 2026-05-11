using SIDM.VideoGrabber.Dash;

namespace SIDM.Core.Tests.VideoGrabber.Dash;

public class DashUrlDetectorTests
{
    [Theory]
    [InlineData("https://cdn.example.com/v/manifest.mpd")]
    [InlineData("https://cdn.example.com/v/manifest.MPD?token=abc")]
    public void Recognizes_mpd_urls(string url) =>
        DashUrlDetector.IsDashUrl(url).Should().BeTrue();

    [Theory]
    [InlineData("https://example.com/video.mp4")]
    [InlineData("https://example.com/playlist.m3u8")]
    [InlineData("https://example.com/")]
    [InlineData("")]
    public void Rejects_non_dash_urls(string url) =>
        DashUrlDetector.IsDashUrl(url).Should().BeFalse();
}
