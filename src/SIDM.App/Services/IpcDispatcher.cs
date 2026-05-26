using Microsoft.Extensions.Logging;
using SIDM.Ipc;
using SIDM.VideoGrabber;

namespace SIDM.App.Services;

/// <summary>
/// Translates inbound <see cref="IpcMessage"/>s into application actions:
/// hello → reply with capabilities; download → hand off to the
/// <see cref="IDownloadIntake"/> which shows the IDM-style popup (file info,
/// folder picker), and on accept creates the row + starts the engine. The
/// dispatcher itself never touches the database or the UI directly.
/// </summary>
public sealed class IpcDispatcher
{
    /// <summary>
    /// How long to suppress duplicate captures of the same URL. Many "Download as
    /// Excel/CSV" buttons fire the click handler twice (once for the click, once
    /// from a download-started fallback), and Chrome's onCreated re-fires after
    /// our cancel/erase. Two captures within this window collapse to one row.
    /// </summary>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(5);

    private readonly IDownloadIntake _intake;
    private readonly BrowserExtensionPresence _presence;
    private readonly IYtDlpRunner _ytDlpRunner;
    private readonly VideoGrabberSettingsService _videoGrabberSettings;
    private readonly ILogger<IpcDispatcher> _logger;

    private readonly object _recentLock = new();
    private readonly Dictionary<string, (long Id, DateTimeOffset At)> _recent = new();

    public IpcDispatcher(
        IDownloadIntake intake,
        BrowserExtensionPresence presence,
        IYtDlpRunner ytDlpRunner,
        VideoGrabberSettingsService videoGrabberSettings,
        ILogger<IpcDispatcher> logger)
    {
        _intake = intake;
        _presence = presence;
        _ytDlpRunner = ytDlpRunner;
        _videoGrabberSettings = videoGrabberSettings;
        _logger = logger;
    }

    public async Task<IpcMessage> DispatchAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        return message switch
        {
            HelloRequest hello => Hello(hello),
            DownloadRequestMessage dl => await OnDownloadAsync(dl, cancellationToken),
            ListFormatsRequestMessage lf => await OnListFormatsAsync(lf, cancellationToken),
            _ => new ErrorMessage("Unsupported", $"Type {message.GetType().Name} is not handled by SIDM.App"),
        };
    }

    private IpcMessage Hello(HelloRequest req)
    {
        // Parse the client name to learn which browser is on the other end and
        // flip the in-app "extension installed" tracker. This is what makes
        // the install banner disappear and the Install dialog rows show
        // "Connected" once the user finishes the manual Load-unpacked dance.
        if (BrowserExtensionPresence.KindFromClientName(req.ClientName) is { } kind)
        {
            _presence.MarkSeen(kind, req.ClientVersion);
        }
        return new HelloResponse(
            AppName: SIDM.Core.AppInfo.DisplayName,
            AppVersion: SIDM.Core.AppInfo.Version,
            // "list-formats" tells the extension that this build can serve
            // the format-picker; older builds without it will fall back to
            // the plain auto-capture path.
            Capabilities: new[] { "download", "list-formats" },
            // The extension uses this to detect "SIDM has a newer
            // extension on disk than I'm running" and self-reload.
            BundledExtensionVersion: SIDM.Core.AppInfo.Version);
    }

    /// <summary>
    /// Handles a "list-formats" message from the browser picker. Runs
    /// <c>yt-dlp -J</c>, parses the JSON, and returns a curated set of
    /// video resolutions + audio bitrates with file sizes. Errors come
    /// back as an <see cref="ErrorMessage"/> so the picker can show a
    /// useful explanation instead of silently spinning.
    /// </summary>
    private async Task<IpcMessage> OnListFormatsAsync(ListFormatsRequestMessage req, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return new ErrorMessage("InvalidUrl", $"Not a probeable URL: {req.Url}");
        }

        var ytDlpPath = _videoGrabberSettings.ResolveYtDlp();
        if (string.IsNullOrEmpty(ytDlpPath))
        {
            return new ErrorMessage("YtDlpMissing",
                "yt-dlp.exe is not configured. Open Settings → Video downloader to set its path.");
        }

        var result = await _ytDlpRunner.ListFormatsAsync(
            new YtDlpFormatListRequest(req.Url, ytDlpPath), cancellationToken);

        if (!result.Success)
        {
            return new ErrorMessage("ListFormatsFailed", result.FailureMessage ?? "yt-dlp failed");
        }

        // Wire-shape mapping: VideoGrabber's record → SIDM.Ipc's record.
        // They mirror each other but live in separate layers per the
        // architecture rule (VideoGrabber doesn't reference Ipc).
        var wire = result.Formats.Select(f => new FormatOption(
            FormatId: f.FormatId,
            Kind: f.Kind,
            Label: f.Label,
            Ext: f.Ext,
            Vcodec: f.Vcodec,
            Acodec: f.Acodec,
            Height: f.Height,
            Fps: f.Fps,
            AudioBitrateKbps: f.AudioBitrateKbps,
            FileSize: f.FileSize)).ToArray();

        return new ListFormatsResponseMessage(req.Url, result.Title, wire);
    }

    private async Task<IpcMessage> OnDownloadAsync(DownloadRequestMessage req, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return new ErrorMessage("InvalidUrl", $"Not a downloadable URL: {req.Url}");
        }

        // Dedupe identical URLs that arrive within the dedup window (Chrome /
        // website button can fire the same download twice in rapid succession).
        if (TryGetRecent(req.Url) is { } recentId)
        {
            _logger.LogInformation("Suppressed duplicate IPC download for {Url} (existing id {Id})", req.Url, recentId);
            return new DownloadResponseMessage(recentId, "DuplicateSuppressed");
        }

        var result = await _intake.PromptAsync(req, cancellationToken);

        if (result.DownloadId is null)
        {
            _logger.LogInformation("User canceled IPC download for {Url}", req.Url);
            return new DownloadResponseMessage(0, result.Status);
        }

        RecordRecent(req.Url, result.DownloadId.Value);
        _logger.LogInformation("Enqueued IPC download {Id} for {Url}", result.DownloadId, req.Url);
        return new DownloadResponseMessage(result.DownloadId.Value, result.Status);
    }

    private long? TryGetRecent(string url)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_recentLock)
        {
            // Sweep expired entries while we're holding the lock.
            var expired = _recent.Where(kv => now - kv.Value.At > DuplicateWindow).Select(kv => kv.Key).ToArray();
            foreach (var key in expired) _recent.Remove(key);

            return _recent.TryGetValue(url, out var entry) ? entry.Id : null;
        }
    }

    private void RecordRecent(string url, long id)
    {
        lock (_recentLock) { _recent[url] = (id, DateTimeOffset.UtcNow); }
    }
}
