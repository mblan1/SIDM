using SIDM.Core.Http;

namespace SIDM.Core.Abstractions;

public interface IRangeProbe
{
    Task<ProbeResult> ProbeAsync(
        Uri url,
        IReadOnlyDictionary<string, string>? requestHeaders,
        IReadOnlyDictionary<string, string>? cookies,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lightweight HEAD-only probe that returns just the server-suggested
    /// file name (from <c>Content-Disposition</c>, falling back to the URL's
    /// last path segment). Used at dialog-open time to recover the real
    /// name for CDN-redirect URLs (e.g. GitHub release assets whose URL ends
    /// in a GUID). Returns null when nothing better than the URL guess is
    /// available. Never throws — network failures resolve to null.
    /// </summary>
    Task<string?> ProbeFileNameAsync(
        Uri url,
        IReadOnlyDictionary<string, string>? requestHeaders,
        IReadOnlyDictionary<string, string>? cookies,
        CancellationToken cancellationToken);
}
