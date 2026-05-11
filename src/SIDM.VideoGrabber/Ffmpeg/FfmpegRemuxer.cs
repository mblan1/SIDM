using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SIDM.VideoGrabber.Ffmpeg;

/// <summary>What happened to the remux.</summary>
public enum RemuxOutcome
{
    /// <summary>ffmpeg path was null/missing — caller should keep the input file.</summary>
    NotConfigured,
    /// <summary>Remux succeeded; <see cref="FfmpegRemuxResult.OutputPath"/> is the new file.</summary>
    Succeeded,
    /// <summary>ffmpeg ran but exited non-zero; <see cref="FfmpegRemuxResult.FailureMessage"/> has details.</summary>
    Failed,
}

public sealed record FfmpegRemuxResult(RemuxOutcome Outcome, string? OutputPath, string? FailureMessage);

/// <summary>
/// Wraps <c>ffmpeg -i in.ts -c copy out.mp4</c>. Used by the HLS pipeline to
/// turn the concatenated MPEG-TS file into a more shareable MP4 container.
/// "-c copy" is a stream copy (no re-encode), so the operation is essentially
/// I/O-bound and finishes in seconds even for hour-long videos.
///
/// The remuxer is best-effort: if ffmpeg is not configured we return
/// <see cref="RemuxOutcome.NotConfigured"/> and the caller leaves the .ts in
/// place. The .ts is still playable in VLC and the user has a useful artifact
/// either way.
/// </summary>
public sealed class FfmpegRemuxer
{
    private readonly ILogger<FfmpegRemuxer> _logger;

    public FfmpegRemuxer(ILogger<FfmpegRemuxer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Remuxes <paramref name="inputPath"/> (typically a .ts) to MP4 next to
    /// it (same filename, .mp4 extension). On success the input file is
    /// deleted. When <paramref name="ffmpegPath"/> is null or missing the
    /// method returns immediately with <see cref="RemuxOutcome.NotConfigured"/>.
    /// </summary>
    public async Task<FfmpegRemuxResult> RemuxToMp4Async(
        string inputPath,
        string? ffmpegPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            return new FfmpegRemuxResult(RemuxOutcome.NotConfigured, null, null);
        }

        var outputPath = Path.ChangeExtension(inputPath, ".mp4");
        if (File.Exists(outputPath))
        {
            try { File.Delete(outputPath); } catch { /* fall through; ffmpeg will fail with a clear message */ }
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-bsf:a");
        // aac_adtstoasc is needed for HLS AAC tracks; without it many players
        // won't seek inside the resulting MP4. Harmless for streams that
        // already use the right bitstream format.
        psi.ArgumentList.Add("aac_adtstoasc");
        psi.ArgumentList.Add(outputPath);

        _logger.LogInformation("ffmpeg remux: {Input} → {Output}", inputPath, outputPath);

        string? lastStderr = null;
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) lastStderr = e.Data;
        };

        try
        {
            if (!process.Start())
            {
                return new FfmpegRemuxResult(RemuxOutcome.Failed, null, "ffmpeg failed to start");
            }
        }
        catch (Exception ex)
        {
            return new FfmpegRemuxResult(RemuxOutcome.Failed, null, $"ffmpeg failed to start: {ex.Message}");
        }

        process.BeginErrorReadLine();
        // Drain stdout to avoid the pipe-buffer-full deadlock; we don't care
        // about the contents (ffmpeg's interesting output goes to stderr).
        _ = Task.Run(async () =>
        {
            try { await process.StandardOutput.ReadToEndAsync(cancellationToken); }
            catch { /* ignore */ }
        }, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            TryDelete(outputPath);
            return new FfmpegRemuxResult(RemuxOutcome.Failed, null, "Canceled");
        }

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            TryDelete(outputPath);
            return new FfmpegRemuxResult(RemuxOutcome.Failed, null,
                lastStderr ?? $"ffmpeg exited with code {process.ExitCode}");
        }

        // Success — delete the source .ts. Best-effort; if locked, leave it
        // behind so the user can clean up manually.
        TryDelete(inputPath);
        return new FfmpegRemuxResult(RemuxOutcome.Succeeded, outputPath, null);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
