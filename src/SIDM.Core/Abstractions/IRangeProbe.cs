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
    /// Lightweight probe that returns just the server-suggested file name from
    /// <c>Content-Disposition</c>. Tries a HEAD first, then falls back to a
    /// ranged GET (<c>bytes=0-0</c>) because many file hosts — e.g. the
    /// link-shortener → CDN shape whose URL ends in an opaque id — only emit
    /// <c>Content-Disposition</c> on a GET, which is what the browser sees.
    /// Used at dialog-open time to recover the real name for CDN-redirect URLs
    /// (GitHub release assets, signed S3 links, short links). Returns null when
    /// no header name is available so the caller keeps its own URL guess.
    /// Never throws — network failures resolve to null.
    /// </summary>
    Task<string?> ProbeFileNameAsync(
        Uri url,
        IReadOnlyDictionary<string, string>? requestHeaders,
        IReadOnlyDictionary<string, string>? cookies,
        CancellationToken cancellationToken);
}
