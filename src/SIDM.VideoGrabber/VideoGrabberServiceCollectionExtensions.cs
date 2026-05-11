using Microsoft.Extensions.DependencyInjection;
using SIDM.VideoGrabber.Dash;
using SIDM.VideoGrabber.Ffmpeg;
using SIDM.VideoGrabber.Hls;

namespace SIDM.VideoGrabber;

public static class VideoGrabberServiceCollectionExtensions
{
    /// <summary>
    /// Registers the yt-dlp process runner, the HLS + DASH downloaders, and
    /// the ffmpeg remuxer. The host app still owns path resolution +
    /// settings, since those are user-configurable and live in SIDM.App's
    /// <c>VideoGrabberSettingsService</c>.
    /// </summary>
    public static IServiceCollection AddSidmVideoGrabber(this IServiceCollection services)
    {
        services.AddSingleton<IYtDlpRunner, YtDlpProcessRunner>();
        services.AddSingleton<IHlsHttpClient, HlsHttpClient>();
        services.AddSingleton<HlsDownloader>();
        services.AddSingleton<DashDownloader>();
        services.AddSingleton<FfmpegRemuxer>();
        return services;
    }
}
