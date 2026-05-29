using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SIDM.App.Services;
using SIDM.App.ViewModels;
using SIDM.App.Views;
using SIDM.Core.Models;
using Wpf.Ui.Controls;

namespace SIDM.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly OnboardingService _onboarding;
    private readonly CloseBehaviorService _closeBehavior;
    private readonly IServiceProvider _services;
    private readonly UpdaterService? _updater;
    private bool _forcedUpdatePrompted;

    public MainWindow(MainViewModel viewModel, OnboardingService onboarding, CloseBehaviorService closeBehavior, IServiceProvider services)
    {
        _viewModel = viewModel;
        _onboarding = onboarding;
        _closeBehavior = closeBehavior;
        _services = services;
        _updater = services.GetService(typeof(UpdaterService)) as UpdaterService;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            // Categories first so the sidebar is populated by the time the
            // download rows start arriving and the CollectionView filter
            // evaluates them.
            await viewModel.Categories.LoadAsync();
            await viewModel.Downloads.LoadAsync();
            await MaybeShowWelcomeAsync();
        };
        Closing += OnWindowClosing;

        // Forced-update gate. Tied to the main window becoming VISIBLE so it
        // only fires on a manual/foreground open — a background download
        // cold-launch (window never shown) is never interrupted, per the
        // product decision. IsVisibleChanged covers re-opening from the tray
        // too; the UpdateAvailable event covers the check completing while
        // the window is already up.
        IsVisibleChanged += OnVisibilityChangedForUpdateGate;
        if (_updater is not null)
        {
            _updater.UpdateAvailable += OnUpdaterReportedAvailable;
            Closing += (_, _) => _updater.UpdateAvailable -= OnUpdaterReportedAvailable;
        }
    }

    private void OnVisibilityChangedForUpdateGate(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) MaybeForceUpdate();
    }

    private void OnUpdaterReportedAvailable(UpdateCheckResult result)
    {
        Dispatcher.BeginInvoke(() => { if (IsVisible) MaybeForceUpdate(); });
    }

    /// <summary>
    /// If the updater has confirmed a newer version, block the app behind a
    /// modal "update or exit" dialog. Only a definitive
    /// <see cref="UpdateCheckState.UpdateAvailable"/> triggers it — feed
    /// errors / offline / up-to-date never lock the user out.
    /// </summary>
    private void MaybeForceUpdate()
    {
        if (_forcedUpdatePrompted || _updater is null) return;
        if (_updater.LastResult is not { State: UpdateCheckState.UpdateAvailable } pending) return;

        _forcedUpdatePrompted = true;

        var dlg = new UpdateRequiredDialog(pending.AvailableVersion) { Owner = this };
        dlg.ShowDialog();

        if (dlg.Choice == UpdateRequiredDialog.UpdateChoice.Update)
        {
            // Velopack kills + relaunches on success. On failure, fall through
            // and let the user keep working rather than trapping them.
            if (_updater.ApplyPendingAndRestart()) return;
            _forcedUpdatePrompted = false; // allow a retry on next open
        }
        else
        {
            // User declined — outdated builds aren't allowed to run.
            _closeBehavior.RequestExit();
            System.Windows.Application.Current.Shutdown();
        }
    }

    /// <summary>
    /// Intercepts the window X button. When the user's close preference is
    /// HideToTray (the default), we cancel the close and just hide the
    /// window — the tray icon keeps the app alive. The tray's "Exit" menu
    /// item sets <see cref="CloseBehaviorService.ExitRequested"/> first so
    /// this handler knows to let the close go through.
    /// </summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeBehavior.ExitRequested) return;
        if (_closeBehavior.Current == CloseBehavior.HideToTray)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// Keeps <see cref="DownloadsViewModel.SelectedRows"/> in lockstep with
    /// the DataGrid's multi-select state. WPF's <c>DataGrid.SelectedItems</c>
    /// isn't directly bindable so we re-project it on every selection change
    /// — the toolbar's bulk commands read from this collection.
    /// </summary>
    private void OnDownloadsGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var rows = _viewModel.Downloads.SelectedRows;
        rows.Clear();
        foreach (var item in DownloadsGrid.SelectedItems)
        {
            if (item is DownloadRowViewModel row) rows.Add(row);
        }
    }

    /// <summary>
    /// Click in the DataGrid's empty area below the last row clears the
    /// selection — same gesture as Explorer / Finder. The check looks for
    /// a <see cref="DataGridRow"/> or <see cref="DataGridColumnHeader"/>
    /// ancestor of the original source; absence means the click landed
    /// in dead space.
    /// </summary>
    private void OnDownloadsGridPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (FindVisualParent<DataGridRow>(src) is null
            && FindVisualParent<DataGridColumnHeader>(src) is null)
        {
            DownloadsGrid.UnselectAll();
        }
    }

    /// <summary>
    /// Click on the window chrome outside the DataGrid (toolbar margin,
    /// status bar, sidebar/grid gap) clears the download selection. Uses
    /// bubbling MouseDown so toolbar buttons, the categories list, and grid
    /// rows — each of which handles its own click — won't trigger this.
    /// </summary>
    private void OnContentMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src
            && FindVisualParent<System.Windows.Controls.DataGrid>(src) is not null)
        {
            // Click was inside the grid; the grid's own handler covers it.
            return;
        }
        DownloadsGrid.UnselectAll();
    }

    private static T? FindVisualParent<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null && obj is not T)
        {
            obj = VisualTreeHelper.GetParent(obj);
        }
        return obj as T;
    }

    /// <summary>
    /// Click handler for the "Install" button on the browser-extension
    /// install banner. Pulls the dialog out of DI so it gets the installer +
    /// presence services constructor-injected.
    /// </summary>
    private void OnOpenBrowserExtensionDialog(object sender, RoutedEventArgs e)
    {
        var installer = _services.GetService(typeof(BrowserExtensionInstaller)) as BrowserExtensionInstaller;
        var presence = _services.GetService(typeof(BrowserExtensionPresence)) as BrowserExtensionPresence;
        if (installer is null || presence is null) return;
        var dlg = new BrowserExtensionInstallDialog(installer, presence) { Owner = this };
        dlg.ShowDialog();
    }

    /// <summary>
    /// Double-clicking a row in the downloads grid opens either the live
    /// progress window (when still active) or the file properties window
    /// (when terminal). Hooked from <c>DataGrid.RowStyle</c> in XAML.
    /// </summary>
    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow gridRow) return;
        if (gridRow.DataContext is not DownloadRowViewModel row) return;

        var isActive = row.Status is DownloadStatus.Queued
            or DownloadStatus.Probing
            or DownloadStatus.Downloading
            or DownloadStatus.Paused;

        if (isActive)
        {
            var engine = _services.GetService(typeof(DownloadEngine)) as DownloadEngine;
            if (engine is null) return;
            // Pull DownloadsViewModel out of DI so the dialog's Cancel button
            // can call back into the canonical "remove row + delete file" path
            // instead of duplicating that logic in the dialog.
            var downloads = _services.GetService(typeof(DownloadsViewModel)) as DownloadsViewModel;
            Func<Task>? cancelCallback = downloads is not null
                ? () => downloads.CancelAndDeleteAsync(row)
                : null;
            var dlg = new DownloadProgressDialog(row, engine, cancelCallback) { Owner = this };
            dlg.Show();
            dlg.Activate();
        }
        else
        {
            var dlg = new FilePropertiesDialog(row) { Owner = this };
            dlg.ShowDialog();
        }

        e.Handled = true;
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // -- Right-click context menu on a download row -------------------------
    // ContextMenu inherits its DataContext from the DataGridRow it's attached
    // to, so each MenuItem.DataContext is the DownloadRowViewModel for the
    // row the user right-clicked. Use the helper to extract it.

    private static DownloadRowViewModel? RowFromMenuSender(object sender) =>
        sender is System.Windows.Controls.MenuItem mi ? mi.DataContext as DownloadRowViewModel : null;

    private void OnContextOpenFile(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenuSender(sender);
        if (row is null || string.IsNullOrWhiteSpace(row.TargetPath)) return;
        if (!File.Exists(row.TargetPath))
        {
            ShowWarning("Open file", $"File no longer exists at:\n{row.TargetPath}");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(row.TargetPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowWarning("Open file", ex.Message);
        }
    }

    private void OnContextOpenFolder(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenuSender(sender);
        if (row is null || string.IsNullOrWhiteSpace(row.TargetPath)) return;
        try
        {
            if (File.Exists(row.TargetPath))
            {
                // /select highlights the file inside its folder rather than
                // opening it — what a user wants from "Open file location".
                Process.Start("explorer.exe", $"/select,\"{row.TargetPath}\"");
                return;
            }
            var folder = Path.GetDirectoryName(row.TargetPath);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                Process.Start("explorer.exe", $"\"{folder}\"");
                return;
            }
            ShowWarning("Open file location", "Neither the file nor its folder exists anymore.");
        }
        catch (Exception ex)
        {
            ShowWarning("Open file location", ex.Message);
        }
    }

    private void OnContextCopyUrl(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenuSender(sender);
        if (row is null) return;
        try
        {
            Clipboard.SetText(row.Url ?? string.Empty);
            _viewModel.Downloads.StatusBarText = "Copied URL to clipboard.";
        }
        catch (Exception ex)
        {
            // Clipboard can transiently fail when another app holds it open.
            ShowWarning("Copy URL", ex.Message);
        }
    }

    private void OnContextPause(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenuSender(sender);
        if (row is null) return;
        if (_viewModel.Downloads.PauseCommand.CanExecute(row))
        {
            _viewModel.Downloads.PauseCommand.Execute(row);
        }
    }

    private void OnContextResume(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenuSender(sender);
        if (row is null) return;
        if (_viewModel.Downloads.ResumeCommand.CanExecute(row))
        {
            _viewModel.Downloads.ResumeCommand.Execute(row);
        }
    }

    private void OnContextRemove(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenuSender(sender);
        if (row is null) return;
        // Route through the same path as the toolbar's Remove command — it
        // opens the confirm dialog (with the "also delete file" option) so
        // a right-click Remove can't accidentally nuke files.
        if (_viewModel.Downloads.RemoveSelectedCommand.CanExecute(row))
        {
            _viewModel.Downloads.RemoveSelectedCommand.Execute(row);
        }
    }

    private void OnContextProperties(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenuSender(sender);
        if (row is null) return;
        var dlg = new FilePropertiesDialog(row) { Owner = this };
        dlg.ShowDialog();
    }

    private void ShowWarning(string title, string message)
    {
        System.Windows.MessageBox.Show(this, message, title,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }

    private async Task MaybeShowWelcomeAsync()
    {
        if (!await _onboarding.ShouldShowWelcomeAsync()) return;

        var welcome = new WelcomeDialog { Owner = this };
        welcome.ShowDialog();
        await _onboarding.MarkCompletedAsync();

        if (welcome.ShouldOpenExtensionInstaller)
        {
            // Reuse the same dialog path as the toolbar/banner so there's a
            // single code path for opening the extension installer.
            OnOpenBrowserExtensionDialog(this, new RoutedEventArgs());
        }

        if (welcome.ShouldOpenSettings)
        {
            // Reuse the existing Settings command on the downloads VM so
            // there's a single code path for "open the settings dialog."
            _viewModel.Downloads.OpenSettingsCommand.Execute(null);
        }
    }
}
