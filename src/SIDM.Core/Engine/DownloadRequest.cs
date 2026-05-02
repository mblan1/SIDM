namespace SIDM.Core.Engine;

/// <summary>
/// Inputs to <see cref="DownloadOrchestrator.ExecuteAsync"/>.
/// </summary>
public sealed record DownloadRequest
{
    public required Uri Url { get; init; }

    /// <summary>Directory + filename where the final file should land.</summary>
    public required string TargetPath { get; init; }

    /// <summary>Desired number of parallel segments. The actual count is clamped by min segment size.</summary>
    public int Segments { get; init; } = SegmentSplitter.DefaultRequestedSegments;

    /// <summary>Minimum bytes per segment. Falls back to fewer segments rather than over-splitting.</summary>
    public long MinSegmentBytes { get; init; } = SegmentSplitter.DefaultMinSegmentBytes;

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public IReadOnlyDictionary<string, string>? Cookies { get; init; }

    /// <summary>Hex-encoded expected hash; if set together with <see cref="HashAlgo"/>, the download is verified after finalize.</summary>
    public string? ExpectedHash { get; init; }
    public string? HashAlgo { get; init; }

    /// <summary>If set, resume from these per-segment offsets instead of a fresh split.</summary>
    public IReadOnlyList<ResumeSegment>? Resume { get; init; }
}

public sealed record ResumeSegment(int Index, long StartByte, long EndByte, long BytesAlreadyDownloaded);
