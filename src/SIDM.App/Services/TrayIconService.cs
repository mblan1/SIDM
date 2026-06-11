using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace SIDM.App.Services;

/// <summary>
/// Owns the Windows shell tray icon. The icon stays visible for the entire
/// app lifetime — its menu has "Open SIDM" (restore + activate the main
/// window) and "Exit" (mark exit-requested then shutdown). Double-clicking
/// the icon opens the main window.
///
/// Lives as a singleton in DI and is initialized from the App startup once
/// MainWindow has been created.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly CloseBehaviorService _closeBehavior;
    private readonly UpdaterService _updater;
    private WinForms.NotifyIcon? _notifyIcon;
    private Window? _mainWindow;

    public TrayIconService(CloseBehaviorService closeBehavior, UpdaterService updater)
    {
        _closeBehavior = closeBehavior;
        _updater = updater;
    }

    public void Initialize(Window mainWindow)
    {
        if (_notifyIcon is not null) return;
        _mainWindow = mainWindow;

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = TryLoadIcon(),
            Visible = true,
            Text = "Snw Internet Download Manager",
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open SIDM", null, (_, _) => RestoreMainWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.DoubleClick += (_, _) => RestoreMainWindow();

        // Update notifications — balloon tip when the updater reports a new
        // version. Click the balloon to bring up the main window so the user
        // can hit "Install update and restart" from Settings.
        _notifyIcon.BalloonTipClicked += (_, _) => RestoreMainWindow();
        _updater.UpdateAvailable += OnUpdateAvailable;
        // The startup check may have completed BEFORE the tray was wired up
        // (UpdaterStartupCheck runs as IHostedService before MainWindow shows).
        // Replay the last result if it was UpdateAvailable.
        if (_updater.LastResult is { State: UpdateCheckState.UpdateAvailable } pending)
        {
            OnUpdateAvailable(pending);
        }
    }

    private void OnUpdateAvailable(UpdateCheckResult result)
    {
        if (_notifyIcon is null) return;
        _notifyIcon.BalloonTipTitle = "SIDM update available";
        _notifyIcon.BalloonTipText = result.AvailableVersion is { } v
            ? $"Version {v} is ready to install. Click to open SIDM."
            : "A new version is ready to install. Click to open SIDM.";
        _notifyIcon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
        // 10s is the Windows default; some shells ignore custom durations
        // anyway. Long enough to notice without nagging.
        _notifyIcon.ShowBalloonTip(10_000);
    }

    /// <summary>
    /// Resolves the tray icon in this priority order:
    ///   1) The packed PNG resource at <c>Resources/sidm.png</c>, rasterized
    ///      to a 32x32 GDI icon (the SIDM brand logo);
    ///   2) The .exe's embedded icon (set by &lt;ApplicationIcon&gt; in the
    ///      csproj — present in Release/published builds);
    ///   3) The generic Windows application icon (last-ditch fallback so
    ///      the tray is never blank).
    /// </summary>
    private static Icon TryLoadIcon()
    {
        // 1) Packed PNG resource — present in Debug builds where the .exe
        //    icon may not be embedded yet.
        try
        {
            var resourceUri = new Uri("pack://application:,,,/Resources/sidm.png", UriKind.Absolute);
            var streamInfo = WpfApplication.GetResourceStream(resourceUri);
            if (streamInfo is not null)
            {
                using var bmp = new Bitmap(streamInfo.Stream);
                using var resized = new Bitmap(bmp, new System.Drawing.Size(32, 32));
                var hIcon = resized.GetHicon();
                // Clone so the icon owns its handle independent of resized's
                // GDI lifetime (resized goes away at the end of this scope).
                using var transient = Icon.FromHandle(hIcon);
                return (Icon)transient.Clone();
            }
        }
        catch
        {
            // Fall through to .exe icon.
        }

        // 2) Embedded .exe icon
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon is not null) return icon;
            }
        }
        catch
        {
            // Fall through to SystemIcons.
        }

        // 3) Last resort
        return SystemIcons.Application;
    }

    /// <summary>
    /// Public entry point for surfacing the main window — used by the
    /// single-instance handler when a second (manual) launch asks the
    /// running instance to come to the foreground.
    /// </summary>
    public void ShowMainWindow() => RestoreMainWindow();

    private void RestoreMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Dispatcher.BeginInvoke(() =>
        {
            if (!_mainWindow.IsVisible) _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            _mainWindow.Activate();
            // Topmost flip: same trick we use elsewhere to bring a window to
            // the front when Windows would otherwise deny focus-steal.
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
            _mainWindow.Focus();
        });
    }

    private void ExitApp()
    {
        _closeBehavior.RequestExit();
        WpfApplication.Current.Dispatcher.BeginInvoke(() => WpfApplication.Current.Shutdown());
    }

    public void Dispose()
    {
        _updater.UpdateAvailable -= OnUpdateAvailable;
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }
}
