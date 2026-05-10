using System.IO;
using Microsoft.Win32;
using SIDM.Core;
using SIDM.Core.Ipc;

namespace SIDM.App.Services;

/// <summary>
/// Per-user (HKCU) registration of the Native Messaging Host with Chromium-family
/// browsers and Firefox. Writes one manifest JSON file under
/// %LocalAppData%\SIDM\host\ and creates the registry keys each browser scans
/// to discover NMH executables.
///
/// Per-user means no admin/UAC: Velopack installs to %LocalAppData% anyway, and
/// HKCU\Software\&lt;Browser&gt;\NativeMessagingHosts\com.sidm.host is the
/// canonical location for that scope on Windows.
/// </summary>
public static class NativeHostRegistration
{
    public const string HostId = NativeHostManifest.HostId;

    private const string FallbackChromiumExtensionId = "paaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FirefoxExtensionId = "sidm@snw.dev";

    private static readonly (string Name, string KeyPath, BrowserKind Kind)[] Browsers =
    {
        ("Chrome",  @"Software\Google\Chrome\NativeMessagingHosts",            BrowserKind.Chromium),
        ("Edge",    @"Software\Microsoft\Edge\NativeMessagingHosts",           BrowserKind.Chromium),
        ("Brave",   @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts", BrowserKind.Chromium),
        ("Firefox", @"Software\Mozilla\NativeMessagingHosts",                  BrowserKind.Firefox),
    };

    private enum BrowserKind { Chromium, Firefox }

    public static string ManifestDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppInfo.LocalAppDataFolder,
        "host");

    public static string ChromiumManifestPath => Path.Combine(ManifestDirectory, "com.sidm.host.chromium.json");
    public static string FirefoxManifestPath => Path.Combine(ManifestDirectory, "com.sidm.host.firefox.json");

    /// <summary>
    /// Writes the manifests and registers them with every supported browser.
    /// <paramref name="chromiumExtensionId"/> may be null — a placeholder is used,
    /// which the caller is expected to update once the extension is published.
    /// </summary>
    public static RegistrationResult Register(string? chromiumExtensionId = null)
    {
        var browserHostPath = ResolveBrowserHostPath();
        if (browserHostPath is null)
        {
            return RegistrationResult.Failure(
                "Cannot find SIDM.BrowserHost.exe next to SIDM.App.exe or in dev paths. " +
                "Run `dotnet publish src/SIDM.BrowserHost -c Release -r win-x64` first.");
        }

        Directory.CreateDirectory(ManifestDirectory);

        var chromeOrigin = $"chrome-extension://{chromiumExtensionId ?? FallbackChromiumExtensionId}/";
        File.WriteAllBytes(ChromiumManifestPath, NativeHostManifest.BuildChromium(browserHostPath, chromeOrigin));
        File.WriteAllBytes(FirefoxManifestPath, NativeHostManifest.BuildFirefox(browserHostPath, FirefoxExtensionId));

        var registered = new List<string>();
        var skipped = new List<(string Browser, string Reason)>();

        foreach (var (name, keyPath, kind) in Browsers)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{keyPath}\{HostId}", writable: true);
                if (key is null) { skipped.Add((name, "could not create registry subkey")); continue; }
                var manifestPath = kind == BrowserKind.Chromium ? ChromiumManifestPath : FirefoxManifestPath;
                key.SetValue(string.Empty, manifestPath, RegistryValueKind.String);
                registered.Add(name);
            }
            catch (Exception ex)
            {
                skipped.Add((name, ex.Message));
            }
        }

        return new RegistrationResult(
            Success: registered.Count > 0,
            Message: registered.Count > 0
                ? $"Registered with: {string.Join(", ", registered)}"
                : "No browsers were registered.",
            ChromiumExtensionIdUsed: chromiumExtensionId ?? FallbackChromiumExtensionId,
            BrowserHostPath: browserHostPath,
            ManifestsWritten: new[] { ChromiumManifestPath, FirefoxManifestPath },
            SkippedBrowsers: skipped.ToArray());
    }

    public static RegistrationResult Unregister()
    {
        var removed = new List<string>();
        var skipped = new List<(string Browser, string Reason)>();

        foreach (var (name, keyPath, _) in Browsers)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"{keyPath}\{HostId}", throwOnMissingSubKey: false);
                removed.Add(name);
            }
            catch (Exception ex)
            {
                skipped.Add((name, ex.Message));
            }
        }

        try { if (File.Exists(ChromiumManifestPath)) File.Delete(ChromiumManifestPath); } catch { }
        try { if (File.Exists(FirefoxManifestPath)) File.Delete(FirefoxManifestPath); } catch { }

        return new RegistrationResult(
            Success: true,
            Message: removed.Count == 0 ? "Nothing to unregister." : $"Unregistered from: {string.Join(", ", removed)}",
            ChromiumExtensionIdUsed: null,
            BrowserHostPath: null,
            ManifestsWritten: Array.Empty<string>(),
            SkippedBrowsers: skipped.ToArray());
    }

    public static StatusReport GetStatus()
    {
        var browsers = new List<BrowserStatus>();
        foreach (var (name, keyPath, _) in Browsers)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{keyPath}\{HostId}");
                var path = key?.GetValue(string.Empty) as string;
                browsers.Add(new BrowserStatus(name, path is not null, path));
            }
            catch (Exception ex)
            {
                browsers.Add(new BrowserStatus(name, false, $"error: {ex.Message}"));
            }
        }

        return new StatusReport(
            ManifestsExist: File.Exists(ChromiumManifestPath) && File.Exists(FirefoxManifestPath),
            ChromiumManifestPath: ChromiumManifestPath,
            FirefoxManifestPath: FirefoxManifestPath,
            BrowserHostPath: ResolveBrowserHostPath(),
            Browsers: browsers.ToArray());
    }

    /// <summary>
    /// Searches for SIDM.BrowserHost.exe in: (1) the same folder as SIDM.App.exe
    /// (Velopack-installed layout), (2) the publish folder relative to dev sources,
    /// (3) the build output folder relative to dev sources.
    /// </summary>
    public static string? ResolveBrowserHostPath()
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";

        var candidates = new[]
        {
            Path.Combine(appDir, "SIDM.BrowserHost.exe"),
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "SIDM.BrowserHost", "bin", "Release", "net8.0-windows", "win-x64", "publish", "SIDM.BrowserHost.exe")),
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "SIDM.BrowserHost", "bin", "Release", "net8.0-windows", "win-x64", "SIDM.BrowserHost.exe")),
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "SIDM.BrowserHost", "bin", "Debug", "net8.0-windows", "win-x64", "SIDM.BrowserHost.exe")),
        };

        return Array.Find(candidates, File.Exists);
    }
}

public sealed record RegistrationResult(
    bool Success,
    string Message,
    string? ChromiumExtensionIdUsed,
    string? BrowserHostPath,
    string[] ManifestsWritten,
    (string Browser, string Reason)[] SkippedBrowsers)
{
    public static RegistrationResult Failure(string reason) =>
        new(false, reason, null, null, Array.Empty<string>(), Array.Empty<(string, string)>());
}

public sealed record BrowserStatus(string Browser, bool Registered, string? ManifestPath);

public sealed record StatusReport(
    bool ManifestsExist,
    string ChromiumManifestPath,
    string FirefoxManifestPath,
    string? BrowserHostPath,
    BrowserStatus[] Browsers);
