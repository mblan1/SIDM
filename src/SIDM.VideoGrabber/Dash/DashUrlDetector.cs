namespace SIDM.VideoGrabber.Dash;

/// <summary>
/// Fast intake-time check — URL path ends in <c>.mpd</c>. The browser
/// extension's media sniffer (Phase 4.D) will eventually do a content-type
/// sniff for cases where the URL doesn't betray its kind.
/// </summary>
public static class DashUrlDetector
{
    public static bool IsDashUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not "http" and not "https") return false;

        return uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
    }
}
