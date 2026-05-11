using System.Windows;
using SIDM.App.Services;
using SIDM.App.ViewModels;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

/// <summary>
/// Per-download progress window with a chunk grid. Opens automatically after
/// the user accepts the "Add download" popup. Multiple instances may be live
/// at once (one per download). Closing the window does NOT cancel — Pause is
/// explicit. The window auto-closes when the download completes.
/// </summary>
public partial class DownloadProgressDialog : FluentWindow
{
    private readonly DownloadRowViewModel _row;
    private readonly DownloadEngine _engine;

    public DownloadProgressDialog(DownloadRowViewModel row, DownloadEngine engine)
    {
        InitializeComponent();
        _row = row;
        _engine = engine;
        DataContext = row;
        Title = $"Downloading — {row.FileName}";

        _row.PropertyChanged += OnRowPropertyChanged;
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DownloadRowViewModel.Status)) return;
        if (_row.Status == SIDM.Core.Models.DownloadStatus.Completed)
        {
            Dispatcher.BeginInvoke(() => Close());
        }
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        _engine.Pause(_row.Id);
    }

    private async void OnResume(object sender, RoutedEventArgs e)
    {
        await _engine.StartAsync(_row.Id);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _row.PropertyChanged -= OnRowPropertyChanged;
        base.OnClosed(e);
    }
}
