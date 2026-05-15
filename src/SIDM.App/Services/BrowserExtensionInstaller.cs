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
    /// GitHub Releases URL pattern for the per-browser zip. Filename matches
    /// what scripts/publish-extensions.ps1 (or the manual workflow) uploads.
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
        // Filename convention from the publish workflow: "Chrome" zip serves
        // every Chromium-based browser; Firefox has its own gecko-targeted build.
        var packLabel = browser.Kind.IsChromium() ? "Chrome" : "Firefox";
        var version = SIDM.Core.AppInfo.Version;
        var url = string.Format(GitHubReleaseUrl, version, packLabel);
        var folder = ExtensionFolder(browser.Kind);

        try
        {
            // -- Download ----------------------------------------------------
            progress?.Report(new InstallProgress(InstallStep.Downloading,
                $"Downloading {browser.Kind.DisplayName()} extension…"));
            var zipPath = Path.Combine(Path.GetTempPath(), $"SIDM-Extension-{packLabel}-{version}.zip");
            await DownloadAsync(url, zipPath, progress, cancellationToken);

            // -- Extract -----------------------------------------------------
            progress?.Report(new InstallProgress(InstallStep.Extracting, "Extracting…", 0.85));
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            ZipFile.ExtractToDirectory(zipPath, folder, overwriteFiles: true);

            // -- Open browser ------------------------------------------------
            progress?.Report(new InstallProgress(InstallStep.OpeningBrowser, "Opening browser…", 0.95));
            OpenInstallPage(browser, folder);

            // Also pop Explorer at the folder so the user can drag/select it
            // for "Load unpacked" / "Load Temporary Add-on" without hunting.
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not open Explorer for {Folder}", folder); }

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
    /// Chromium browsers we go to <c>chrome://extensions/</c>; for Firefox
    /// to <c>about:debugging</c>. When store URLs are populated we'll prefer
    /// those instead.
    /// </summary>
    private static void OpenInstallPage(DetectedBrowser browser, string extractedFolder)
    {
        string url;
        if (browser.Kind.IsChromium())
        {
            url = !string.IsNullOrEmpty(ChromiumStoreUrl)
                ? ChromiumStoreUrl
                : "chrome://extensions/";
        }
        else
        {
            url = !string.IsNullOrEmpty(FirefoxStoreUrl)
                ? FirefoxStoreUrl
                : "about:debugging#/runtime/this-firefox";
        }

        // Launch the specific browser exe so the page opens in the browser
        // the user clicked Install for (not whatever the default browser is).
        Process.Start(new ProcessStartInfo
        {
            FileName = browser.ExecutablePath,
            Arguments = url,
            UseShellExecute = false,
        });
    }
}
