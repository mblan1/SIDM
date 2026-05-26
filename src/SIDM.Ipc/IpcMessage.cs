using System.Text.Json.Serialization;

namespace SIDM.Ipc;

/// <summary>
/// Base type for every message exchanged between SIDM.BrowserHost and SIDM.App.
/// The "type" property is the discriminator and is added automatically by
/// <see cref="JsonSerializer"/> when configured with <see cref="IpcSerializer"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloRequest), typeDiscriminator: "hello")]
[JsonDerivedType(typeof(HelloResponse), typeDiscriminator: "hello-response")]
[JsonDerivedType(typeof(DownloadRequestMessage), typeDiscriminator: "download")]
[JsonDerivedType(typeof(DownloadResponseMessage), typeDiscriminator: "download-response")]
[JsonDerivedType(typeof(ListFormatsRequestMessage), typeDiscriminator: "list-formats")]
[JsonDerivedType(typeof(ListFormatsResponseMessage), typeDiscriminator: "list-formats-response")]
[JsonDerivedType(typeof(ErrorMessage), typeDiscriminator: "error")]
public abstract record IpcMessage;

public sealed record HelloRequest(string ClientName, string ClientVersion) : IpcMessage;

public sealed record HelloResponse(
    string AppName,
    string AppVersion,
    string[] Capabilities,
    /// <summary>
    /// Version of the browser extension that ships with this SIDM build.
    /// The extension compares this to its own manifest version on every
    /// hello handshake and calls <c>chrome.runtime.reload()</c> if SIDM
    /// is newer — so an upgrade flow that auto-extracts new files into
    /// %LocalAppData%\SIDM\extensions\&lt;kind&gt; takes effect without
    /// the user having to visit chrome://extensions/ and click reload.
    /// Null in older builds; new extensions tolerate that as "no signal".
    /// </summary>
    string? BundledExtensionVersion = null) : IpcMessage;

/// <summary>
/// "Capture this download". The browser extension forwards everything it knows
/// about the resource so SIDM can replay the request authentically.
/// </summary>
public sealed record DownloadRequestMessage(
    string Url,
    string? FileName = null,
    string? SuggestedFolder = null,
    Dictionary<string, string>? Headers = null,
    Dictionary<string, string>? Cookies = null,
    string? Referer = null,
    string? UserAgent = null,
    long? ExpectedLength = null,
    string? Mime = null,
    /// <summary>
    /// Optional yt-dlp format selector (e.g. <c>"137+140"</c> for 1080p
    /// video with best audio, or <c>"bestaudio[ext=m4a]"</c>). Set by the
    /// in-page format picker — null for plain downloads where SIDM uses
    /// its default. The engine wires this into <c>YtDlpRunRequest.FormatSelector</c>.
    /// </summary>
    string? YtDlpFormat = null,
    /// <summary>
    /// Human-readable label for the chosen format (e.g. "1080p MP4 · ~145 MB").
    /// Shown read-only in the AddDownloadDialog so the user sees exactly
    /// what they're about to download. Null when <see cref="YtDlpFormat"/>
    /// is null.
    /// </summary>
    string? YtDlpFormatLabel = null) : IpcMessage;

public sealed record DownloadResponseMessage(long DownloadId, string Status) : IpcMessage;

/// <summary>
/// "Probe this URL with yt-dlp and tell me what video resolutions and audio
/// bitrates are available." Used by the browser-overlay picker before the
/// user commits to downloading anything.
/// </summary>
public sealed record ListFormatsRequestMessage(string Url) : IpcMessage;

/// <summary>
/// Reply to <see cref="ListFormatsRequestMessage"/>. <see cref="Title"/> is
/// the video's title from yt-dlp (used by the picker as a header). Each
/// <see cref="FormatOption"/> has enough info for the picker to render a
/// row and to round-trip a chosen <c>FormatId</c> back as
/// <see cref="DownloadRequestMessage.YtDlpFormat"/>.
/// </summary>
public sealed record ListFormatsResponseMessage(
    string Url,
    string? Title,
    FormatOption[] Formats) : IpcMessage;

/// <summary>
/// One row in the format picker. <see cref="FormatId"/> is what gets sent
/// back as <c>YtDlpFormat</c>; the rest is display metadata.
/// </summary>
public sealed record FormatOption(
    string FormatId,
    /// <summary>"video" or "audio".</summary>
    string Kind,
    /// <summary>e.g. "1080p", "Opus 256k", "4K (2160p)" — ready to render.</summary>
    string Label,
    /// <summary>e.g. "mp4", "m4a", "webm".</summary>
    string Ext,
    /// <summary>e.g. "h264", "vp9", "av1", or null for audio-only.</summary>
    string? Vcodec = null,
    /// <summary>e.g. "aac", "opus", or null for video-only.</summary>
    string? Acodec = null,
    /// <summary>Pixel height — for video formats only.</summary>
    int? Height = null,
    /// <summary>Frames per second — for video formats only.</summary>
    int? Fps = null,
    /// <summary>Audio bitrate in kbps — for audio formats only.</summary>
    int? AudioBitrateKbps = null,
    /// <summary>Bytes, when known. May be null for live/manifest streams.</summary>
    long? FileSize = null);

public sealed record ErrorMessage(string Reason, string? Detail = null) : IpcMessage;
