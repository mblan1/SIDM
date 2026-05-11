using SIDM.VideoGrabber.Hls;

namespace SIDM.Core.Tests.VideoGrabber.Hls;

public class HlsUrlDetectorTests
{
    [Theory]
    [InlineData("https://cdn.example.com/video/master.m3u8")]
    [InlineData("https://cdn.example.com/v/playlist.m3u8?token=abc")]
    [InlineData("http://example.com/old.M3U")]
    public void Recognizes_m3u8_urls(string url) =>
        HlsUrlDetector.IsHlsUrl(url).Should().BeTrue();

    [Theory]
    [InlineData("https://example.com/video.mp4")]
    [InlineData("https://example.com/installer.exe")]
    [InlineData("https://example.com/")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Rejects_non_hls_urls(string url) =>
        HlsUrlDetector.IsHlsUrl(url).Should().BeFalse();
}
