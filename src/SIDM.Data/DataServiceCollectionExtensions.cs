using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIDM.Core;
using SIDM.Core.Persistence;
using SIDM.Data.Repositories;

namespace SIDM.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>Default location: <c>%LocalAppData%\SIDM\sidm.db</c>.</summary>
    public static string DefaultConnectionString
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInfo.LocalAppDataFolder);
            return $"Data Source={Path.Combine(folder, "sidm.db")};Cache=Shared;Pooling=True";
        }
    }

    public static IServiceCollection AddSidmData(this IServiceCollection services, string? connectionString = null)
    {
        var cs = connectionString ?? DefaultConnectionString;

        services.AddDbContext<SidmDbContext>(options => options.UseSqlite(cs));

        services.AddScoped<IDownloadRepository, DownloadRepository>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();

        services.AddSingleton<SegmentProgressWriter>(sp =>
            new SegmentProgressWriter(cs, sp.GetRequiredService<ILogger<SegmentProgressWriter>>()));
        services.AddSingleton<IDownloadProgressSink>(sp => sp.GetRequiredService<SegmentProgressWriter>());
        services.AddHostedService(sp => sp.GetRequiredService<SegmentProgressWriter>());

        services.AddSingleton<IHostedService>(sp =>
            new SqliteSchemaInitializer(
                sp,
                sp.GetRequiredService<ILogger<SqliteSchemaInitializer>>(),
                cs));

        return services;
    }
}
