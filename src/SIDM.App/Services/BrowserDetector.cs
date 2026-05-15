using System.IO;
using Microsoft.Win32;
using SIDM.Core;

namespace SIDM.App.Services;

/// <summary>A browser SIDM detected on the user's machine.</summary>
public sealed record DetectedBrowser(BrowserKind Kind, string ExecutablePath);

/// <summary>
/// Finds installed browsers by probing the standard install paths for each.
/// Falls back to <c>App Paths</c> registry lookups so user-portable installs
/// and non-default install directories still get picked up.
///
/// Pure file-system inspection — nothing here requires admin or modifies state.
/// </summary>
public static class BrowserDetector
{
    private static readonly (BrowserKind Kind, string AppPathsKey, string[] CandidatePaths)[] Probes =
    {
        (BrowserKind.Chrome,  "chrome.exe",  new[]
        {
            @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
            @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
            @"%LocalAppData%\Google\Chrome\Application\chrome.exe",
        }),
        (BrowserKind.Edge,    "msedge.exe",  new[]
        {
            @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
            @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe",
        }),
        (BrowserKind.Brave,   "brave.exe",   new[]
        {
            @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe",
            @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe",
            @"%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe",
        }),
        (BrowserKind.Firefox, "firefox.exe", new[]
        {
            @"%ProgramFiles%\Mozilla Firefox\firefox.exe",
            @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe",
        }),
    };

    public static IReadOnlyList<DetectedBrowser> DetectInstalled()
    {
        var found = new List<DetectedBrowser>(4);
        foreach (var (kind, appPathsKey, candidates) in Probes)
        {
            var path = ResolveFromCandidates(candidates) ?? ResolveFromAppPaths(appPathsKey);
            if (path is not null) found.Add(new DetectedBrowser(kind, path));
        }
        return found;
    }

    private static string? ResolveFromCandidates(string[] candidates)
    {
        foreach (var raw in candidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(raw);
            if (File.Exists(expanded)) return expanded;
        }
        return null;
    }

    /// <summary>
    /// Windows "App Paths" — every well-behaved Windows installer drops the
    /// program's exe path here so <c>start chrome</c> resolves. Catches
    /// non-default install directories.
    /// </summary>
    private static string? ResolveFromAppPaths(string exeName)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
                var path = key?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
            }
            catch
            {
                // Registry read can fail in locked-down environments — fall through.
            }
        }
        return null;
    }
}
