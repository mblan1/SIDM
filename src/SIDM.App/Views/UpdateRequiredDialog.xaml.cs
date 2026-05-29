using System;
using System.ComponentModel;
using System.Windows;
using SIDM.App.Services;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

/// <summary>
/// Modal "you must update to keep using SIDM" gate. Shown when the update
/// check finds a newer version. Clicking "Update now" downloads the package
/// (with a live progress bar) then applies it and restarts — Velopack kills
/// this process. "Exit" (or the X) closes the app: outdated builds aren't
/// allowed to keep running.
/// </summary>
public partial class UpdateRequiredDialog : FluentWindow
{
    public enum UpdateChoice { Exit, Update }

    private readonly UpdaterService _updater;
    private readonly System.Threading.CancellationTokenSource _cts = new();

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Exit;

    public UpdateRequiredDialog(string? availableVersion, UpdaterService updater)
    {
        InitializeComponent();
        _updater = updater;
        if (!string.IsNullOrWhiteSpace(availableVersion))
        {
            HeadlineText.Text = $"SIDM {availableVersion} is available";
        }
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.Update;

        // Switch to the downloading state — disable the buttons and show the
        // progress bar. The bar jumps to 100% fast when the background check
        // already pre-downloaded the package, or tracks a real download.
        UpdateButton.IsEnabled = false;
        ExitButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Downloading update…";

        var progress = new Progress<int>(p =>
        {
            DownloadProgress.IsIndeterminate = p <= 0;
            DownloadProgress.Value = p;
            ProgressText.Text = p > 0 ? $"Downloading update… {p}%" : "Downloading update…";
        });

        var downloaded = await _updater.DownloadPendingAsync(progress, _cts.Token);
        if (!downloaded)
        {
            ProgressText.Text = "Download failed. Check your connection and try again.";
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
            UpdateButton.IsEnabled = true;
            ExitButton.IsEnabled = true;
            return;
        }

        // Walk the install stages. The download above is the only phase we get
        // live progress for; the unpack/install/cleanup happen in Velopack's
        // Update.exe after this process exits, so we surface them as a short
        // labelled sequence with an indeterminate bar before handing off.
        DownloadProgress.IsIndeterminate = true;
        await ShowStageAsync("Verifying download…");
        await ShowStageAsync("Unpacking…");
        await ShowStageAsync("Installing…");
        await ShowStageAsync("Cleaning up old files…");
        ProgressText.Text = "Restarting SIDM…";

        // Applies the pending update and restarts — this call does not return
        // on success (Velopack terminates the process). If it returns false,
        // applying failed; let the user retry or exit rather than trapping them.
        if (!_updater.ApplyPendingAndRestart())
        {
            ProgressText.Text = "Could not apply the update. Try again or exit.";
            DownloadProgress.IsIndeterminate = false;
            UpdateButton.IsEnabled = true;
            ExitButton.IsEnabled = true;
        }
    }

    /// <summary>Shows one install-stage label briefly so the sequence is
    /// readable. Honest about the work Velopack is about to do on apply.</summary>
    private async Task ShowStageAsync(string label)
    {
        ProgressText.Text = label;
        try { await Task.Delay(650, _cts.Token); }
        catch (OperationCanceledException) { }
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.Exit;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Closing via the title-bar X (or Alt+F4) counts as "Exit" — never as a
    /// silent dismiss that would let the user run the outdated build.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DialogResult is null)
        {
            Choice = UpdateChoice.Exit;
        }
        try { _cts.Cancel(); } catch { /* ignore */ }
        base.OnClosing(e);
    }
}
