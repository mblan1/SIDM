using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.Core.Persistence;
using SIDM.VideoGrabber;

namespace SIDM.App.Services;

/// <summary>
/// Loads / persists the paths to <c>yt-dlp.exe</c> and <c>ffmpeg.exe</c>.
/// The actual lookup falls back through <see cref="YtDlpPathResolver"/> when
/// no user override is set, so the app works out-of-the-box if either binary
/// is on PATH.
/// </summary>
public sealed class VideoGrabberSettingsService
{
    public const string YtDlpPathKey = "videograbber.ytDlpPath";
    public const string FfmpegPathKey = "videograbber.ffmpegPath";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VideoGrabberSettingsService> _logger;

    private string? _ytDlpOverride;
    private string? _ffmpegOverride;

    public VideoGrabberSettingsService(
        IServiceScopeFactory scopeFactory,
        ILogger<VideoGrabberSettingsService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string? YtDlpPathOverride => _ytDlpOverride;
    public string? FfmpegPathOverride => _ffmpegOverride;

    /// <summary>Resolved yt-dlp.exe path or null if neither override nor PATH lookup finds one.</summary>
    public string? ResolveYtDlp() => YtDlpPathResolver.ResolveYtDlp(_ytDlpOverride);
    /// <summary>Resolved ffmpeg.exe path or null.</summary>
    public string? ResolveFfmpeg() => YtDlpPathResolver.ResolveFfmpeg(_ffmpegOverride);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            _ytDlpOverride = await settings.GetAsync<string>(YtDlpPathKey, cancellationToken);
            _ffmpegOverride = await settings.GetAsync<string>(FfmpegPathKey, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load VideoGrabber path settings");
        }
    }

    public async Task SetYtDlpPathAsync(string? path, CancellationToken cancellationToken = default)
    {
        _ytDlpOverride = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        if (_ytDlpOverride is null) await settings.RemoveAsync(YtDlpPathKey, cancellationToken);
        else await settings.SetAsync(YtDlpPathKey, _ytDlpOverride, cancellationToken);
    }

    public async Task SetFfmpegPathAsync(string? path, CancellationToken cancellationToken = default)
    {
        _ffmpegOverride = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        if (_ffmpegOverride is null) await settings.RemoveAsync(FfmpegPathKey, cancellationToken);
        else await settings.SetAsync(FfmpegPathKey, _ffmpegOverride, cancellationToken);
    }
}
