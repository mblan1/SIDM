using System.Diagnostics;

namespace SIDM.VideoGrabber;

/// <summary>
/// Locates the <c>yt-dlp.exe</c> and <c>ffmpeg.exe</c> binaries the
/// VideoGrabber needs. Resolution order:
///   1) explicit path provided by the caller (user setting),
///   2) <c>bin/</c> sibling folder of <see cref="AppContext.BaseDirectory"/>,
///   3) <c>%LocalAppData%\SIDM\bin\</c>,
///   4) the system <c>PATH</c>.
/// Returns <c>null</c> when nothing is found so callers can surface a clear
/// "configure yt-dlp.exe in Settings" error rather than spawn-failing later.
/// </summary>
public static class YtDlpPathResolver
{
    public static string? ResolveYtDlp(string? userOverride = null) =>
        Resolve("yt-dlp.exe", userOverride);

    public static string? ResolveFfmpeg(string? userOverride = null) =>
        Resolve("ffmpeg.exe", userOverride);

    private static string? Resolve(string executableName, string? userOverride)
    {
        if (!string.IsNullOrWhiteSpace(userOverride))
        {
            // The setting may be a directory ("D:\tools") or a full file path.
            if (File.Exists(userOverride)) return userOverride;
            var asDir = Path.Combine(userOverride, executableName);
            if (File.Exists(asDir)) return asDir;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "bin", executableName);
        if (File.Exists(beside)) return beside;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var managed = Path.Combine(localAppData, "SIDM", "bin", executableName);
        if (File.Exists(managed)) return managed;

        return SearchPath(executableName);
    }

    private static string? SearchPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, executableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Some PATH entries on Windows contain invalid characters
                // (e.g. unescaped quotes). Skip rather than crash.
            }
        }
        return null;
    }

    /// <summary>
    /// Runs <c>yt-dlp --version</c> and returns the trimmed first line, or
    /// null if the binary cannot be launched. Used by the settings dialog to
    /// confirm the configured path actually works.
    /// </summary>
    public static async Task<string?> TryGetYtDlpVersionAsync(string ytDlpPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ytDlpPath) || !File.Exists(ytDlpPath)) return null;
        try
        {
            var psi = new ProcessStartInfo(ytDlpPath, "--version")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var line = await p.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
        catch
        {
            return null;
        }
    }
}
