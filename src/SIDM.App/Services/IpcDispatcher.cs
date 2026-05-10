using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.Core.Models;
using SIDM.Core.Persistence;
using SIDM.Ipc;

namespace SIDM.App.Services;

/// <summary>
/// Translates inbound <see cref="IpcMessage"/>s into application actions:
/// hello → reply with our capabilities; download → create a Downloads row and
/// kick the engine.
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

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DownloadEngine _engine;
    private readonly DownloadCreatedNotifier _notifier;
    private readonly ILogger<IpcDispatcher> _logger;

    private readonly object _recentLock = new();
    private readonly Dictionary<string, (long Id, DateTimeOffset At)> _recent = new();

    public IpcDispatcher(
        IServiceScopeFactory scopeFactory,
        DownloadEngine engine,
        DownloadCreatedNotifier notifier,
        ILogger<IpcDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _engine = engine;
        _notifier = notifier;
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

        var fileName = !string.IsNullOrWhiteSpace(req.FileName)
            ? req.FileName
            : ViewModels.AddDownloadViewModel.GuessFileNameFromUrl(req.Url);

        var folder = !string.IsNullOrWhiteSpace(req.SuggestedFolder)
            ? req.SuggestedFolder
            : ViewModels.AddDownloadViewModel.DefaultDownloadsFolder();

        Directory.CreateDirectory(folder);
        var targetPath = Path.Combine(folder, fileName);

        var headers = MergeHeaders(req.Headers, req.Referer, req.UserAgent);

        var download = new Download
        {
            Url = req.Url,
            FileName = fileName,
            TargetPath = targetPath,
            Status = DownloadStatus.Queued,
            CreatedUtc = DateTimeOffset.UtcNow,
            SegmentCount = 8,
            Mime = req.Mime,
            TotalBytes = req.ExpectedLength,
            HeadersJson = headers is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(headers) : null,
            CookiesJson = req.Cookies is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(req.Cookies) : null,
        };

        long id;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            id = await repo.AddAsync(download, cancellationToken);
        }

        RecordRecent(req.Url, id);
        _notifier.Publish(id);

        await _engine.StartAsync(id, cancellationToken);
        _logger.LogInformation("Enqueued IPC download {Id} for {Url}", id, req.Url);

        return new DownloadResponseMessage(id, DownloadStatus.Queued.ToString());
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

    private static Dictionary<string, string>? MergeHeaders(
        Dictionary<string, string>? extra, string? referer, string? userAgent)
    {
        var merged = extra is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(extra, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(referer) && !merged.ContainsKey("Referer"))
            merged["Referer"] = referer;
        if (!string.IsNullOrWhiteSpace(userAgent) && !merged.ContainsKey("User-Agent"))
            merged["User-Agent"] = userAgent;
        return merged.Count == 0 ? null : merged;
    }
}
