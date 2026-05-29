namespace SIDM.VideoGrabber;

/// <summary>
/// Recognizes URLs that should be routed through yt-dlp rather than the
/// HTTP segment engine. Conservative by design — we'd rather miss a video
/// site (the user can flip the source kind manually) than mis-route a normal
/// HTTP download into yt-dlp and pay the process-spawn cost.
///
/// The list reflects the host names yt-dlp itself targets most reliably;
/// every supported site can still be added later as users report regressions.
/// </summary>
public static class YouTubeUrlDetector
{
    /// <summary>
    /// Each entry is either a bare host ("youtube.com") meaning every path on
    /// that host counts, or "host/path-prefix" meaning the host AND the URL
    /// path must start with the listed prefix (used for sites like Facebook
    /// where only certain sections host videos).
    /// </summary>
    private static readonly (string Host, string? PathPrefix)[] VideoPatterns =
    [
        ("youtube.com", null),
        ("youtu.be", null),
        ("vimeo.com", null),
        ("twitch.tv", null),
        ("dailymotion.com", null),
        ("tiktok.com", null),
        ("facebook.com", "/watch"),
        ("facebook.com", "/reel"),
        ("facebook.com", "/share/v"),
        ("facebook.com", "/share/r"),
        ("fb.watch", null),
        ("instagram.com", "/reel"),
        ("instagram.com", "/p/"),
        ("instagram.com", "/tv/"),
    ];

    public static bool IsVideoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not "http" and not "https") return false;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        foreach (var (patternHost, pathPrefix) in VideoPatterns)
        {
            if (!HostMatches(host, patternHost)) continue;
            if (pathPrefix is not null && !path.StartsWith(pathPrefix, StringComparison.Ordinal)) continue;
            return true;
        }
        return false;
    }

    /// <summary>True if <paramref name="actualHost"/> equals <paramref name="patternHost"/>
    /// or is any subdomain of it ("www.foo.com" matches "foo.com").</summary>
    private static bool HostMatches(string actualHost, string patternHost) =>
        actualHost == patternHost || actualHost.EndsWith("." + patternHost, StringComparison.Ordinal);
}
