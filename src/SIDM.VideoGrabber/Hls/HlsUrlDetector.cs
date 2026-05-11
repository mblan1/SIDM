namespace SIDM.VideoGrabber.Hls;

/// <summary>
/// Recognizes URLs that point at an HLS playlist by file extension. This is
/// the simple, fast check used at intake time — the more thorough variant
/// (content-type / first-bytes sniff) belongs to the future browser
/// extension's media sniffer, not the intake popup.
/// </summary>
public static class HlsUrlDetector
{
    public static bool IsHlsUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not "http" and not "https") return false;

        var path = uri.AbsolutePath;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
    }
}
