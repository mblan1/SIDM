using System;
using System.Globalization;
using System.Windows;
using SIDM.App.Services;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

/// <summary>
/// Optional "update available" window. "Update now" downloads the package with
/// a live progress bar (percent + speed + size), then applies it and restarts
/// (Velopack terminates this process). "Later" just closes — the user keeps
/// running the current version. Closing mid-download cancels it; Velopack
/// keeps the partial so a later attempt resumes.
/// </summary>
public partial class UpdateRequiredDialog : FluentWindow
{
    private readonly UpdaterService _updater;
    private readonly System.Threading.CancellationTokenSource _cts = new();

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
        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Starting download…";

        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            DownloadProgress.IsIndeterminate = p.Percent <= 0;
            DownloadProgress.Value = p.Percent;
            ProgressText.Text = FormatProgress(p);
        });

        var downloaded = await _updater.DownloadPendingAsync(progress, _cts.Token);

        if (!downloaded)
        {
            // Either failed or canceled (window closing). If the window is
            // still open, let the user retry.
            if (IsLoaded && IsVisible)
            {
                ProgressText.Text = "Download didn't finish. Click Update now to resume.";
                DownloadProgress.IsIndeterminate = false;
                UpdateButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
            }
            return;
        }

        // Post-download install phases (Velopack unpacks/installs/cleans on
        // apply, out-of-process, then restarts).
        DownloadProgress.IsIndeterminate = true;
        await ShowStageAsync("Verifying download…");
        await ShowStageAsync("Unpacking…");
        await ShowStageAsync("Installing…");
        await ShowStageAsync("Cleaning up old files…");
        ProgressText.Text = "Restarting SIDM…";

        if (!_updater.ApplyPendingAndRestart())
        {
            ProgressText.Text = "Could not apply the update. Try again or close.";
            DownloadProgress.IsIndeterminate = false;
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Cancel an in-flight download so the window can close promptly; the
        // partial package stays on disk for a resume next time.
        try { _cts.Cancel(); } catch { }
        base.OnClosing(e);
    }

    private async System.Threading.Tasks.Task ShowStageAsync(string label)
    {
        ProgressText.Text = label;
        try { await System.Threading.Tasks.Task.Delay(650, _cts.Token); }
        catch (OperationCanceledException) { }
    }

    private static string FormatProgress(UpdateDownloadProgress p)
    {
        var parts = new System.Collections.Generic.List<string> { $"Downloading update… {p.Percent}%" };
        if (p.BytesPerSecond > 1) parts.Add(FormatBytes((long)p.BytesPerSecond) + "/s");
        if (p.TotalBytes > 0) parts.Add($"{FormatBytes(p.ReceivedBytes)} / {FormatBytes(p.TotalBytes)}");
        return string.Join("  ·  ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return string.Format(CultureInfo.CurrentCulture, "{0:F1} {1}", v, units[u]);
    }
}
