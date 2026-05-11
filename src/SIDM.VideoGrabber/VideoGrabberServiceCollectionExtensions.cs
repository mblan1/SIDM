using Microsoft.Extensions.DependencyInjection;

namespace SIDM.VideoGrabber;

public static class VideoGrabberServiceCollectionExtensions
{
    /// <summary>
    /// Registers the yt-dlp process runner. The host app still owns path
    /// resolution + settings, since those are user-configurable and live in
    /// SIDM.App's <c>VideoGrabberSettingsService</c>.
    /// </summary>
    public static IServiceCollection AddSidmVideoGrabber(this IServiceCollection services)
    {
        services.AddSingleton<IYtDlpRunner, YtDlpProcessRunner>();
        return services;
    }
}
