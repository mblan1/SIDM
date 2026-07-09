using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SIDM.VideoGrabber;

/// <summary>
/// Default <see cref="IYtDlpRunner"/>. Spawns <c>yt-dlp.exe</c>, reads stdout
/// line-by-line, parses progress lines via <see cref="YtDlpProgressParser"/>,
/// and reports them through the supplied <see cref="IProgress{T}"/>.
///
/// The runner also asks yt-dlp to print the final file path on its own line
/// using <c>--print after_move:filepath</c>, so we can record where the file
/// ended up after any ffmpeg post-processing renamed it.
/// </summary>
public sealed class YtDlpProcessRunner : IYtDlpRunner
{
    private const string FinalPathPrefix = "SIDM-FILE:";

    private readonly ILogger<YtDlpProcessRunner> _logger;

    public YtDlpProcessRunner(ILogger<YtDlpProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<YtDlpRunResult> RunAsync(
        YtDlpRunRequest request,
        IProgress<YtDlpProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.YtDlpPath) || !File.Exists(request.YtDlpPath))
        {
            return new YtDlpRunResult(false, null, -1,
                $"yt-dlp.exe not found at '{request.YtDlpPath ?? "(none)"}'. Configure it in Settings → Video downloader.");
        }

        Directory.CreateDirectory(request.OutputDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = request.YtDlpPath,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = request.OutputDirectory,
        };
        BuildArgs(psi.ArgumentList, request);

        _logger.LogInformation("Starting yt-dlp for {Url} → {Dir}", request.Url, request.OutputDirectory);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        string? finalPath = null;
        string? lastStderrLine = null;

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            if (e.Data.StartsWith(FinalPathPrefix, StringComparison.Ordinal))
            {
                finalPath = e.Data[FinalPathPrefix.Length..].Trim();
                return;
            }
            if (YtDlpProgressParser.TryParse(e.Data, out var sample))
            {
                progress?.Report(sample);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            lastStderrLine = e.Data;
            _logger.LogDebug("yt-dlp stderr: {Line}", e.Data);
        };

        try
        {
            if (!process.Start())
            {
                return new YtDlpRunResult(false, null, -1, "Failed to start yt-dlp process.");
            }
        }
        catch (Exception ex)
        {
            return new YtDlpRunResult(false, null, -1, $"Failed to start yt-dlp: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return new YtDlpRunResult(false, null, -2, "Canceled");
        }

        // Drain any final buffered output.
        process.WaitForExit();

        var success = process.ExitCode == 0;
        var message = success
            ? null
            : (lastStderrLine ?? $"yt-dlp exited with code {process.ExitCode}");
        return new YtDlpRunResult(success, finalPath, process.ExitCode, message);
    }

    public async Task<YtDlpFormatListResult> ListFormatsAsync(
        YtDlpFormatListRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.YtDlpPath) || !File.Exists(request.YtDlpPath))
        {
            return new YtDlpFormatListResult(false, null, Array.Empty<YtDlpFormatOption>(),
                $"yt-dlp.exe not found at '{request.YtDlpPath ?? "(none)"}'. Configure it in Settings → Video downloader.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = request.YtDlpPath,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // -J → dump full info as JSON to stdout. --no-warnings keeps stderr
        // quiet so we don't mis-attribute non-fatal stuff to a real error.
        // --no-playlist makes a playlist URL probe ONLY the first video,
        // matching what the user expects when they pick a format.
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(request.Url);

        _logger.LogInformation("Probing yt-dlp formats for {Url}", request.Url);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                return new YtDlpFormatListResult(false, null, Array.Empty<YtDlpFormatOption>(),
                    "Failed to start yt-dlp process.");
            }
        }
        catch (Exception ex)
        {
            return new YtDlpFormatListResult(false, null, Array.Empty<YtDlpFormatOption>(),
                $"Failed to start yt-dlp: {ex.Message}");
        }

        // Read stdout + stderr concurrently so neither blocks the other if
        // yt-dlp emits a large JSON payload (some YouTube videos have
        // hundreds of formats).
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return new YtDlpFormatListResult(false, null, Array.Empty<YtDlpFormatOption>(), "Canceled");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var msg = string.IsNullOrWhiteSpace(stderr)
                ? $"yt-dlp exited with code {process.ExitCode}"
                : stderr.Trim();
            return new YtDlpFormatListResult(false, null, Array.Empty<YtDlpFormatOption>(), msg);
        }

        try
        {
            var (title, formats) = YtDlpFormatJsonParser.Parse(stdout);
            return new YtDlpFormatListResult(true, title, formats, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp -J output could not be parsed");
            return new YtDlpFormatListResult(false, null, Array.Empty<YtDlpFormatOption>(),
                $"Could not parse yt-dlp output: {ex.Message}");
        }
    }

    private static void BuildArgs(System.Collections.ObjectModel.Collection<string> args, YtDlpRunRequest request)
    {
        args.Add("--newline");
        args.Add("--no-colors");
        args.Add("--progress");
        args.Add("--progress-template");
        args.Add(YtDlpProgressParser.ProgressTemplate);
        args.Add("--print");
        args.Add($"after_move:{FinalPathPrefix}%(filepath)s");
        args.Add("--no-warnings");
        // Critical: YouTube radio / playlist URLs (?list=…&start_radio=1)
        // make yt-dlp pull every entry in the list by default. The user
        // picked one video; without this flag, SIDM would queue dozens of
        // unrelated files. Matches the behavior of --no-playlist we
        // already pass during ListFormatsAsync so probing and downloading
        // see the same single video.
        args.Add("--no-playlist");

        // Output template. When the caller passed a filename stem (the title
        // shown/edited in the dialog), use it so the saved file matches what
        // the user saw — yt-dlp still appends the real container via %(ext)s.
        // Otherwise fall back to yt-dlp's own %(title)s.
        args.Add("-o");
        var outName = string.IsNullOrWhiteSpace(request.OutputFileNameStem)
            ? "%(title)s.%(ext)s"
            : SanitizeStem(request.OutputFileNameStem!) + ".%(ext)s";
        args.Add(Path.Combine(request.OutputDirectory, outName));

        args.Add("-f");
        args.Add(request.FormatSelector ?? "bestvideo*+bestaudio/best");

        if (!string.IsNullOrWhiteSpace(request.AudioFormat))
        {
            // Audio-only download with conversion: extract the chosen stream and
            // transcode it into the requested container (mp3/m4a/opus/flac/wav…).
            // Needs ffmpeg. No mp4 merge — there's no video track to merge, and
            // --audio-quality 0 asks for best quality (VBR for lossy codecs;
            // ignored for lossless flac/wav).
            args.Add("-x");
            args.Add("--audio-format");
            args.Add(request.AudioFormat!);
            args.Add("--audio-quality");
            args.Add("0");
        }
        else
        {
            // Force a single .mp4 output when the chosen format is video+audio
            // that yt-dlp has to merge (i.e. selectors using +bestaudio, or
            // any DASH-only video stream). Without this, yt-dlp picks
            // .mkv / .webm and — worse — falls back to keeping the streams
            // SEPARATE when ffmpeg isn't on PATH, producing two files.
            args.Add("--merge-output-format");
            args.Add("mp4");
        }

        if (!string.IsNullOrWhiteSpace(request.FfmpegPath))
        {
            args.Add("--ffmpeg-location");
            args.Add(request.FfmpegPath!);
        }

        args.Add("--");
        args.Add(request.Url);
    }

    /// <summary>
    /// Makes a caller-supplied filename safe for a yt-dlp <c>-o</c> template:
    /// strips path separators, characters Windows forbids, and (critically)
    /// <c>%</c> so the stem can't be parsed as a yt-dlp output field. Trims to
    /// a sane length. Falls back to "video" if nothing usable remains.
    /// </summary>
    private static string SanitizeStem(string stem)
    {
        // Drop any extension the caller left on (we append %(ext)s ourselves).
        stem = Path.GetFileNameWithoutExtension(stem);
        var cleaned = new string(stem
            .Where(c => c != '%' && !Path.GetInvalidFileNameChars().Contains(c))
            .ToArray())
            .Trim();
        if (cleaned.Length > 120) cleaned = cleaned[..120].Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "video" : cleaned;
    }
}
