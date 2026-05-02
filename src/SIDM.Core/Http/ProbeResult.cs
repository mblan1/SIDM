namespace SIDM.Core.Http;

/// <summary>
/// Result of probing a download URL: tells us whether we can use multi-segment
/// (ranges) and gives us the metadata needed to allocate / verify the file.
/// </summary>
public sealed record ProbeResult(
    Uri EffectiveUrl,
    long? ContentLength,
    bool AcceptsRanges,
    string? ETag,
    string? LastModified,
    string? Mime,
    string? SuggestedFileName,
    string? ContentHash,
    string? HashAlgo);
