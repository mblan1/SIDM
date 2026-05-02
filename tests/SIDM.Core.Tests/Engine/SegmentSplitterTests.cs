using SIDM.Core.Engine;
using SIDM.Core.Models;

namespace SIDM.Core.Tests.Engine;

public class SegmentSplitterTests
{
    [Fact]
    public void Split_with_one_segment_returns_full_range()
    {
        var ranges = SegmentSplitter.Split(100, requestedSegments: 1, minSegmentBytes: 1);

        ranges.Should().ContainSingle();
        ranges[0].Should().Be(new ByteRange(0, 99));
        ranges[0].Length.Should().Be(100);
    }

    [Fact]
    public void Split_evenly_divisible_distributes_equal_sizes()
    {
        var ranges = SegmentSplitter.Split(100, requestedSegments: 4, minSegmentBytes: 1);

        ranges.Should().HaveCount(4);
        ranges.Should().AllSatisfy(r => r.Length.Should().Be(25));
        ranges[0].Should().Be(new ByteRange(0, 24));
        ranges[3].Should().Be(new ByteRange(75, 99));
    }

    [Fact]
    public void Split_with_remainder_distributes_extra_bytes_to_first_segments()
    {
        // 101 / 4 = 25 r 1 → first segment gets 26, rest get 25
        var ranges = SegmentSplitter.Split(101, requestedSegments: 4, minSegmentBytes: 1);

        ranges.Should().HaveCount(4);
        ranges[0].Length.Should().Be(26);
        ranges[1].Length.Should().Be(25);
        ranges[2].Length.Should().Be(25);
        ranges[3].Length.Should().Be(25);
        ranges.Sum(r => r.Length).Should().Be(101);
    }

    [Fact]
    public void Split_clamps_segment_count_when_min_size_would_be_violated()
    {
        // 8 bytes, 4 segments requested, but min size 4 → only 2 segments fit
        var ranges = SegmentSplitter.Split(8, requestedSegments: 4, minSegmentBytes: 4);

        ranges.Should().HaveCount(2);
        ranges.Sum(r => r.Length).Should().Be(8);
    }

    [Fact]
    public void Split_falls_back_to_one_segment_when_total_is_smaller_than_min_size()
    {
        var ranges = SegmentSplitter.Split(100, requestedSegments: 8, minSegmentBytes: 1024);

        ranges.Should().ContainSingle();
        ranges[0].Should().Be(new ByteRange(0, 99));
    }

    [Theory]
    [InlineData(1L, 1, 1L)]
    [InlineData(1L, 8, 1L)]
    [InlineData(7L, 8, 1L)]
    [InlineData(1024L * 1024 * 1024, 8, 1L << 20)] // 1 GiB / 8
    [InlineData(1024L * 1024 * 1024 + 7, 8, 1L << 20)] // 1 GiB + 7 bytes (awkward remainder)
    [InlineData(long.MaxValue / 2, 16, 1L << 20)] // huge file
    public void Split_invariants_hold_for_arbitrary_inputs(long total, int segments, long minSize)
    {
        var ranges = SegmentSplitter.Split(total, segments, minSize);

        ranges.Should().NotBeEmpty();
        ranges[0].Start.Should().Be(0, "first range must start at byte 0");
        ranges[^1].End.Should().Be(total - 1, "last range must end at the final byte");
        ranges.Sum(r => r.Length).Should().Be(total, "ranges must cover every byte exactly once");

        for (var i = 1; i < ranges.Count; i++)
        {
            ranges[i].Start.Should().Be(ranges[i - 1].End + 1,
                $"range {i} must start immediately after range {i - 1} ends");
        }

        ranges.Should().AllSatisfy(r => r.Length.Should().BeGreaterThan(0));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Split_throws_for_non_positive_total(long total)
    {
        var act = () => SegmentSplitter.Split(total, 4, 1);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("totalBytes");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Split_throws_for_non_positive_segment_count(int segments)
    {
        var act = () => SegmentSplitter.Split(100, segments, 1);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("requestedSegments");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Split_throws_for_non_positive_min_segment_bytes(long minBytes)
    {
        var act = () => SegmentSplitter.Split(100, 4, minBytes);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("minSegmentBytes");
    }

    [Fact]
    public void ByteRange_FromLength_constructs_correct_inclusive_range()
    {
        var range = ByteRange.FromLength(start: 100, length: 50);
        range.Start.Should().Be(100);
        range.End.Should().Be(149);
        range.Length.Should().Be(50);
    }

    [Fact]
    public void ByteRange_ToHttpRangeValue_formats_correctly()
    {
        new ByteRange(0, 1023).ToHttpRangeValue().Should().Be("bytes=0-1023");
        new ByteRange(2048, 4095).ToHttpRangeValue().Should().Be("bytes=2048-4095");
    }
}
