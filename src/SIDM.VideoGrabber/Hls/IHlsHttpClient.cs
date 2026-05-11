namespace SIDM.VideoGrabber.Hls;

/// <summary>
/// Minimal HTTP abstraction the HLS downloader needs. Exists so tests can
/// substitute deterministic fake responses without touching the real
/// <see cref="HttpClient"/>. The production implementation
/// <see cref="HlsHttpClient"/> wraps an <see cref="IHttpClientFactory"/>.
/// </summary>
public interface IHlsHttpClient
{
    /// <summary>Fetches a UTF-8 text resource (playlist file).</summary>
    Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>Fetches a binary resource (segment or key).</summary>
    Task<byte[]> GetBytesAsync(Uri url, CancellationToken cancellationToken);
}
