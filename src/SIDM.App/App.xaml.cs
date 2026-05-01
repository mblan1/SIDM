using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SIDM.App.Composition;
using SIDM.Core;

namespace SIDM.App;

public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        var logsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppInfo.LocalAppDataFolder,
            "logs");
        Directory.CreateDirectory(logsFolder);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Debug()
            .WriteTo.File(
                path: Path.Combine(logsFolder, "sidm-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) => services.AddSidmServices())
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        Log.Information("Starting {App} v{Version}", AppInfo.DisplayName, AppInfo.Version);
        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("Shutting down");
        using (_host)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}
