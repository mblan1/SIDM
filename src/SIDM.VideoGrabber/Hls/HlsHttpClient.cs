using SIDM.Core.Http;

namespace SIDM.VideoGrabber.Hls;

/// <summary>
/// Production <see cref="IHlsHttpClient"/>. Uses the same named
/// <see cref="HttpClient"/> as the segment workers (which already has the
/// Polly retry policy + decompression + cookies-off configuration), so HLS
/// downloads benefit from the existing resilience plumbing for free.
/// </summary>
public sealed class HlsHttpClient : IHlsHttpClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HlsHttpClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(DownloadHttpClient.Name);
        return await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetBytesAsync(Uri url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(DownloadHttpClient.Name);
        return await client.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
    }
}
