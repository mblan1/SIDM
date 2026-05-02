using SIDM.Core.Models;

namespace SIDM.Core.Engine;

/// <summary>
/// One unit of work for a <see cref="SegmentWorker"/>. Captures everything the worker
/// needs to fetch a byte range and stream it to the file writer.
/// </summary>
public sealed record SegmentTask(
    Uri Url,
    int Index,
    long StartByte,
    long EndByte,
    long BytesAlreadyDownloaded,
    IReadOnlyDictionary<string, string>? Headers,
    IReadOnlyDictionary<string, string>? Cookies)
{
    public ByteRange FullRange => new(StartByte, EndByte);
    public ByteRange RemainingRange => new(StartByte + BytesAlreadyDownloaded, EndByte);
    public bool IsAlreadyComplete => BytesAlreadyDownloaded >= FullRange.Length;
}

public enum SegmentOutcome
{
    Completed = 0,
    Canceled = 1,
    RangeNotHonored = 2,
    HttpError = 3,
    NetworkError = 4,
}

public sealed record SegmentResult(
    SegmentOutcome Outcome,
    long BytesDownloadedThisRun,
    Exception? Exception = null)
{
    public bool IsSuccess => Outcome == SegmentOutcome.Completed;
}

public interface ISegmentProgressSink
{
    /// <summary>
    /// Called periodically by the worker with the current cumulative byte count.
    /// Implementations should be fast (channel write) and non-blocking.
    /// </summary>
    void Report(int segmentIndex, long bytesDownloaded);
}

public sealed class NullProgressSink : ISegmentProgressSink
{
    public static readonly NullProgressSink Instance = new();
    public void Report(int segmentIndex, long bytesDownloaded) { }
}
