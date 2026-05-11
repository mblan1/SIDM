using SIDM.VideoGrabber;

namespace SIDM.Core.Tests.VideoGrabber;

public class YouTubeUrlDetectorTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=abc")]
    [InlineData("https://music.youtube.com/playlist?list=xyz")]
    [InlineData("https://www.youtube.com/shorts/abcdef")]
    [InlineData("https://vimeo.com/12345678")]
    [InlineData("https://www.twitch.tv/videos/123")]
    [InlineData("https://dailymotion.com/video/x9abc")]
    [InlineData("https://www.tiktok.com/@user/video/1234")]
    [InlineData("https://www.facebook.com/watch?v=123")]
    [InlineData("https://www.instagram.com/reel/abc")]
    public void Recognizes_known_video_hosts(string url)
    {
        YouTubeUrlDetector.IsVideoUrl(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://example.com/installer.exe")]
    [InlineData("https://github.com/anthropics/repo/releases/download/v1/file.zip")]
    [InlineData("https://www.facebook.com/profile.php?id=123", false)] // not /watch
    [InlineData("https://instagram.com/some-profile", false)]            // not /reel
    public void Rejects_non_video_urls(string url, bool _ = false)
    {
        YouTubeUrlDetector.IsVideoUrl(url).Should().BeFalse();
    }

    [Fact]
    public void Rejects_garbage_input()
    {
        YouTubeUrlDetector.IsVideoUrl("").Should().BeFalse();
        YouTubeUrlDetector.IsVideoUrl("not a url").Should().BeFalse();
        YouTubeUrlDetector.IsVideoUrl("ftp://youtube.com/").Should().BeFalse();
    }
}
