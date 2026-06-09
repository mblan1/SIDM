using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
    // Per-user single-instance primitives. The Mutex detects "another SIDM is
    // already running"; the EventWaitHandle lets a second (manual) launch ask
    // the primary instance to surface its main window before exiting.
    private const string SingleInstanceMutexName = @"Local\SIDM.App.SingleInstance";
    private const string ShowMainWindowEventName = @"Local\SIDM.App.ShowMainWindow";

    private readonly IHost? _host;
    private readonly string[] _cliArgs;
    private readonly bool _isCliMode;
    /// <summary>True when launched by SIDM.BrowserHost just to capture a
    /// download (via the <c>--background</c> flag) — the main window is
    /// suppressed and a tray cue is shown instead.</summary>
    private readonly bool _startHidden;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showMainWindowEvent;

    public App()
    {
        // Velopack must be initialized as early as possible — it handles the
        // app's special-purpose lifecycle hooks (--squirrel-firstrun,
        // --squirrel-updated, --squirrel-obsolete) without ever showing the
        // main window. Without this call, an installer/update would launch
        // the WPF UI during a silent hook and the install would visibly hang.
        VelopackApp.Build()
            .WithFirstRun(_ => OnFirstRun())
            // On uninstall, drop the "Start with Windows" Run entry so we don't
            // leave an orphan pointing at a deleted exe. Static + DI-free so it
            // works inside Velopack's fast uninstall hook.
            .WithBeforeUninstallFastCallback((_) => Services.StartupService.RemoveFromStartupForUninstall())
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

        // BrowserHost passes --background when it cold-launches us just to
        // capture a download. In that mode we don't show the main window.
        _startHidden = _cliArgs.Contains("--background");

        // Single-instance gate. If another SIDM is already running, hand off
        // to it instead of spinning up a duplicate (the "two main windows"
        // bug). A manual launch asks the primary to surface its main window;
        // a --background launch just exits (the primary's IPC pipe will get
        // the download from BrowserHost's retry-connect).
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isPrimary);
        _showMainWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowMainWindowEventName);
        if (!isPrimary)
        {
            if (!_startHidden)
            {
                try { _showMainWindowEvent.Set(); } catch { /* best-effort */ }
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            // Shutdown(0) here would still spin up WPF; Environment.Exit is the
            // cleanest way to bail before any window/host is built.
            Environment.Exit(0);
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

        // Re-run NMH registration on every startup. Idempotent — writes the
        // same JSON + registry keys. Needed because Velopack's WithFirstRun
        // hook only fires on FRESH installs; an in-place update that
        // changes the allowed extension ID (or the BrowserHost.exe path)
        // would otherwise leave a stale manifest on disk and the browser
        // extension would fail with "Access to the specified native
        // messaging host is forbidden."
        try
        {
            var registration = NativeHostRegistration.Register();
            Log.Debug("NMH registration refreshed: {Message}", registration.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "NMH registration refresh failed");
        }

        // Auto-re-extract any browser extension folders that are older
        // than the bundled zip. Catches the case where the user upgraded
        // SIDM but never clicked Install/Update — without this, the
        // browser keeps loading the old manifest (and stale content
        // scripts) until the user remembers to re-trigger extraction.
        try
        {
            var installer = _host.Services.GetService<Services.BrowserExtensionInstaller>();
            if (installer is not null)
            {
                foreach (var kind in Enum.GetValues<SIDM.Core.BrowserKind>())
                {
                    if (installer.ExtractBundledIfOutdated(kind))
                    {
                        Log.Information("Auto-updated browser extension for {Kind} to v{Version}",
                            kind, SIDM.Core.AppInfo.Version);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Browser extension auto-extract failed");
        }

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

        // Reconcile "Start with Windows" — defaults ON on a fresh install and
        // rewrites the HKCU Run entry to the current exe path so it survives
        // updates / a moved install. Best-effort; never blocks startup.
        await _host.Services.GetRequiredService<Services.StartupService>().LoadAndReconcileAsync();

        // Load extension presence so the MainWindow banner + install dialog
        // know up-front which browser kinds have ever connected.
        await _host.Services.GetRequiredService<Services.BrowserExtensionPresence>().LoadAsync();

        // Switch to explicit shutdown BEFORE (maybe) skipping the main window
        // show — otherwise, in --background mode with no visible window, WPF's
        // default OnLastWindowClose would shut us down before the download
        // dialog ever appears.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Always create the main window so the tray "Open SIDM" item and the
        // single-instance "show main window" signal have something to surface.
        // Only actually SHOW it for a normal (manual) launch.
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        var tray = _host.Services.GetRequiredService<Services.TrayIconService>();
        tray.Initialize(mainWindow);

        if (_startHidden)
        {
            // Cold launch just to capture a browser download — keep the main
            // window hidden and show a tray cue while the IPC download arrives
            // and opens the Add-download dialog.
            tray.ShowOpeningCue();
        }
        else
        {
            mainWindow.Show();
        }

        // Listen for a second (manual) instance asking us to surface the main
        // window. Background thread blocks on the named event; on signal we
        // marshal to the UI thread and show/activate the window.
        StartShowMainWindowListener(tray);
    }

    /// <summary>
    /// Spawns a background thread that waits on the cross-process
    /// <see cref="ShowMainWindowEventName"/> event. A second instance launched
    /// manually (Start-menu / shortcut) sets it instead of opening its own
    /// window, so the already-running instance comes to the foreground.
    /// </summary>
    private void StartShowMainWindowListener(Services.TrayIconService tray)
    {
        if (_showMainWindowEvent is null) return;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_showMainWindowEvent.WaitOne()) return;
                    tray.ShowMainWindow();
                }
                catch (ObjectDisposedException) { return; }
                catch (AbandonedMutexException) { /* ignore */ }
                catch { return; }
            }
        })
        {
            IsBackground = true,
            Name = "SIDM-ShowMainWindow-Listener",
        };
        thread.Start();
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

        // Release the single-instance handles so a relaunch (incl. Velopack's
        // apply-and-restart) can immediately re-acquire the mutex.
        try { _showMainWindowEvent?.Dispose(); } catch { }
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { /* never block exit */ }

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
