using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SIDM.Core.Abstractions;

namespace SIDM.Core.Http;

public sealed class RangeProbe : IRangeProbe
{
    public const string HttpClientName = "sidm-probe";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RangeProbe> _logger;

    public RangeProbe(IHttpClientFactory httpClientFactory, ILogger<RangeProbe> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ProbeResult> ProbeAsync(
        Uri url,
        IReadOnlyDictionary<string, string>? requestHeaders,
        IReadOnlyDictionary<string, string>? cookies,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // Phase A: HEAD to read metadata. Some servers reject HEAD — fall back to a
        // ranged GET in that case.
        var head = await SendAsync(client, HttpMethod.Head, url, requestHeaders, cookies,
            range: null, cancellationToken);
        var probeUrl = head.RequestMessage?.RequestUri ?? url;

        long? contentLength;
        bool acceptsRangesHeader;
        string? etag;
        string? lastModified;
        string? mime;
        string? fileName;
        string? contentHash;
        string? hashAlgo;

        if (head.IsSuccessStatusCode)
        {
            contentLength = head.Content.Headers.ContentLength;
            acceptsRangesHeader = HasAcceptRangesBytes(head.Headers);
            etag = head.Headers.ETag?.Tag;
            lastModified = head.Content.Headers.LastModified?.ToString("R");
            mime = head.Content.Headers.ContentType?.MediaType;
            fileName = ExtractFilename(head.Content.Headers.ContentDisposition, url);
            (contentHash, hashAlgo) = ExtractHash(head.Content.Headers, head.Headers);
            head.Dispose();
        }
        else
        {
            head.Dispose();
            _logger.LogDebug("HEAD returned {Status} for {Url}; probing with ranged GET", head.StatusCode, url);
            contentLength = null;
            acceptsRangesHeader = false;
            etag = null;
            lastModified = null;
            mime = null;
            fileName = null;
            contentHash = null;
            hashAlgo = null;
        }

        // Phase B: confirm range support. Servers lie about Accept-Ranges; the only
        // reliable test is a ranged GET that returns 206 Partial Content.
        var rangeProbe = await SendAsync(client, HttpMethod.Get, probeUrl, requestHeaders, cookies,
            range: new RangeHeaderValue(0, 0), cancellationToken);

        var rangesActuallyWork = rangeProbe.StatusCode == HttpStatusCode.PartialContent;

        // If the HEAD didn't give us a content length but the ranged GET reveals it
        // (Content-Range: bytes 0-0/12345), parse it.
        if (contentLength is null && rangeProbe.Content.Headers.ContentRange is { HasLength: true } cr)
        {
            contentLength = cr.Length;
        }
        contentLength ??= rangeProbe.Content.Headers.ContentLength;

        if (head.IsSuccessStatusCode == false && rangeProbe.IsSuccessStatusCode)
        {
            etag = rangeProbe.Headers.ETag?.Tag;
            lastModified = rangeProbe.Content.Headers.LastModified?.ToString("R");
            mime = rangeProbe.Content.Headers.ContentType?.MediaType;
            fileName = ExtractFilename(rangeProbe.Content.Headers.ContentDisposition, url);
            (contentHash, hashAlgo) = ExtractHash(rangeProbe.Content.Headers, rangeProbe.Headers);
        }

        rangeProbe.Dispose();

        var effectiveUrl = rangeProbe.RequestMessage?.RequestUri ?? probeUrl;
        return new ProbeResult(
            EffectiveUrl: effectiveUrl,
            ContentLength: contentLength,
            AcceptsRanges: acceptsRangesHeader || rangesActuallyWork,
            ETag: etag,
            LastModified: lastModified,
            Mime: mime,
            SuggestedFileName: fileName,
            ContentHash: contentHash,
            HashAlgo: hashAlgo);
    }

    public async Task<string?> ProbeFileNameAsync(
        Uri url,
        IReadOnlyDictionary<string, string>? requestHeaders,
        IReadOnlyDictionary<string, string>? cookies,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // Phase A: HEAD. Cheapest path — well-behaved servers (GitHub release
        // assets, signed S3 links) return Content-Disposition on a HEAD, so
        // we can recover the real name without touching the body.
        try
        {
            using var head = await SendAsync(client, HttpMethod.Head, url, requestHeaders, cookies,
                range: null, cancellationToken);
            if (head.IsSuccessStatusCode)
            {
                var fromHead = FileNameFromDisposition(head.Content.Headers.ContentDisposition);
                if (fromHead is not null) return fromHead;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Filename HEAD probe failed for {Url}", url);
        }

        // Phase B: ranged GET (bytes=0-0). Many file hosts — notably the
        // link-shortener → CDN shape (e.g. vnshort.com/<id> → cdn/<sha256>) —
        // emit Content-Disposition only on a GET, never on a HEAD. That's why
        // the browser gets the right name and a HEAD-only probe doesn't. We
        // ask for a single byte and read headers only (ResponseHeadersRead +
        // immediate dispose), so the body is never downloaded.
        try
        {
            using var get = await SendAsync(client, HttpMethod.Get, url, requestHeaders, cookies,
                range: new RangeHeaderValue(0, 0), cancellationToken);
            if (get.IsSuccessStatusCode)
            {
                var fromGet = FileNameFromDisposition(get.Content.Headers.ContentDisposition);
                if (fromGet is not null) return fromGet;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Filename GET probe failed for {Url}", url);
        }

        // Nothing better than the URL guess — that's the caller's fallback.
        return null;
    }

    /// <summary>
    /// Extracts a bare, safe file name from a <c>Content-Disposition</c>
    /// header, or null when it carries no usable name. Strips any directory
    /// component a misbehaving server might embed (<c>../</c>, drive paths)
    /// so the result is always a single path segment.
    /// </summary>
    private static string? FileNameFromDisposition(ContentDispositionHeaderValue? cd)
    {
        var name = (cd?.FileNameStar ?? cd?.FileName)?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Defend against a server stuffing a path into the header — keep only
        // the final segment regardless of '/' or '\' separators.
        name = Path.GetFileName(name.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        Uri url,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyDictionary<string, string>? cookies,
        RangeHeaderValue? range,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(method, url);

        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                if (!req.Headers.TryAddWithoutValidation(k, v))
                {
                    req.Content?.Headers.TryAddWithoutValidation(k, v);
                }
            }
        }

        if (cookies is { Count: > 0 })
        {
            var cookieHeader = string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        if (range is not null)
        {
            req.Headers.Range = range;
        }

        return await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static bool HasAcceptRangesBytes(HttpResponseHeaders headers)
    {
        if (!headers.AcceptRanges.Any()) return false;
        return headers.AcceptRanges.Any(v => string.Equals(v, "bytes", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractFilename(ContentDispositionHeaderValue? cd, Uri url)
    {
        var name = cd?.FileNameStar ?? cd?.FileName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim('"');
        }

        var pathName = Path.GetFileName(url.AbsolutePath);
        return string.IsNullOrWhiteSpace(pathName) ? null : Uri.UnescapeDataString(pathName);
    }

    private static (string? hash, string? algo) ExtractHash(HttpContentHeaders content, HttpResponseHeaders response)
    {
        if (content.ContentMD5 is { Length: > 0 } md5)
        {
            return (Convert.ToHexString(md5).ToLowerInvariant(), "md5");
        }

        if (response.TryGetValues("Digest", out var digests))
        {
            foreach (var d in digests)
            {
                var eq = d.IndexOf('=');
                if (eq <= 0) continue;
                var algo = d[..eq].Trim().ToLowerInvariant();
                var value = d[(eq + 1)..].Trim();
                if (algo is "sha-256" or "sha256")
                {
                    return (NormalizeBase64ToHex(value), "sha256");
                }
            }
        }

        return (null, null);
    }

    private static string NormalizeBase64ToHex(string maybeBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(maybeBase64);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch (FormatException)
        {
            return maybeBase64;
        }
    }
}
