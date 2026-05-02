namespace SIDM.Core.Engine;

public enum DownloadFailureKind
{
    None = 0,
    ProbeFailed = 1,
    /// <summary>Server didn't honor ranges; orchestrator gave up rather than fall back to single-stream.</summary>
    RangeNotHonored = 2,
    Canceled = 3,
    SegmentFailed = 4,
    HashMismatch = 5,
    IoError = 6,
    UnknownContentLength = 7,
}

public sealed record DownloadResult(
    bool Success,
    string? FinalPath,
    long TotalBytes,
    DownloadFailureKind FailureKind,
    string? FailureMessage,
    IReadOnlyList<SegmentSnapshot> Segments,
    Exception? Exception = null);

public sealed record SegmentSnapshot(
    int Index,
    long StartByte,
    long EndByte,
    long BytesDownloaded,
    SegmentOutcome LastOutcome);
