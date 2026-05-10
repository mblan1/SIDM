using System.Text.Json;

namespace SIDM.Core.Ipc;

/// <summary>
/// Builds Native Messaging Host manifest JSON for Chromium-family browsers and
/// Firefox. The actual registry / filesystem writes live in
/// <c>SIDM.App.Services.NativeHostRegistration</c> — keeping the manifest shape
/// here so it can be unit-tested without touching the live system.
/// </summary>
public static class NativeHostManifest
{
    public const string HostId = "com.sidm.host";
    public const string DefaultDescription = "Snw Internet Download Manager Native Messaging Host";

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    public static byte[] BuildChromium(string browserHostPath, string allowedOrigin) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = HostId,
            description = DefaultDescription,
            path = browserHostPath,
            type = "stdio",
            allowed_origins = new[] { allowedOrigin },
        }, PrettyOptions);

    public static byte[] BuildFirefox(string browserHostPath, string allowedExtension) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = HostId,
            description = DefaultDescription,
            path = browserHostPath,
            type = "stdio",
            allowed_extensions = new[] { allowedExtension },
        }, PrettyOptions);
}
