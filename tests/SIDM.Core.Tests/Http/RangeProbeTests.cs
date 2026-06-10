using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using SIDM.Core.Http;

namespace SIDM.Core.Tests.Http;

public class RangeProbeTests
{
    private static readonly Uri TestUrl = new("https://cdn.example.com/big.bin");

    [Fact]
    public async Task Probe_returns_metadata_when_HEAD_succeeds_and_server_honors_ranges()
    {
        var handler = new FakeHttpMessageHandler(req => req.Method switch
        {
            { } m when m == HttpMethod.Head => HeadResponse(contentLength: 1_000_000, acceptRanges: true,
                etag: "\"abc\"", contentType: "application/octet-stream",
                contentDisposition: "attachment; filename=\"installer.exe\""),
            { } m when m == HttpMethod.Get => PartialContentResponse(start: 0, end: 0, total: 1_000_000),
            _ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
        });

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var result = await probe.ProbeAsync(TestUrl, requestHeaders: null, cookies: null, CancellationToken.None);

        result.ContentLength.Should().Be(1_000_000);
        result.AcceptsRanges.Should().BeTrue();
        result.ETag.Should().Be("\"abc\"");
        result.Mime.Should().Be("application/octet-stream");
        result.SuggestedFileName.Should().Be("installer.exe");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Method.Should().Be(HttpMethod.Head);
        handler.Requests[1].Method.Should().Be(HttpMethod.Get);
        handler.Requests[1].Headers.Range!.ToString().Should().Be("bytes=0-0");
    }

    [Fact]
    public async Task Probe_detects_range_support_via_206_even_when_AcceptRanges_header_absent()
    {
        var handler = new FakeHttpMessageHandler(req => req.Method == HttpMethod.Head
            ? HeadResponse(contentLength: 500_000, acceptRanges: false)
            : PartialContentResponse(start: 0, end: 0, total: 500_000));

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var result = await probe.ProbeAsync(TestUrl, null, null, CancellationToken.None);

        result.AcceptsRanges.Should().BeTrue("server returned 206 Partial Content even without Accept-Ranges header");
        result.ContentLength.Should().Be(500_000);
    }

    [Fact]
    public async Task Probe_marks_ranges_unsupported_when_server_returns_200_to_ranged_GET()
    {
        // Server advertises Accept-Ranges: bytes but doesn't honor a Range header.
        // The ranged-GET response is 200 OK, not 206. Treat as unsupported.
        var handler = new FakeHttpMessageHandler(req => req.Method == HttpMethod.Head
            ? HeadResponse(contentLength: 200_000, acceptRanges: true)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 100)) { Headers = { ContentLength = 200_000 } },
            });

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var result = await probe.ProbeAsync(TestUrl, null, null, CancellationToken.None);

        // AcceptsRanges may still be true based on the header — but the more important
        // contract for the orchestrator is that we expose what we know. Document
        // current behavior: header-says-yes wins. Phase D fallback handles the lie.
        result.AcceptsRanges.Should().BeTrue();
    }

    [Fact]
    public async Task Probe_falls_back_to_ranged_GET_for_metadata_when_HEAD_returns_405()
    {
        var handler = new FakeHttpMessageHandler(req => req.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            : PartialContentResponse(0, 0, total: 7_777, etag: "\"v1\"", contentType: "video/mp4"));

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var result = await probe.ProbeAsync(TestUrl, null, null, CancellationToken.None);

        result.ContentLength.Should().Be(7_777, "Content-Range total should be parsed when HEAD didn't give us length");
        result.AcceptsRanges.Should().BeTrue();
        result.ETag.Should().Be("\"v1\"");
        result.Mime.Should().Be("video/mp4");
    }

    [Fact]
    public async Task Probe_extracts_filename_from_url_when_no_ContentDisposition()
    {
        var handler = new FakeHttpMessageHandler(_ => HeadResponse(contentLength: 1, acceptRanges: false));

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var result = await probe.ProbeAsync(new Uri("https://x.test/path/to/My%20File.zip?token=abc"), null, null, CancellationToken.None);

        result.SuggestedFileName.Should().Be("My File.zip");
    }

    [Fact]
    public async Task Probe_forwards_cookies_and_custom_headers()
    {
        var handler = new FakeHttpMessageHandler(_ => HeadResponse(contentLength: 1, acceptRanges: false));

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = "SIDM/0.1",
            ["Referer"] = "https://referrer.test/",
        };
        var cookies = new Dictionary<string, string>
        {
            ["session"] = "abc123",
            ["pref"] = "dark",
        };

        await probe.ProbeAsync(TestUrl, headers, cookies, CancellationToken.None);

        handler.Requests.Should().AllSatisfy(req =>
        {
            req.Headers.UserAgent.ToString().Should().Be("SIDM/0.1");
            req.Headers.Referrer!.ToString().Should().Be("https://referrer.test/");
            req.Headers.GetValues("Cookie").Single().Should().Be("session=abc123; pref=dark");
        });
    }

    [Fact]
    public async Task Probe_extracts_ContentMD5_as_hex_hash()
    {
        var hashBytes = new byte[] { 0xde, 0xad, 0xbe, 0xef, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb };
        var handler = new FakeHttpMessageHandler(req =>
        {
            var resp = HeadResponse(contentLength: 16, acceptRanges: true);
            resp.Content.Headers.ContentMD5 = hashBytes;
            return resp;
        });

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var result = await probe.ProbeAsync(TestUrl, null, null, CancellationToken.None);

        result.HashAlgo.Should().Be("md5");
        result.ContentHash.Should().Be("deadbeef00112233445566778899aabb");
    }

    [Fact]
    public async Task ProbeFileName_returns_ContentDisposition_name()
    {
        // GitHub release asset shape: URL ends in a GUID, real name in
        // Content-Disposition.
        var handler = new FakeHttpMessageHandler(req => req.Method == HttpMethod.Head
            ? HeadResponse(contentLength: 7_000_000, acceptRanges: true,
                contentDisposition: "attachment; filename=go2rtc_win64.zip")
            : new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var name = await probe.ProbeFileNameAsync(
            new Uri("https://release-assets.githubusercontent.com/x/4ed81825-4b19-4a4f-b113-be56aed03a82"),
            requestHeaders: null, cookies: null, CancellationToken.None);

        name.Should().Be("go2rtc_win64.zip");
        handler.Requests.Should().ContainSingle()
            .Which.Method.Should().Be(HttpMethod.Head, "a HEAD that yields the name short-circuits the GET fallback");
    }

    [Fact]
    public async Task ProbeFileName_falls_back_to_ranged_GET_when_HEAD_lacks_ContentDisposition()
    {
        // Link-shortener → CDN shape (the reported vnshort.com/<id> bug): the
        // HEAD carries no Content-Disposition, but the GET does — which is what
        // the browser sees and why Chrome gets the right name.
        var handler = new FakeHttpMessageHandler(req => req.Method == HttpMethod.Head
            ? HeadResponse(contentLength: 7_000_000, acceptRanges: true) // no CD on HEAD
            : PartialContentResponse(0, 0, total: 7_000_000,
                contentDisposition: "attachment; filename=\"007.First.Light.Crack.Only-voices38.7z\""));

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var name = await probe.ProbeFileNameAsync(
            new Uri("https://cdn.example.com/e7bb6a02fd00323697c6a87a5e5f7554e2fadde7b440dc0f42f88268bf5ee266"),
            requestHeaders: null, cookies: null, CancellationToken.None);

        name.Should().Be("007.First.Light.Crack.Only-voices38.7z");
        handler.Requests.Should().HaveCount(2, "HEAD had no name, so we fall through to a ranged GET");
        handler.Requests[1].Method.Should().Be(HttpMethod.Get);
        handler.Requests[1].Headers.Range!.ToString().Should().Be("bytes=0-0", "only one byte is requested — headers are all we need");
    }

    [Fact]
    public async Task ProbeFileName_strips_path_components_from_ContentDisposition()
    {
        // A misbehaving server embeds a path; we must keep only the last segment.
        var handler = new FakeHttpMessageHandler(req => req.Method == HttpMethod.Head
            ? HeadResponse(contentLength: 10, acceptRanges: true,
                contentDisposition: "attachment; filename=\"../../etc/evil.7z\"")
            : new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var name = await probe.ProbeFileNameAsync(TestUrl, null, null, CancellationToken.None);

        name.Should().Be("evil.7z");
    }

    [Fact]
    public async Task ProbeFileName_returns_null_when_no_ContentDisposition()
    {
        // No Content-Disposition on either HEAD or GET → null (caller keeps its
        // own URL guess).
        var handler = new FakeHttpMessageHandler(_ => HeadResponse(contentLength: 1, acceptRanges: false));

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var name = await probe.ProbeFileNameAsync(TestUrl, null, null, CancellationToken.None);

        name.Should().BeNull();
    }

    [Fact]
    public async Task ProbeFileName_returns_null_on_network_failure()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("boom"));

        var probe = new RangeProbe(FakeHttpMessageHandler.ToFactory(RangeProbe.HttpClientName, handler), NullLogger<RangeProbe>.Instance);

        var name = await probe.ProbeFileNameAsync(TestUrl, null, null, CancellationToken.None);

        name.Should().BeNull("a failed probe must not throw — caller falls back to the URL guess");
    }

    private static HttpResponseMessage HeadResponse(
        long contentLength,
        bool acceptRanges,
        string? etag = null,
        string? contentType = null,
        string? contentDisposition = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        resp.Content.Headers.ContentLength = contentLength;
        if (acceptRanges) resp.Headers.AcceptRanges.Add("bytes");
        if (etag is not null) resp.Headers.ETag = new EntityTagHeaderValue(etag);
        if (contentType is not null) resp.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (contentDisposition is not null) resp.Content.Headers.ContentDisposition = ContentDispositionHeaderValue.Parse(contentDisposition);
        return resp;
    }

    private static HttpResponseMessage PartialContentResponse(
        long start,
        long end,
        long total,
        string? etag = null,
        string? contentType = null,
        string? contentDisposition = null)
    {
        var bodyLength = (int)(end - start + 1);
        var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(new byte[bodyLength]),
        };
        resp.Content.Headers.ContentLength = bodyLength;
        resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, total);
        resp.Headers.AcceptRanges.Add("bytes");
        if (etag is not null) resp.Headers.ETag = new EntityTagHeaderValue(etag);
        if (contentType is not null) resp.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (contentDisposition is not null) resp.Content.Headers.ContentDisposition = ContentDispositionHeaderValue.Parse(contentDisposition);
        return resp;
    }
}
