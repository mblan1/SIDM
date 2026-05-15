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

    public MainWindow(MainViewModel viewModel, OnboardingService onboarding, CloseBehaviorService closeBehavior, IServiceProvider services)
    {
        _viewModel = viewModel;
        _onboarding = onboarding;
        _closeBehavior = closeBehavior;
        _services = services;
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
            var dlg = new DownloadProgressDialog(row, engine) { Owner = this };
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

    private async Task MaybeShowWelcomeAsync()
    {
        if (!await _onboarding.ShouldShowWelcomeAsync()) return;

        var welcome = new WelcomeDialog { Owner = this };
        welcome.ShowDialog();
        await _onboarding.MarkCompletedAsync();

        if (welcome.ShouldOpenSettings)
        {
            // Reuse the existing Settings command on the downloads VM so
            // there's a single code path for "open the settings dialog."
            _viewModel.Downloads.OpenSettingsCommand.Execute(null);
        }
    }
}
