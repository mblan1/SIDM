using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using SIDM.VideoGrabber.Hls;

namespace SIDM.Core.Tests.VideoGrabber.Hls;

public class HlsDownloaderTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly FakeHlsHttpClient _http = new();

    public HlsDownloaderTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "sidm-hls-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Plain_media_playlist_concatenates_segments_in_order()
    {
        var playlist = new Uri("https://cdn.test/v/index.m3u8");
        _http.Strings[playlist] = """
        #EXTM3U
        #EXTINF:6,
        s0.ts
        #EXTINF:6,
        s1.ts
        #EXTINF:6,
        s2.ts
        #EXT-X-ENDLIST
        """;
        _http.Bytes[new Uri("https://cdn.test/v/s0.ts")] = Bytes("AAAA");
        _http.Bytes[new Uri("https://cdn.test/v/s1.ts")] = Bytes("BBBB");
        _http.Bytes[new Uri("https://cdn.test/v/s2.ts")] = Bytes("CCCC");

        var output = Path.Combine(_scratchDir, "out.ts");
        var downloader = new HlsDownloader(_http, NullLogger<HlsDownloader>.Instance);

        var result = await downloader.DownloadAsync(
            new HlsDownloadRequest(playlist, output, ParallelSegmentDownloads: 4),
            progress: null,
            cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue();
        result.FinalFilePath.Should().Be(output);
        result.TotalBytes.Should().Be(12);
        (await File.ReadAllBytesAsync(output)).Should().Equal(Bytes("AAAABBBBCCCC"));
    }

    [Fact]
    public async Task Master_playlist_picks_highest_bandwidth_variant()
    {
        var master = new Uri("https://cdn.test/master.m3u8");
        _http.Strings[master] = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=500000
        low.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=3000000
        high.m3u8
        """;
        _http.Strings[new Uri("https://cdn.test/high.m3u8")] = """
        #EXTM3U
        #EXTINF:6,
        h0.ts
        #EXT-X-ENDLIST
        """;
        _http.Strings[new Uri("https://cdn.test/low.m3u8")] = """
        #EXTM3U
        #EXTINF:6,
        l0.ts
        #EXT-X-ENDLIST
        """;
        _http.Bytes[new Uri("https://cdn.test/h0.ts")] = Bytes("HIGH");

        var output = Path.Combine(_scratchDir, "best.ts");
        var downloader = new HlsDownloader(_http, NullLogger<HlsDownloader>.Instance);

        var result = await downloader.DownloadAsync(
            new HlsDownloadRequest(master, output), null, CancellationToken.None);

        result.Success.Should().BeTrue();
        (await File.ReadAllBytesAsync(output)).Should().Equal(Bytes("HIGH"));
    }

    [Fact]
    public async Task Live_stream_is_refused()
    {
        var playlist = new Uri("https://cdn.test/live.m3u8");
        _http.Strings[playlist] = "#EXTM3U\n#EXTINF:6,\nlive0.ts\n"; // no EXT-X-ENDLIST

        var downloader = new HlsDownloader(_http, NullLogger<HlsDownloader>.Instance);
        var result = await downloader.DownloadAsync(
            new HlsDownloadRequest(playlist, Path.Combine(_scratchDir, "x.ts")),
            null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("Live streams", "live streams aren't supported in v1");
    }

    [Fact]
    public async Task Aes128_segments_are_decrypted_before_concat()
    {
        // Encrypt some known plaintext, serve the ciphertext, expect plaintext on disk.
        var key = RandomNumberGenerator.GetBytes(16);
        var seg0Plain = Bytes("hello world segment 0!");
        var seg1Plain = Bytes("hello world segment 1!");

        // Per spec: no explicit IV → IV is the segment's media sequence number.
        var seg0Iv = HlsCrypto.DeriveIvFromSequence(0);
        var seg1Iv = HlsCrypto.DeriveIvFromSequence(1);

        var playlist = new Uri("https://cdn.test/p/index.m3u8");
        var keyUri = new Uri("https://cdn.test/p/k.bin");
        _http.Strings[playlist] = """
        #EXTM3U
        #EXT-X-KEY:METHOD=AES-128,URI="k.bin"
        #EXTINF:6,
        s0.ts
        #EXTINF:6,
        s1.ts
        #EXT-X-ENDLIST
        """;
        _http.Bytes[keyUri] = key;
        _http.Bytes[new Uri("https://cdn.test/p/s0.ts")] = AesEncrypt(seg0Plain, key, seg0Iv);
        _http.Bytes[new Uri("https://cdn.test/p/s1.ts")] = AesEncrypt(seg1Plain, key, seg1Iv);

        var output = Path.Combine(_scratchDir, "decrypted.ts");
        var downloader = new HlsDownloader(_http, NullLogger<HlsDownloader>.Instance);

        var result = await downloader.DownloadAsync(
            new HlsDownloadRequest(playlist, output), null, CancellationToken.None);

        result.Success.Should().BeTrue();
        var combined = seg0Plain.Concat(seg1Plain).ToArray();
        (await File.ReadAllBytesAsync(output)).Should().Equal(combined);
    }

    [Fact]
    public async Task Empty_playlist_fails_with_helpful_message()
    {
        var playlist = new Uri("https://cdn.test/empty.m3u8");
        _http.Strings[playlist] = "#EXTM3U\n#EXT-X-ENDLIST\n";

        var downloader = new HlsDownloader(_http, NullLogger<HlsDownloader>.Instance);
        var result = await downloader.DownloadAsync(
            new HlsDownloadRequest(playlist, Path.Combine(_scratchDir, "x.ts")),
            null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("no segments");
    }

    [Fact]
    public async Task Wrong_size_aes_key_is_reported_clearly()
    {
        var playlist = new Uri("https://cdn.test/bad/index.m3u8");
        _http.Strings[playlist] = """
        #EXTM3U
        #EXT-X-KEY:METHOD=AES-128,URI="k.bin"
        #EXTINF:6,
        s0.ts
        #EXT-X-ENDLIST
        """;
        _http.Bytes[new Uri("https://cdn.test/bad/k.bin")] = new byte[8]; // wrong size

        var downloader = new HlsDownloader(_http, NullLogger<HlsDownloader>.Instance);
        var result = await downloader.DownloadAsync(
            new HlsDownloadRequest(playlist, Path.Combine(_scratchDir, "x.ts")),
            null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("8 bytes");
    }

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static byte[] AesEncrypt(byte[] plaintext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    /// <summary>In-memory stand-in for IHlsHttpClient — populate the dicts, call the downloader.</summary>
    private sealed class FakeHlsHttpClient : IHlsHttpClient
    {
        public Dictionary<Uri, string> Strings { get; } = new();
        public Dictionary<Uri, byte[]> Bytes { get; } = new();

        public Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken)
        {
            if (Strings.TryGetValue(url, out var s)) return Task.FromResult(s);
            throw new HttpRequestException($"FakeHlsHttpClient has no string for {url}");
        }

        public Task<byte[]> GetBytesAsync(Uri url, CancellationToken cancellationToken)
        {
            if (Bytes.TryGetValue(url, out var b)) return Task.FromResult(b);
            throw new HttpRequestException($"FakeHlsHttpClient has no bytes for {url}");
        }
    }
}
