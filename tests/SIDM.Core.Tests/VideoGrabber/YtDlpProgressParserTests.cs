using SIDM.VideoGrabber;

namespace SIDM.Core.Tests.VideoGrabber;

public class YtDlpProgressParserTests
{
    [Fact]
    public void Parses_a_fully_populated_line()
    {
        var line = "SIDM:12345|98765|3|10|45|1048576";

        YtDlpProgressParser.TryParse(line, out var p).Should().BeTrue();
        p.DownloadedBytes.Should().Be(12345);
        p.TotalBytes.Should().Be(98765);
        p.FragmentIndex.Should().Be(3);
        p.FragmentCount.Should().Be(10);
        p.EtaSeconds.Should().Be(45);
        p.SpeedBytesPerSecond.Should().Be(1048576);
    }

    [Fact]
    public void Treats_NA_as_null()
    {
        var line = "SIDM:1000|NA|NA|NA|NA|NA";

        YtDlpProgressParser.TryParse(line, out var p).Should().BeTrue();
        p.DownloadedBytes.Should().Be(1000);
        p.TotalBytes.Should().BeNull();
        p.FragmentIndex.Should().BeNull();
        p.FragmentCount.Should().BeNull();
        p.EtaSeconds.Should().BeNull();
        p.SpeedBytesPerSecond.Should().BeNull();
    }

    [Fact]
    public void Accepts_decimal_byte_counts()
    {
        // Some yt-dlp builds emit floats for downloaded_bytes / total_bytes.
        var line = "SIDM:12345.0|98765.5|NA|NA|NA|1048576.0";

        YtDlpProgressParser.TryParse(line, out var p).Should().BeTrue();
        p.DownloadedBytes.Should().Be(12345);
        p.TotalBytes.Should().Be(98765);
        p.SpeedBytesPerSecond.Should().Be(1048576);
    }

    [Fact]
    public void Rejects_lines_without_the_prefix()
    {
        YtDlpProgressParser.TryParse("[download] 1.4MiB / 12.3MiB at 2.5MiB/s", out _).Should().BeFalse();
        YtDlpProgressParser.TryParse("", out _).Should().BeFalse();
        YtDlpProgressParser.TryParse("Other text", out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_lines_with_too_few_fields()
    {
        YtDlpProgressParser.TryParse("SIDM:1|2|3", out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_lines_with_unparseable_downloaded_bytes()
    {
        // downloaded_bytes is required; "NA" here is treated as unparseable
        // (we don't know how many bytes happened), so the line is dropped.
        YtDlpProgressParser.TryParse("SIDM:NA|100|NA|NA|NA|NA", out _).Should().BeFalse();
    }
}
