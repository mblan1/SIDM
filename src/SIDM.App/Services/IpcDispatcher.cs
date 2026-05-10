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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DownloadEngine _engine;
    private readonly ILogger<IpcDispatcher> _logger;

    public IpcDispatcher(IServiceScopeFactory scopeFactory, DownloadEngine engine, ILogger<IpcDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _engine = engine;
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

        await _engine.StartAsync(id, cancellationToken);
        _logger.LogInformation("Enqueued IPC download {Id} for {Url}", id, req.Url);

        return new DownloadResponseMessage(id, DownloadStatus.Queued.ToString());
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
