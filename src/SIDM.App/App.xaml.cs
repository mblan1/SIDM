using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SIDM.App.Composition;
using SIDM.App.Services;
using SIDM.Core;

namespace SIDM.App;

public partial class App : Application
{
    private readonly IHost? _host;
    private readonly string[] _cliArgs;
    private readonly bool _isCliMode;

    public App()
    {
        _cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _isCliMode = IsCliCommand(_cliArgs);

        if (_isCliMode)
        {
            // No log file, no IHost — just enough infrastructure to run the
            // command and exit. Output goes to stderr so it shows in the parent
            // shell when invoked via `SIDM.App.exe --register-hosts`.
            return;
        }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
    private const int ATTACH_PARENT_PROCESS = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (_isCliMode)
        {
            // SIDM.App is a WinExe — by default there's no console attached, so
            // Console.Error.WriteLine vanishes. Reuse the parent shell's console
            // when invoked from a terminal so CLI output is visible.
            AttachConsole(ATTACH_PARENT_PROCESS);

            var exitCode = ExecuteCliCommand(_cliArgs);
            Console.Out.Flush();
            Console.Error.Flush();
            Environment.Exit(exitCode);
            return;
        }
        OnStartupAsync(e);
    }

    private async void OnStartupAsync(StartupEventArgs e)
    {

        Log.Information("Starting {App} v{Version}", AppInfo.DisplayName, AppInfo.Version);
        await _host!.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_isCliMode || _host is null)
        {
            base.OnExit(e);
            return;
        }

        Log.Information("Shutting down");
        using (_host)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    private static bool IsCliCommand(string[] args) =>
        args.Length > 0 && args[0] is "--register-hosts" or "--unregister-hosts" or "--hosts-status";

    private static int ExecuteCliCommand(string[] args)
    {
        switch (args[0])
        {
            case "--register-hosts":
            {
                string? extId = null;
                for (var i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--extension-id") extId = args[i + 1];
                }

                var result = NativeHostRegistration.Register(extId);
                Console.Error.WriteLine($"Result: {(result.Success ? "OK" : "FAIL")} — {result.Message}");
                Console.Error.WriteLine($"  BrowserHost path: {result.BrowserHostPath ?? "(unresolved)"}");
                Console.Error.WriteLine($"  Chromium extension ID: {result.ChromiumExtensionIdUsed}");
                foreach (var path in result.ManifestsWritten) Console.Error.WriteLine($"  Wrote: {path}");
                foreach (var (browser, reason) in result.SkippedBrowsers)
                    Console.Error.WriteLine($"  Skipped {browser}: {reason}");
                return result.Success ? 0 : 1;
            }

            case "--unregister-hosts":
            {
                var result = NativeHostRegistration.Unregister();
                Console.Error.WriteLine($"Result: {(result.Success ? "OK" : "FAIL")} — {result.Message}");
                foreach (var (browser, reason) in result.SkippedBrowsers)
                    Console.Error.WriteLine($"  Skipped {browser}: {reason}");
                return 0;
            }

            case "--hosts-status":
            {
                var status = NativeHostRegistration.GetStatus();
                Console.Error.WriteLine($"BrowserHost.exe: {status.BrowserHostPath ?? "(not found)"}");
                Console.Error.WriteLine($"Manifests exist: {status.ManifestsExist}");
                Console.Error.WriteLine($"  Chromium: {status.ChromiumManifestPath}");
                Console.Error.WriteLine($"  Firefox:  {status.FirefoxManifestPath}");
                foreach (var b in status.Browsers)
                {
                    var label = b.Registered ? "registered" : "NOT registered";
                    Console.Error.WriteLine($"  {b.Browser}: {label}{(b.ManifestPath is null ? "" : " → " + b.ManifestPath)}");
                }
                return 0;
            }

            default:
                Console.Error.WriteLine($"Unknown CLI command: {args[0]}");
                return 2;
        }
    }
}
