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
[JsonDerivedType(typeof(ErrorMessage), typeDiscriminator: "error")]
public abstract record IpcMessage;

public sealed record HelloRequest(string ClientName, string ClientVersion) : IpcMessage;

public sealed record HelloResponse(string AppName, string AppVersion, string[] Capabilities) : IpcMessage;

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
    string? Mime = null) : IpcMessage;

public sealed record DownloadResponseMessage(long DownloadId, string Status) : IpcMessage;

public sealed record ErrorMessage(string Reason, string? Detail = null) : IpcMessage;
