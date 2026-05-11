namespace SIDM.VideoGrabber.Hls;

/// <summary>
/// A parsed HLS master playlist (the one with multiple variants/qualities).
/// </summary>
/// <param name="Variants">Quality variants in declaration order.</param>
public sealed record HlsMasterPlaylist(IReadOnlyList<HlsVariant> Variants);

/// <summary>One quality variant inside a master playlist.</summary>
/// <param name="Url">Absolute URL of the variant's media playlist.</param>
/// <param name="Bandwidth">Peak bandwidth in bits per second (from EXT-X-STREAM-INF:BANDWIDTH).</param>
/// <param name="Resolution">e.g. "1920x1080" or null if not declared.</param>
/// <param name="Codecs">Codec string (e.g. "avc1.4d401f,mp4a.40.2") or null.</param>
public sealed record HlsVariant(Uri Url, long Bandwidth, string? Resolution, string? Codecs);

/// <summary>
/// A parsed HLS media playlist (the one with the actual segments). This is
/// the unit the downloader consumes — a master playlist is reduced to one
/// of these by variant selection.
/// </summary>
/// <param name="TargetDuration">EXT-X-TARGETDURATION in seconds.</param>
/// <param name="MediaSequence">EXT-X-MEDIA-SEQUENCE of the first segment (defaults to 0).</param>
/// <param name="IsLive">True if EXT-X-ENDLIST is absent — we refuse live streams in v1.</param>
/// <param name="IsFmp4">True if EXT-X-MAP is present (fragmented MP4 stream rather than MPEG-TS).</param>
/// <param name="Segments">Segments in playback order.</param>
public sealed record HlsMediaPlaylist(
    int TargetDuration,
    long MediaSequence,
    bool IsLive,
    bool IsFmp4,
    IReadOnlyList<HlsSegment> Segments);

/// <summary>One media segment.</summary>
/// <param name="Url">Absolute URL.</param>
/// <param name="DurationSeconds">From EXTINF.</param>
/// <param name="MediaSequenceNumber">Index used for IV derivation when EXT-X-KEY does not carry an explicit IV.</param>
/// <param name="Key">Encryption metadata applying to this segment, or null when in the clear.</param>
public sealed record HlsSegment(
    Uri Url,
    double DurationSeconds,
    long MediaSequenceNumber,
    HlsKey? Key);

/// <summary>
/// Decryption metadata for a stretch of segments. yt-dlp and Apple's spec
/// allow multiple EXT-X-KEY changes in the same playlist; we model that by
/// associating each segment with the key currently in effect.
/// </summary>
/// <param name="Method">e.g. "AES-128". v1 supports AES-128 only; "NONE" yields a null HlsKey.</param>
/// <param name="KeyUrl">URL the AES key is fetched from.</param>
/// <param name="ExplicitIv">Optional explicit IV (16 bytes). When null, callers derive the IV from the segment's media sequence number.</param>
public sealed record HlsKey(string Method, Uri KeyUrl, byte[]? ExplicitIv);
