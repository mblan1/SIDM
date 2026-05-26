using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using SIDM.Core;

namespace SIDM.App.Services;

public enum InstallStep
{
    Downloading,
    Extracting,
    OpeningBrowser,
    Complete,
    Failed,
}

public sealed record InstallProgress(InstallStep Step, string Message, double Percent = 0);

/// <summary>
/// Downloads the SIDM browser extension pack from GitHub Releases, extracts
/// it to a stable folder under LocalAppData, then opens the right browser
/// page so the user can finish the install (Load unpacked in Chrome, Load
/// Temporary Add-on in Firefox).
///
/// Pre-store-approval flow — once Chrome Web Store and AMO accept the
/// listing, swap <see cref="ChromiumStoreUrl"/> / <see cref="FirefoxStoreUrl"/>
/// to point at the listing and the dialog becomes a real one-click install.
/// </summary>
public sealed class BrowserExtensionInstaller
{
    /// <summary>
    /// Subfolder, relative to the installed-app directory, where
    /// scripts/publish.ps1 drops the bundled extension zips so the first-run
    /// install works offline.
    /// </summary>
    private const string LocalPackSubfolder = "extensions";

    /// <summary>
    /// GitHub Releases URL pattern for the per-browser zip. Used as a fallback
    /// when no bundled zip is found next to the installed app — e.g. after an
    /// older build is updated without a corresponding extension refresh.
    /// </summary>
    private const string GitHubReleaseUrl =
        "https://github.com/mblan1/SIDM/releases/download/v{0}/SIDM-Extension-{1}-{0}.zip";

    /// <summary>Chrome Web Store listing — fill in once approved.</summary>
    public const string ChromiumStoreUrl = ""; // e.g. https://chromewebstore.google.com/detail/sidm/<id>

    /// <summary>Firefox Add-ons listing — fill in once approved.</summary>
    public const string FirefoxStoreUrl = ""; // e.g. https://addons.mozilla.org/firefox/addon/sidm/

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BrowserExtensionInstaller> _logger;

    public BrowserExtensionInstaller(IHttpClientFactory httpClientFactory, ILogger<BrowserExtensionInstaller> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns the folder the extension was extracted to (or about to be).
    /// Stable per-browser so re-runs overwrite cleanly. The Native Messaging
    /// Host manifest is registered globally on first run, so dropping new
    /// extension bits here is the only filesystem step needed.
    /// </summary>
    public static string ExtensionFolder(BrowserKind kind) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SIDM", "extensions", kind.ToString().ToLowerInvariant());

    /// <summary>
    /// Downloads + extracts the extension pack for the given browser, then
    /// opens the appropriate install page. Progress events fire on the
    /// supplied <paramref name="progress"/> handler so the dialog can update
    /// its status line.
    /// </summary>
    public async Task<InstallProgress> InstallAsync(
        DetectedBrowser browser,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Filename convention from publish.ps1: Chromium pack serves Chrome /
        // Edge / Brave; Firefox has its own gecko-targeted build.
        var packLabel = browser.Kind.IsChromium() ? "Chromium" : "Firefox";
        var version = SIDM.Core.AppInfo.Version;
        var folder = ExtensionFolder(browser.Kind);

        try
        {
            string zipPath;
            var localPack = TryResolveLocalPack(packLabel, version);
            if (localPack is not null)
            {
                _logger.LogInformation("Using bundled extension pack {Pack}", localPack);
                progress?.Report(new InstallProgress(InstallStep.Extracting,
                    $"Preparing {browser.Kind.DisplayName()} extension…", 0.8));
                zipPath = localPack;
            }
            else
            {
                progress?.Report(new InstallProgress(InstallStep.Downloading,
                    $"Downloading {browser.Kind.DisplayName()} extension…"));
                var url = string.Format(GitHubReleaseUrl, version, packLabel);
                zipPath = Path.Combine(Path.GetTempPath(), $"SIDM-Extension-{packLabel}-{version}.zip");
                _logger.LogInformation("No bundled pack at {Pack}; downloading from {Url}",
                    Path.Combine(AppContext.BaseDirectory, LocalPackSubfolder,
                        $"SIDM-Extension-{packLabel}-{version}.zip"), url);
                await DownloadAsync(url, zipPath, progress, cancellationToken);
            }

            // -- Extract -----------------------------------------------------
            progress?.Report(new InstallProgress(InstallStep.Extracting, "Extracting…", 0.85));
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            ZipFile.ExtractToDirectory(zipPath, folder, overwriteFiles: true);

            // -- Open browser ------------------------------------------------
            progress?.Report(new InstallProgress(InstallStep.OpeningBrowser, "Opening browser…", 0.95));
            OpenExtensionPage(browser);

            // (Explorer is intentionally NOT opened here — the guide UI
            // exposes an "Open folder" button per step, so users who want
            // to see the extracted folder can ask for it explicitly.)

            var done = new InstallProgress(InstallStep.Complete,
                $"Extension ready in {folder}. Finish the install in {browser.Kind.DisplayName()}.", 1.0);
            progress?.Report(done);
            return done;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser extension install failed for {Kind}", browser.Kind);
            var fail = new InstallProgress(InstallStep.Failed, $"Install failed: {ex.Message}");
            progress?.Report(fail);
            return fail;
        }
    }

    /// <summary>
    /// Returns the path to the bundled extension zip if publish.ps1 dropped
    /// one next to the installed app, or null when running from a dev build
    /// that didn't bundle anything.
    /// </summary>
    private static string? TryResolveLocalPack(string packLabel, string version)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            LocalPackSubfolder,
            $"SIDM-Extension-{packLabel}-{version}.zip");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Silent re-extract used by the startup auto-updater. Compares the
    /// extracted folder's manifest version to the bundled zip's version
    /// (which is just <see cref="AppInfo.Version"/>); if the folder is
    /// older, overwrites it from the bundled zip without opening the
    /// browser or showing the install dialog. Returns true when an
    /// update was applied (so callers can log it).
    /// </summary>
    public bool ExtractBundledIfOutdated(BrowserKind kind)
    {
        var packLabel = kind.IsChromium() ? "Chromium" : "Firefox";
        var bundledVersion = SIDM.Core.AppInfo.Version;
        var zipPath = TryResolveLocalPack(packLabel, bundledVersion);
        if (zipPath is null)
        {
            // No bundled zip → nothing to do (we don't reach for the
            // network on startup, only on explicit user action).
            return false;
        }

        var folder = ExtensionFolder(kind);
        if (Directory.Exists(folder))
        {
            var installedVersion = ReadManifestVersion(folder);
            if (!string.IsNullOrEmpty(installedVersion)
                && CompareDottedVersions(installedVersion, bundledVersion) >= 0)
            {
                // Already at or beyond the bundled version — leave alone.
                return false;
            }
            _logger.LogInformation(
                "Re-extracting {Kind} extension: installed v{Installed} → bundled v{Bundled}",
                kind, installedVersion ?? "(unknown)", bundledVersion);
        }
        else
        {
            // No folder yet — this is the very-first-run case. Don't
            // pre-create folders for browsers the user hasn't installed
            // the extension into; only re-extract on top of existing ones.
            return false;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            ZipFile.ExtractToDirectory(zipPath, folder, overwriteFiles: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-extract failed for {Kind}", kind);
            return false;
        }
    }

    private static string? ReadManifestVersion(string folder)
    {
        var manifestPath = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Dotted-int compare — "0.1.7" &gt; "0.1.0"; safe with pre-release
    /// suffixes that fall back to ordinal comparison.</summary>
    private static int CompareDottedVersions(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 0;
        var pa = SplitToInts(a);
        var pb = SplitToInts(b);
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return string.CompareOrdinal(a, b);
    }

    private static int[] SplitToInts(string v)
    {
        var clean = new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (clean.Length == 0) return Array.Empty<int>();
        return clean.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .ToArray();
    }

    private async Task DownloadAsync(
        string url, string destination,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("sidm-download");
        client.Timeout = TimeSpan.FromMinutes(2);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var src = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var dst = File.Create(destination);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            read += n;
            if (total > 0)
            {
                var pct = (double)read / total * 0.8; // download is 80% of overall flow
                progress?.Report(new InstallProgress(InstallStep.Downloading,
                    $"Downloading… {read / 1024} KiB / {total / 1024} KiB", pct));
            }
        }
    }

    /// <summary>
    /// Opens the right browser-internal URL to finish the install. For
    /// Chromium browsers we use the browser-specific scheme so the
    /// breadcrumb in the address bar matches the chrome the user sees
    /// (edge://… on Edge, brave://… on Brave). For Firefox we go to
    /// <c>about:debugging</c>. When store URLs are populated we'll prefer
    /// those instead.
    /// </summary>
    public static void OpenExtensionPage(DetectedBrowser browser)
    {
        if (string.IsNullOrEmpty(browser.ExecutablePath)) return;
        var url = GetExtensionPageUrl(browser.Kind);
        // Launch the specific browser exe so the page opens in the browser
        // the user clicked Install for (not whatever the default browser is).
        Process.Start(new ProcessStartInfo
        {
            FileName = browser.ExecutablePath,
            Arguments = url,
            UseShellExecute = false,
        });
    }

    /// <summary>
    /// Returns the URL the guide opens for the given browser. Prefers the
    /// store listing once published (one-click install path) and otherwise
    /// the browser's internal extensions page so the user can sideload the
    /// unpacked build.
    /// </summary>
    public static string GetExtensionPageUrl(BrowserKind kind)
    {
        if (kind.IsChromium() && !string.IsNullOrEmpty(ChromiumStoreUrl)) return ChromiumStoreUrl;
        if (kind == BrowserKind.Firefox && !string.IsNullOrEmpty(FirefoxStoreUrl)) return FirefoxStoreUrl;
        return kind switch
        {
            BrowserKind.Chrome => "chrome://extensions/",
            BrowserKind.Edge => "edge://extensions/",
            BrowserKind.Brave => "brave://extensions/",
            BrowserKind.Firefox => "about:debugging#/runtime/this-firefox",
            _ => "chrome://extensions/",
        };
    }

    /// <summary>
    /// True when this browser will install the extension with a single click
    /// (because we have a published store listing). The guide collapses to a
    /// one-step "click Add" panel in that case.
    /// </summary>
    public static bool HasOneClickInstall(BrowserKind kind) =>
        (kind.IsChromium() && !string.IsNullOrEmpty(ChromiumStoreUrl)) ||
        (kind == BrowserKind.Firefox && !string.IsNullOrEmpty(FirefoxStoreUrl));
}
