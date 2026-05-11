namespace SIDM.VideoGrabber.Dash;

/// <summary>
/// A parsed MPEG-DASH manifest reduced to what SIDM v1 actually consumes.
/// Multi-period manifests are flattened to the first period; the rest are
/// refused upstream.
/// </summary>
/// <param name="IsDynamic">True if MPD@type == "dynamic" — live stream, refused.</param>
/// <param name="HasDrm">True if any AdaptationSet/Representation has ContentProtection — refused.</param>
/// <param name="Representations">All representations in the (first) period, video + audio mixed.</param>
public sealed record DashManifest(bool IsDynamic, bool HasDrm, IReadOnlyList<DashRepresentation> Representations);

/// <summary>One quality stream — typically one video or one audio track.</summary>
/// <param name="Id">Representation id from the MPD.</param>
/// <param name="ContentKind">Video / Audio / Other (deduced from AdaptationSet @contentType or @mimeType).</param>
/// <param name="Bandwidth">Bits per second from @bandwidth.</param>
/// <param name="MimeType">Inherited from AdaptationSet/Representation; e.g. "video/mp4".</param>
/// <param name="Codecs">e.g. "avc1.4d401f"; null if not declared.</param>
/// <param name="Width">Pixels (video only); null otherwise.</param>
/// <param name="Height">Pixels (video only); null otherwise.</param>
/// <param name="InitSegmentUrl">Absolute URL of the init segment, or null if not declared.</param>
/// <param name="MediaSegmentUrls">Absolute URLs of media segments, in playback order.</param>
public sealed record DashRepresentation(
    string Id,
    DashContentKind ContentKind,
    long Bandwidth,
    string? MimeType,
    string? Codecs,
    int? Width,
    int? Height,
    Uri? InitSegmentUrl,
    IReadOnlyList<Uri> MediaSegmentUrls);

public enum DashContentKind
{
    Other = 0,
    Video = 1,
    Audio = 2,
}
