using SIDM.VideoGrabber.Hls;

namespace SIDM.Core.Tests.VideoGrabber.Hls;

public class M3U8ParserTests
{
    private static readonly Uri Base = new("https://cdn.example.test/video/master.m3u8");

    [Fact]
    public void IsMasterPlaylist_recognizes_stream_inf()
    {
        var text = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000\n720p.m3u8\n";
        M3U8Parser.IsMasterPlaylist(text).Should().BeTrue();
    }

    [Fact]
    public void IsMasterPlaylist_rejects_media_playlist()
    {
        var text = "#EXTM3U\n#EXTINF:6.0,\nseg0.ts\n#EXT-X-ENDLIST\n";
        M3U8Parser.IsMasterPlaylist(text).Should().BeFalse();
    }

    [Fact]
    public void Master_playlist_returns_variants_in_order()
    {
        var text = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=400000,RESOLUTION=480x270,CODECS="avc1.42c00d,mp4a.40.2"
        low/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720
        high/index.m3u8
        """;

        var master = M3U8Parser.ParseMaster(text, Base);

        master.Variants.Should().HaveCount(2);
        master.Variants[0].Bandwidth.Should().Be(400_000);
        master.Variants[0].Resolution.Should().Be("480x270");
        master.Variants[0].Codecs.Should().Be("avc1.42c00d,mp4a.40.2");
        master.Variants[0].Url.AbsoluteUri.Should().Be("https://cdn.example.test/video/low/index.m3u8");
        master.Variants[1].Bandwidth.Should().Be(2_500_000);
        master.Variants[1].Url.AbsoluteUri.Should().Be("https://cdn.example.test/video/high/index.m3u8");
    }

    [Fact]
    public void Media_playlist_parses_segments_with_sequence_numbering()
    {
        var text = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:100
        #EXTINF:6.0,
        seg100.ts
        #EXTINF:6.0,
        seg101.ts
        #EXTINF:4.5,
        seg102.ts
        #EXT-X-ENDLIST
        """;

        var media = M3U8Parser.ParseMedia(text, Base);

        media.TargetDuration.Should().Be(6);
        media.MediaSequence.Should().Be(100);
        media.IsLive.Should().BeFalse();
        media.IsFmp4.Should().BeFalse();
        media.Segments.Should().HaveCount(3);
        media.Segments[0].MediaSequenceNumber.Should().Be(100);
        media.Segments[1].MediaSequenceNumber.Should().Be(101);
        media.Segments[2].MediaSequenceNumber.Should().Be(102);
        media.Segments[2].DurationSeconds.Should().BeApproximately(4.5, 0.0001);
    }

    [Fact]
    public void Media_playlist_without_endlist_is_marked_live()
    {
        var text = "#EXTM3U\n#EXTINF:6,\nseg0.ts\n";

        M3U8Parser.ParseMedia(text, Base).IsLive.Should().BeTrue();
    }

    [Fact]
    public void Media_playlist_with_map_is_marked_fmp4()
    {
        var text = """
        #EXTM3U
        #EXT-X-MAP:URI="init.mp4"
        #EXTINF:6,
        seg0.m4s
        #EXT-X-ENDLIST
        """;
        M3U8Parser.ParseMedia(text, Base).IsFmp4.Should().BeTrue();
    }

    [Fact]
    public void Media_playlist_parses_aes128_key_with_explicit_iv()
    {
        var text = """
        #EXTM3U
        #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.test/k1",IV=0x00000000000000000000000000000064
        #EXTINF:6,
        seg0.ts
        #EXTINF:6,
        seg1.ts
        #EXT-X-ENDLIST
        """;

        var media = M3U8Parser.ParseMedia(text, Base);

        media.Segments.Should().HaveCount(2);
        var key = media.Segments[0].Key!;
        key.Method.Should().Be("AES-128");
        key.KeyUrl.AbsoluteUri.Should().Be("https://keys.example.test/k1");
        key.ExplicitIv.Should().NotBeNull();
        key.ExplicitIv![15].Should().Be(0x64); // 100 decimal at the LSB.
    }

    [Fact]
    public void Method_none_yields_null_key()
    {
        var text = """
        #EXTM3U
        #EXT-X-KEY:METHOD=NONE
        #EXTINF:6,
        seg0.ts
        #EXT-X-ENDLIST
        """;

        M3U8Parser.ParseMedia(text, Base).Segments[0].Key.Should().BeNull();
    }

    [Fact]
    public void Key_change_mid_playlist_applies_to_subsequent_segments_only()
    {
        var text = """
        #EXTM3U
        #EXT-X-KEY:METHOD=AES-128,URI="k1"
        #EXTINF:6,
        a.ts
        #EXT-X-KEY:METHOD=AES-128,URI="k2"
        #EXTINF:6,
        b.ts
        #EXT-X-ENDLIST
        """;

        var media = M3U8Parser.ParseMedia(text, Base);

        media.Segments[0].Key!.KeyUrl.AbsoluteUri.Should().EndWith("/k1");
        media.Segments[1].Key!.KeyUrl.AbsoluteUri.Should().EndWith("/k2");
    }

    [Fact]
    public void Tolerates_CRLF_line_endings()
    {
        var text = "#EXTM3U\r\n#EXTINF:6.0,\r\nseg0.ts\r\n#EXT-X-ENDLIST\r\n";

        var media = M3U8Parser.ParseMedia(text, Base);

        media.Segments.Should().HaveCount(1);
    }

    [Fact]
    public void ParseAttributes_handles_quoted_commas()
    {
        var attrs = M3U8Parser.ParseAttributes("BANDWIDTH=400000,RESOLUTION=480x270,CODECS=\"avc1.42c00d,mp4a.40.2\"");

        attrs["BANDWIDTH"].Should().Be("400000");
        attrs["RESOLUTION"].Should().Be("480x270");
        attrs["CODECS"].Should().Be("avc1.42c00d,mp4a.40.2");
    }

    [Theory]
    [InlineData("0x000102030405060708090A0B0C0D0E0F", new byte[] { 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15 })]
    [InlineData("000102030405060708090a0b0c0d0e0f", new byte[] { 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15 })]
    public void ParseHexIv_decodes_valid_input(string input, byte[] expected)
    {
        M3U8Parser.ParseHexIv(input).Should().Equal(expected);
    }

    [Theory]
    [InlineData("0x")]
    [InlineData("0xZZ")]
    [InlineData("not-hex")]
    public void ParseHexIv_returns_null_for_malformed(string input)
    {
        M3U8Parser.ParseHexIv(input).Should().BeNull();
    }

    [Fact]
    public void ResolveUri_keeps_absolute_unchanged_and_resolves_relative()
    {
        M3U8Parser.ResolveUri(Base, "https://other.test/x.ts").AbsoluteUri.Should().Be("https://other.test/x.ts");
        M3U8Parser.ResolveUri(Base, "../audio/eng.m3u8").AbsoluteUri.Should().Be("https://cdn.example.test/audio/eng.m3u8");
    }
}
