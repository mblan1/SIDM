using SIDM.VideoGrabber;

namespace SIDM.Core.Tests.VideoGrabber;

/// <summary>
/// Unit tests for <see cref="YtDlpFormatJsonParser"/>. The parser is the
/// pure-function piece of the format-list pipeline — runs against a
/// captured JSON fixture so the tests don't depend on yt-dlp itself.
/// </summary>
public class YtDlpFormatJsonParserTests
{
    /// <summary>
    /// Minimal but realistic yt-dlp -J dump: a couple of muxed video formats
    /// (with audio baked in), a couple of DASH video-only formats at
    /// different heights, and two audio-only formats. The parser should
    /// produce one row per distinct height plus the audio options.
    /// </summary>
    private const string Fixture = """
    {
      "id": "abc123",
      "title": "Sample video",
      "formats": [
        { "format_id": "18",  "ext": "mp4",  "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "height": 360,  "width": 640,  "fps": 30, "tbr": 600, "filesize": 22000000 },
        { "format_id": "22",  "ext": "mp4",  "vcodec": "avc1.64001F", "acodec": "mp4a.40.2", "height": 720,  "width": 1280, "fps": 30, "tbr": 1500, "filesize": 80000000 },
        { "format_id": "137", "ext": "mp4",  "vcodec": "avc1.640028", "acodec": "none",      "height": 1080, "width": 1920, "fps": 30, "tbr": 3500, "filesize": 145000000 },
        { "format_id": "315", "ext": "webm", "vcodec": "vp9",         "acodec": "none",      "height": 2160, "width": 3840, "fps": 60, "tbr": 16000, "filesize_approx": 800000000 },
        { "format_id": "140", "ext": "m4a",  "vcodec": "none",        "acodec": "mp4a.40.2", "abr": 128, "filesize": 4800000 },
        { "format_id": "251", "ext": "webm", "vcodec": "none",        "acodec": "opus",      "abr": 160, "filesize": 5500000 }
      ]
    }
    """;

    [Fact]
    public void Parses_title_and_groups_formats_by_kind()
    {
        var (title, formats) = YtDlpFormatJsonParser.Parse(Fixture);

        title.Should().Be("Sample video");
        formats.Should().NotBeEmpty();

        var videos = formats.Where(f => f.Kind == "video").ToList();
        var audios = formats.Where(f => f.Kind == "audio").ToList();

        // 4 distinct heights → 4 video rows.
        videos.Should().HaveCount(4);
        // 2 audio-only formats → 2 audio rows.
        audios.Should().HaveCount(2);
    }

    [Fact]
    public void Video_rows_ordered_highest_resolution_first()
    {
        var (_, formats) = YtDlpFormatJsonParser.Parse(Fixture);
        var heights = formats.Where(f => f.Kind == "video").Select(f => f.Height).ToArray();
        heights.Should().Equal(2160, 1080, 720, 360);
    }

    [Fact]
    public void Prefers_muxed_video_when_available_otherwise_pairs_with_bestaudio()
    {
        var (_, formats) = YtDlpFormatJsonParser.Parse(Fixture);

        // 720p has a muxed format (id 22, includes audio) → use that id directly.
        var p720 = formats.First(f => f.Height == 720);
        p720.FormatId.Should().Be("22");

        // 1080p has only a video-only format (id 137) → wrap with +bestaudio.
        var p1080 = formats.First(f => f.Height == 1080);
        p1080.FormatId.Should().Be("137+bestaudio/best");
    }

    [Fact]
    public void High_fps_video_label_includes_fps()
    {
        var (_, formats) = YtDlpFormatJsonParser.Parse(Fixture);
        var p2160 = formats.First(f => f.Height == 2160);
        // 60 fps on a 2160p source → "4K60".
        p2160.Label.Should().Be("4K60");
    }

    [Fact]
    public void Audio_rows_ordered_highest_bitrate_first()
    {
        var (_, formats) = YtDlpFormatJsonParser.Parse(Fixture);
        var audioBitrates = formats.Where(f => f.Kind == "audio").Select(f => f.AudioBitrateKbps).ToArray();
        audioBitrates.Should().Equal(160, 128);
    }

    [Fact]
    public void Audio_label_uses_friendly_codec_name()
    {
        var (_, formats) = YtDlpFormatJsonParser.Parse(Fixture);
        var audios = formats.Where(f => f.Kind == "audio").ToList();
        // Opus is the higher-bitrate one in the fixture, so it comes first.
        audios[0].Label.Should().Be("Opus · 160k");
        audios[1].Label.Should().Be("M4A (AAC) · 128k");
    }

    [Fact]
    public void Empty_input_returns_no_formats()
    {
        var (title, formats) = YtDlpFormatJsonParser.Parse("");
        title.Should().BeNull();
        formats.Should().BeEmpty();
    }

    [Fact]
    public void Missing_formats_array_returns_empty_list_but_keeps_title()
    {
        const string json = """{"title":"Nothing to see"}""";
        var (title, formats) = YtDlpFormatJsonParser.Parse(json);
        title.Should().Be("Nothing to see");
        formats.Should().BeEmpty();
    }

    [Fact]
    public void FileSize_falls_back_to_filesize_approx_when_filesize_missing()
    {
        var (_, formats) = YtDlpFormatJsonParser.Parse(Fixture);
        var p2160 = formats.First(f => f.Height == 2160);
        // Fixture has filesize_approx but no filesize for the 2160p track.
        p2160.FileSize.Should().Be(800_000_000L);
    }

    [Fact]
    public void Caps_audio_rows_at_4()
    {
        // Synthesize a payload with 6 audio-only formats; expect only 4.
        var json = """
        {
          "title": "Many audios",
          "formats": [
            { "format_id": "a1", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 320 },
            { "format_id": "a2", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 256 },
            { "format_id": "a3", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 192 },
            { "format_id": "a4", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 128 },
            { "format_id": "a5", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 96 },
            { "format_id": "a6", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 64 }
          ]
        }
        """;
        var (_, formats) = YtDlpFormatJsonParser.Parse(json);
        formats.Where(f => f.Kind == "audio").Should().HaveCount(4);
    }
}
