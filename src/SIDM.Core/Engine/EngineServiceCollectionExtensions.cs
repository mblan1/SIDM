using Microsoft.Extensions.DependencyInjection;
using SIDM.Core.Abstractions;
using SIDM.Core.Bandwidth;

namespace SIDM.Core.Engine;

public static class EngineServiceCollectionExtensions
{
    /// <summary>Registers the file writer factory, the download orchestrator,
    /// and a single shared <see cref="TokenBucketGovernor"/>.</summary>
    public static IServiceCollection AddSidmEngine(this IServiceCollection services)
    {
        services.AddSingleton<IDownloadFileWriterFactory, SparseFileWriterFactory>();
        services.AddSingleton<IBandwidthGovernor>(_ => new TokenBucketGovernor(bytesPerSecond: 0));
        services.AddSingleton<DownloadOrchestrator>();
        return services;
    }
}
