using Microsoft.Extensions.DependencyInjection;
using SIDM.App.ViewModels;

namespace SIDM.App.Composition;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSidmServices(this IServiceCollection services)
    {
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainViewModel>();
        return services;
    }
}
