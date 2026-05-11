using SIDM.VideoGrabber.Dash;

namespace SIDM.Core.Tests.VideoGrabber.Dash;

public class MpdParserTests
{
    private static readonly Uri Base = new("https://cdn.example.test/v/manifest.mpd");

    [Fact]
    public void Static_manifest_with_constant_duration_template_expands_segments()
    {
        // 30 s of video at 6 s per segment → 5 segments numbered 1..5.
        var mpd = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT30S">
          <Period>
            <AdaptationSet mimeType="video/mp4" contentType="video">
              <Representation id="v720" bandwidth="2500000" codecs="avc1.4d401f" width="1280" height="720">
                <SegmentTemplate timescale="30000" duration="180000" startNumber="1"
                                 media="$RepresentationID$/seg-$Number$.m4s"
                                 initialization="$RepresentationID$/init.mp4" />
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

        var manifest = MpdParser.Parse(mpd, Base);

        manifest.IsDynamic.Should().BeFalse();
        manifest.HasDrm.Should().BeFalse();
        manifest.Representations.Should().HaveCount(1);

        var rep = manifest.Representations[0];
        rep.Id.Should().Be("v720");
        rep.ContentKind.Should().Be(DashContentKind.Video);
        rep.Bandwidth.Should().Be(2_500_000);
        rep.Width.Should().Be(1280);
        rep.Height.Should().Be(720);
        rep.InitSegmentUrl!.AbsoluteUri.Should().Be("https://cdn.example.test/v/v720/init.mp4");
        rep.MediaSegmentUrls.Should().HaveCount(5);
        rep.MediaSegmentUrls[0].AbsoluteUri.Should().Be("https://cdn.example.test/v/v720/seg-1.m4s");
        rep.MediaSegmentUrls[4].AbsoluteUri.Should().Be("https://cdn.example.test/v/v720/seg-5.m4s");
    }

    [Fact]
    public void Dynamic_manifest_is_flagged()
    {
        var mpd = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="dynamic">
          <Period />
        </MPD>
        """;

        MpdParser.Parse(mpd, Base).IsDynamic.Should().BeTrue();
    }

    [Fact]
    public void ContentProtection_is_flagged_as_drm()
    {
        var mpd = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S">
          <Period>
            <AdaptationSet mimeType="video/mp4">
              <ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011" value="cenc" />
              <Representation id="v" bandwidth="1000000" />
            </AdaptationSet>
          </Period>
        </MPD>
        """;

        MpdParser.Parse(mpd, Base).HasDrm.Should().BeTrue();
    }

    [Fact]
    public void SegmentTimeline_expands_each_S_entry_and_honors_the_repeat_count()
    {
        // 3 distinct entries: t=0 d=180000, then d=120000 r=2 (3 occurrences),
        // then d=60000 (1 occurrence). Total = 1 + 3 + 1 = 5 segments.
        var mpd = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT30S">
          <Period>
            <AdaptationSet mimeType="video/mp4" contentType="video">
              <Representation id="vt" bandwidth="2000000">
                <SegmentTemplate timescale="30000" startNumber="1"
                                 media="$RepresentationID$/seg-$Number$.m4s"
                                 initialization="$RepresentationID$/init.mp4">
                  <SegmentTimeline>
                    <S t="0" d="180000" />
                    <S d="120000" r="2" />
                    <S d="60000" />
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

        var rep = MpdParser.Parse(mpd, Base).Representations[0];
        rep.MediaSegmentUrls.Should().HaveCount(5);
        rep.MediaSegmentUrls.Select(u => u.AbsoluteUri).Should().Equal(
            "https://cdn.example.test/v/vt/seg-1.m4s",
            "https://cdn.example.test/v/vt/seg-2.m4s",
            "https://cdn.example.test/v/vt/seg-3.m4s",
            "https://cdn.example.test/v/vt/seg-4.m4s",
            "https://cdn.example.test/v/vt/seg-5.m4s");
    }

    [Fact]
    public void SegmentTemplate_inherited_from_AdaptationSet_applies_to_all_representations()
    {
        var mpd = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT12S">
          <Period>
            <AdaptationSet mimeType="video/mp4" contentType="video">
              <SegmentTemplate timescale="30000" duration="180000" startNumber="1"
                               media="$RepresentationID$/$Number$.m4s"
                               initialization="$RepresentationID$/init.mp4" />
              <Representation id="low" bandwidth="500000" />
              <Representation id="high" bandwidth="3000000" />
            </AdaptationSet>
          </Period>
        </MPD>
        """;

        var manifest = MpdParser.Parse(mpd, Base);
        manifest.Representations.Should().HaveCount(2);
        manifest.Representations[0].MediaSegmentUrls.Should().HaveCount(2);
        manifest.Representations[0].MediaSegmentUrls[0].AbsoluteUri.Should().EndWith("/low/1.m4s");
        manifest.Representations[1].MediaSegmentUrls[0].AbsoluteUri.Should().EndWith("/high/1.m4s");
    }

    [Fact]
    public void ApplyTemplate_handles_all_four_variables()
    {
        var result = MpdParser.ApplyTemplate(
            "$RepresentationID$/$Bandwidth$/seg-$Number$-at-$Time$.m4s",
            representationId: "v720", bandwidth: 2_500_000, number: 5, time: 720_000);

        result.Should().Be("v720/2500000/seg-5-at-720000.m4s");
    }

    [Fact]
    public void ApplyTemplate_handles_zero_padded_Number()
    {
        var result = MpdParser.ApplyTemplate("seg-$Number%05d$.m4s",
            representationId: "x", bandwidth: 0, number: 42, time: 0);

        result.Should().Be("seg-00042.m4s");
    }

    [Fact]
    public void Audio_AdaptationSet_is_classified_as_audio()
    {
        var mpd = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT6S">
          <Period>
            <AdaptationSet mimeType="audio/mp4" contentType="audio">
              <Representation id="a" bandwidth="128000">
                <SegmentTemplate timescale="48000" duration="288000" startNumber="1"
                                 media="a/$Number$.m4s" initialization="a/init.mp4" />
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

        var rep = MpdParser.Parse(mpd, Base).Representations[0];
        rep.ContentKind.Should().Be(DashContentKind.Audio);
    }

    [Theory]
    [InlineData("PT30S", 30)]
    [InlineData("PT1M30S", 90)]
    [InlineData("PT1H", 3600)]
    [InlineData("PT0.5S", 0.5)]
    public void ParseIso8601Duration_handles_typical_values(string raw, double expectedSeconds)
    {
        var ts = MpdParser.ParseIso8601Duration(raw)!.Value;
        ts.TotalSeconds.Should().BeApproximately(expectedSeconds, 0.001);
    }

    [Fact]
    public void ParseIso8601Duration_returns_null_for_malformed_or_missing()
    {
        MpdParser.ParseIso8601Duration(null).Should().BeNull();
        MpdParser.ParseIso8601Duration("").Should().BeNull();
        MpdParser.ParseIso8601Duration("nope").Should().BeNull();
    }
}
