using Microsoft.Extensions.DependencyInjection;
using SIDM.Core.Abstractions;

namespace SIDM.Core.Engine;

public static class EngineServiceCollectionExtensions
{
    /// <summary>Registers the file writer factory and the download orchestrator.</summary>
    public static IServiceCollection AddSidmEngine(this IServiceCollection services)
    {
        services.AddSingleton<IDownloadFileWriterFactory, SparseFileWriterFactory>();
        services.AddSingleton<DownloadOrchestrator>();
        return services;
    }
}
