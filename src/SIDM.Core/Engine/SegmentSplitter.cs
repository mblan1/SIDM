using SIDM.Core.Models;

namespace SIDM.Core.Engine;

public static class SegmentSplitter
{
    public const long DefaultMinSegmentBytes = 1L << 20; // 1 MiB
    public const int DefaultRequestedSegments = 8;

    /// <summary>
    /// Splits <paramref name="totalBytes"/> into contiguous, non-overlapping byte ranges.
    /// The actual segment count is clamped so each segment is at least
    /// <paramref name="minSegmentBytes"/>.
    /// Ranges are inclusive on both ends (HTTP Range semantics).
    /// </summary>
    public static IReadOnlyList<ByteRange> Split(
        long totalBytes,
        int requestedSegments = DefaultRequestedSegments,
        long minSegmentBytes = DefaultMinSegmentBytes)
    {
        if (totalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes), "totalBytes must be positive");
        if (requestedSegments <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedSegments), "requestedSegments must be positive");
        if (minSegmentBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(minSegmentBytes), "minSegmentBytes must be positive");

        var maxBySize = Math.Max(1L, totalBytes / minSegmentBytes);
        var n = (int)Math.Min(requestedSegments, maxBySize);

        var baseSize = totalBytes / n;
        var remainder = (int)(totalBytes % n);

        var ranges = new ByteRange[n];
        var cursor = 0L;
        for (var i = 0; i < n; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            ranges[i] = new ByteRange(cursor, cursor + size - 1);
            cursor += size;
        }

        return ranges;
    }
}
