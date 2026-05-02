namespace SIDM.Core.Models;

public class Segment
{
    public long Id { get; set; }
    public long DownloadId { get; set; }
    public int Idx { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public long BytesDownloaded { get; set; }
    public SegmentStatus Status { get; set; }
    public DateTimeOffset? LastErrorUtc { get; set; }

    public ByteRange Range => new(StartByte, EndByte);
    public ByteRange RemainingRange =>
        BytesDownloaded >= Range.Length
            ? new ByteRange(EndByte + 1, EndByte + 1)
            : new ByteRange(StartByte + BytesDownloaded, EndByte);
    public bool IsComplete => BytesDownloaded >= Range.Length;
}
