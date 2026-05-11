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

        args.Add("-o");
        args.Add(Path.Combine(request.OutputDirectory, "%(title)s.%(ext)s"));

        args.Add("-f");
        args.Add(request.FormatSelector ?? "bestvideo*+bestaudio/best");

        if (!string.IsNullOrWhiteSpace(request.FfmpegPath))
        {
            args.Add("--ffmpeg-location");
            args.Add(request.FfmpegPath!);
        }

        args.Add("--");
        args.Add(request.Url);
    }
}
