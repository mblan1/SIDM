using Microsoft.Extensions.DependencyInjection;
using SIDM.VideoGrabber.Hls;

namespace SIDM.VideoGrabber;

public static class VideoGrabberServiceCollectionExtensions
{
    /// <summary>
    /// Registers the yt-dlp process runner and the HLS downloader. The host
    /// app still owns path resolution + settings, since those are
    /// user-configurable and live in SIDM.App's
    /// <c>VideoGrabberSettingsService</c>.
    /// </summary>
    public static IServiceCollection AddSidmVideoGrabber(this IServiceCollection services)
    {
        services.AddSingleton<IYtDlpRunner, YtDlpProcessRunner>();
        services.AddSingleton<IHlsHttpClient, HlsHttpClient>();
        services.AddSingleton<HlsDownloader>();
        return services;
    }
}
