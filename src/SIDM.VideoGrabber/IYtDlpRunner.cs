namespace SIDM.VideoGrabber;

/// <summary>
/// Spawns yt-dlp and streams progress back to the caller. Implemented by
/// <see cref="YtDlpProcessRunner"/>; mocked in tests.
/// </summary>
public interface IYtDlpRunner
{
    Task<YtDlpRunResult> RunAsync(YtDlpRunRequest request, IProgress<YtDlpProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>yt-dlp -J</c> against a URL and returns the curated set of
    /// formats the browser-overlay picker should render. Does NOT download
    /// anything — just enumerates options.
    /// </summary>
    Task<YtDlpFormatListResult> ListFormatsAsync(YtDlpFormatListRequest request, CancellationToken cancellationToken);
}

/// <summary>What to download, where to put it, which binaries to use.</summary>
/// <param name="Url">Video URL (any yt-dlp-supported site).</param>
/// <param name="OutputDirectory">Folder the final file lands in. Created if missing.</param>
/// <param name="YtDlpPath">Resolved yt-dlp executable. Required.</param>
/// <param name="FfmpegPath">Resolved ffmpeg executable. Optional — yt-dlp falls back to single-stream formats if absent.</param>
/// <param name="FormatSelector">yt-dlp <c>-f</c> argument. Defaults to "bestvideo*+bestaudio/best".</param>
/// <param name="OutputFileNameStem">Base file name (without extension) to save
/// as, e.g. the title the user saw/edited in the Add-download dialog. When set,
/// yt-dlp writes <c>&lt;stem&gt;.&lt;ext&gt;</c> so the saved file matches the
/// dialog. Null → yt-dlp's default <c>%(title)s.%(ext)s</c>.</param>
public sealed record YtDlpRunRequest(
    string Url,
    string OutputDirectory,
    string YtDlpPath,
    string? FfmpegPath = null,
    string? FormatSelector = null,
    string? OutputFileNameStem = null);

/// <summary>Terminal outcome.</summary>
/// <param name="Success">True when yt-dlp exited 0 and we recorded a final file path.</param>
/// <param name="FinalFilePath">Resolved path of the merged file (after any ffmpeg post-processing). Null if unknown.</param>
/// <param name="ExitCode">Process exit code (0 on success).</param>
/// <param name="FailureMessage">Human-readable error when <see cref="Success"/> is false.</param>
public sealed record YtDlpRunResult(
    bool Success,
    string? FinalFilePath,
    int ExitCode,
    string? FailureMessage);

/// <summary>Probe arguments for <see cref="IYtDlpRunner.ListFormatsAsync"/>.</summary>
/// <param name="Url">Video URL.</param>
/// <param name="YtDlpPath">Resolved yt-dlp executable. Required.</param>
public sealed record YtDlpFormatListRequest(string Url, string YtDlpPath);

/// <summary>Outcome of a format probe.</summary>
/// <param name="Success">True when yt-dlp exited 0 and produced parsable JSON.</param>
/// <param name="Title">Video title from yt-dlp's info dump, or null.</param>
/// <param name="Formats">Curated rows for the picker — empty on failure.</param>
/// <param name="FailureMessage">Human-readable error when <see cref="Success"/> is false.</param>
public sealed record YtDlpFormatListResult(
    bool Success,
    string? Title,
    YtDlpFormatOption[] Formats,
    string? FailureMessage);
