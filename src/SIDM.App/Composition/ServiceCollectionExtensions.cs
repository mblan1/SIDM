using Microsoft.Extensions.DependencyInjection;
using SIDM.App.Services;
using SIDM.App.ViewModels;
using SIDM.Core.Engine;
using SIDM.Core.Http;
using SIDM.Core.Persistence;
using SIDM.Data;

namespace SIDM.App.Composition;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSidmServices(this IServiceCollection services)
    {
        // SIDM.Core engine + HTTP infrastructure
        services.AddSidmHttp();
        services.AddSidmEngine();

        // SIDM.Data — SQLite at %LocalAppData%\SIDM\sidm.db, migrations applied at startup
        services.AddSidmData();

        // App-level services
        services.AddSingleton<UiProgressBus>();
        services.AddSingleton<DownloadEngine>();

        // Compose the progress sink so progress goes to BOTH the SQLite writer
        // and the in-memory UI bus. Replace the singleton IDownloadProgressSink
        // registered by AddSidmData (which pointed at the SegmentProgressWriter).
        services.AddSingleton<IDownloadProgressSink>(sp =>
            new CompositeProgressSink(new IDownloadProgressSink[]
            {
                sp.GetRequiredService<SIDM.Data.Repositories.SegmentProgressWriter>(),
                sp.GetRequiredService<UiProgressBus>(),
            }));

        // Lifecycle services — auto-resume orphans on launch, graceful pause on shutdown.
        services.AddHostedService<DownloadAutoResumeService>();
        services.AddHostedService<GracefulShutdownService>();

        // ViewModels + Windows
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DownloadsViewModel>();

        return services;
    }
}
