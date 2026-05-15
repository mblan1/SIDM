using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SIDM.App.Composition;
using SIDM.App.Services;
using SIDM.Core;
using Velopack;

namespace SIDM.App;

public partial class App : Application
{
    private readonly IHost? _host;
    private readonly string[] _cliArgs;
    private readonly bool _isCliMode;

    public App()
    {
        // Velopack must be initialized as early as possible — it handles the
        // app's special-purpose lifecycle hooks (--squirrel-firstrun,
        // --squirrel-updated, --squirrel-obsolete) without ever showing the
        // main window. Without this call, an installer/update would launch
        // the WPF UI during a silent hook and the install would visibly hang.
        VelopackApp.Build()
            .WithFirstRun(_ => OnFirstRun())
            .Run();

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

        // Apply the user's theme BEFORE the main window is created so the
        // first paint is the right color — flipping themes after Show()
        // produces a brief flash of the wrong theme.
        try
        {
            await _host.Services.GetRequiredService<Services.ThemeService>().LoadAndApplyAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Theme load/apply failed; falling back to compiled-in Dark");
        }

        // Seed the five IDM-style default categories (Compressed, Documents,
        // Music, Programs, Video) on first run so the sidebar isn't empty.
        // Idempotent — skips if the user already has any category defined.
        await _host.Services.GetRequiredService<Services.CategorySeeder>().SeedIfEmptyAsync();

        // Load the persisted "close button does what" preference before the
        // main window appears so MainWindow.OnWindowClosing sees the right
        // value the first time the user hits X.
        await _host.Services.GetRequiredService<Services.CloseBehaviorService>().LoadAsync();

        // Load extension presence so the MainWindow banner + install dialog
        // know up-front which browser kinds have ever connected.
        await _host.Services.GetRequiredService<Services.BrowserExtensionPresence>().LoadAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        // Tray icon stays alive for the whole process — initialize after
        // MainWindow exists so the tray's "Open SIDM" item can target it.
        // Switching ShutdownMode to explicit means hiding MainWindow does
        // NOT trigger an OnLastWindowClose shutdown.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _host.Services.GetRequiredService<Services.TrayIconService>().Initialize(mainWindow);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_isCliMode || _host is null)
        {
            base.OnExit(e);
            return;
        }

        Log.Information("Shutting down");
        try
        {
            // Hide + dispose the tray icon explicitly — leaving it visible
            // past process exit produces a phantom icon in the notification
            // area until the user hovers over it.
            if (_host.Services.GetService(typeof(Services.TrayIconService)) is Services.TrayIconService tray)
            {
                tray.Dispose();
            }
        }
        catch { /* best-effort */ }

        using (_host)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    private static bool IsCliCommand(string[] args) =>
        args.Length > 0 && args[0] is "--register-hosts" or "--unregister-hosts" or "--hosts-status";

    /// <summary>
    /// Called by Velopack on the very first launch after a fresh install.
    /// Used to register the Native Messaging Host manifests so the browser
    /// extension works without the user running --register-hosts manually.
    /// Best-effort: failures are logged (when Serilog is up) and never block
    /// the launch.
    /// </summary>
    private static void OnFirstRun()
    {
        try
        {
            var result = NativeHostRegistration.Register();
            // We can't reliably log here — Serilog hasn't been configured yet
            // when first-run runs ahead of the normal startup. Failures will
            // surface via the existing --hosts-status command later.
            _ = result;
        }
        catch
        {
            // Swallow — first-run must not block the app starting.
        }
    }

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
