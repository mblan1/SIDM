using Microsoft.Extensions.Logging;
using SIDM.Ipc;

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
    private readonly ILogger<IpcDispatcher> _logger;

    private readonly object _recentLock = new();
    private readonly Dictionary<string, (long Id, DateTimeOffset At)> _recent = new();

    public IpcDispatcher(
        IDownloadIntake intake,
        ILogger<IpcDispatcher> logger)
    {
        _intake = intake;
        _logger = logger;
    }

    public async Task<IpcMessage> DispatchAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        return message switch
        {
            HelloRequest hello => Hello(hello),
            DownloadRequestMessage dl => await OnDownloadAsync(dl, cancellationToken),
            _ => new ErrorMessage("Unsupported", $"Type {message.GetType().Name} is not handled by SIDM.App"),
        };
    }

    private static IpcMessage Hello(HelloRequest req) =>
        new HelloResponse(
            AppName: SIDM.Core.AppInfo.DisplayName,
            AppVersion: SIDM.Core.AppInfo.Version,
            Capabilities: new[] { "download" });

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
