using Microsoft.Extensions.Logging.Abstractions;
using SIDM.VideoGrabber.Dash;
using SIDM.VideoGrabber.Ffmpeg;
using SIDM.VideoGrabber.Hls;

namespace SIDM.Core.Tests.VideoGrabber.Dash;

public class DashDownloaderTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly FakeHttp _http = new();
    private readonly DashDownloader _downloader;

    public DashDownloaderTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "sidm-dash-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_scratchDir);
        _downloader = new DashDownloader(
            _http,
            new FfmpegRemuxer(NullLogger<FfmpegRemuxer>.Instance),
            NullLogger<DashDownloader>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Video_only_manifest_concatenates_init_plus_segments_to_mp4()
    {
        var mpd = new Uri("https://cdn.test/v/m.mpd");
        _http.Strings[mpd] = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT12S">
          <Period>
            <AdaptationSet mimeType="video/mp4" contentType="video">
              <Representation id="v720" bandwidth="2500000">
                <SegmentTemplate timescale="30000" duration="180000" startNumber="1"
                                 media="$RepresentationID$/$Number$.m4s"
                                 initialization="$RepresentationID$/init.mp4" />
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;
        _http.Bytes[new Uri("https://cdn.test/v/v720/init.mp4")] = Bytes("INIT");
        _http.Bytes[new Uri("https://cdn.test/v/v720/1.m4s")] = Bytes("AAAA");
        _http.Bytes[new Uri("https://cdn.test/v/v720/2.m4s")] = Bytes("BBBB");

        var output = Path.Combine(_scratchDir, "out.mp4");
        var result = await _downloader.DownloadAsync(
            new DashDownloadRequest(mpd, output), null, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.FinalFilePath.Should().Be(output);
        (await File.ReadAllBytesAsync(output)).Should().Equal(Bytes("INITAAAABBBB"));
    }

    [Fact]
    public async Task Video_plus_audio_without_ffmpeg_keeps_separate_files_with_helpful_message()
    {
        var mpd = new Uri("https://cdn.test/va/m.mpd");
        _http.Strings[mpd] = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT6S">
          <Period>
            <AdaptationSet mimeType="video/mp4" contentType="video">
              <Representation id="v" bandwidth="2000000">
                <SegmentTemplate timescale="30000" duration="180000" startNumber="1"
                                 media="$RepresentationID$/$Number$.m4s"
                                 initialization="$RepresentationID$/init.mp4" />
              </Representation>
            </AdaptationSet>
            <AdaptationSet mimeType="audio/mp4" contentType="audio">
              <Representation id="a" bandwidth="128000">
                <SegmentTemplate timescale="48000" duration="288000" startNumber="1"
                                 media="$RepresentationID$/$Number$.m4s"
                                 initialization="$RepresentationID$/init.mp4" />
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;
        _http.Bytes[new Uri("https://cdn.test/va/v/init.mp4")] = Bytes("VI");
        _http.Bytes[new Uri("https://cdn.test/va/v/1.m4s")] = Bytes("V0");
        _http.Bytes[new Uri("https://cdn.test/va/a/init.mp4")] = Bytes("AI");
        _http.Bytes[new Uri("https://cdn.test/va/a/1.m4s")] = Bytes("A0");

        var output = Path.Combine(_scratchDir, "out.mp4");
        var result = await _downloader.DownloadAsync(
            new DashDownloadRequest(mpd, output, FfmpegPath: null), null, CancellationToken.None);

        result.Success.Should().BeTrue("the per-track files are still useful artifacts");
        result.FailureMessage.Should().Contain("ffmpeg is not configured");

        var videoTemp = Path.Combine(_scratchDir, "out.video.mp4");
        var audioTemp = Path.Combine(_scratchDir, "out.audio.mp4");
        (await File.ReadAllBytesAsync(videoTemp)).Should().Equal(Bytes("VIV0"));
        (await File.ReadAllBytesAsync(audioTemp)).Should().Equal(Bytes("AIA0"));
    }

    [Fact]
    public async Task Dynamic_manifest_is_refused()
    {
        var mpd = new Uri("https://cdn.test/live.mpd");
        _http.Strings[mpd] = """<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="dynamic"><Period/></MPD>""";

        var result = await _downloader.DownloadAsync(
            new DashDownloadRequest(mpd, Path.Combine(_scratchDir, "x.mp4")),
            null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("Live");
    }

    [Fact]
    public async Task DRM_manifest_is_refused()
    {
        var mpd = new Uri("https://cdn.test/drm.mpd");
        _http.Strings[mpd] = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S">
          <Period>
            <AdaptationSet mimeType="video/mp4">
              <ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011"/>
              <Representation id="v" bandwidth="1000000"/>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

        var result = await _downloader.DownloadAsync(
            new DashDownloadRequest(mpd, Path.Combine(_scratchDir, "x.mp4")),
            null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("DRM");
    }

    [Fact]
    public async Task Picks_highest_bandwidth_video_representation()
    {
        var mpd = new Uri("https://cdn.test/multi/m.mpd");
        _http.Strings[mpd] = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT6S">
          <Period>
            <AdaptationSet mimeType="video/mp4" contentType="video">
              <SegmentTemplate timescale="30000" duration="180000" startNumber="1"
                               media="$RepresentationID$/$Number$.m4s"
                               initialization="$RepresentationID$/init.mp4" />
              <Representation id="low" bandwidth="500000"/>
              <Representation id="high" bandwidth="3000000"/>
            </AdaptationSet>
          </Period>
        </MPD>
        """;
        _http.Bytes[new Uri("https://cdn.test/multi/high/init.mp4")] = Bytes("H");
        _http.Bytes[new Uri("https://cdn.test/multi/high/1.m4s")] = Bytes("HIGH");
        _http.Bytes[new Uri("https://cdn.test/multi/low/init.mp4")] = Bytes("L");
        _http.Bytes[new Uri("https://cdn.test/multi/low/1.m4s")] = Bytes("LOW");

        var output = Path.Combine(_scratchDir, "best.mp4");
        var result = await _downloader.DownloadAsync(
            new DashDownloadRequest(mpd, output), null, CancellationToken.None);

        result.Success.Should().BeTrue();
        (await File.ReadAllBytesAsync(output)).Should().Equal(Bytes("HHIGH"));
    }

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private sealed class FakeHttp : IHlsHttpClient
    {
        public Dictionary<Uri, string> Strings { get; } = new();
        public Dictionary<Uri, byte[]> Bytes { get; } = new();

        public Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken)
        {
            if (Strings.TryGetValue(url, out var s)) return Task.FromResult(s);
            throw new HttpRequestException($"FakeHttp has no string for {url}");
        }

        public Task<byte[]> GetBytesAsync(Uri url, CancellationToken cancellationToken)
        {
            if (Bytes.TryGetValue(url, out var b)) return Task.FromResult(b);
            throw new HttpRequestException($"FakeHttp has no bytes for {url}");
        }
    }
}
