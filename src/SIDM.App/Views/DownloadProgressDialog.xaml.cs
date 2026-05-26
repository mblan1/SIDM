using System.Windows;
using SIDM.App.Services;
using SIDM.App.ViewModels;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

/// <summary>
/// Per-download progress window with a chunk grid. Opens automatically after
/// the user accepts the "Add download" popup. Multiple instances may be live
/// at once (one per download). Closing the window does NOT cancel — Pause is
/// explicit. Cancel-and-delete is a separate, confirmed action. The window
/// auto-closes when the download completes.
/// </summary>
public partial class DownloadProgressDialog : FluentWindow
{
    private readonly DownloadRowViewModel _row;
    private readonly DownloadEngine _engine;

    /// <summary>
    /// Invoked when the user confirms "Cancel download". The owner is
    /// responsible for the actual side effects (pause engine, remove row
    /// from the queue + DB, delete the partial file). Null = no callback;
    /// the Cancel button still works but only closes the dialog after
    /// pausing the engine. <see cref="DownloadsViewModel.ShowProgressDialog"/>
    /// is where the real removal callback gets wired up.
    /// </summary>
    private readonly Func<Task>? _onCancelConfirmed;

    public DownloadProgressDialog(
        DownloadRowViewModel row,
        DownloadEngine engine,
        Func<Task>? onCancelConfirmed = null)
    {
        InitializeComponent();
        _row = row;
        _engine = engine;
        _onCancelConfirmed = onCancelConfirmed;
        DataContext = row;
        Title = $"Downloading — {row.FileName}";

        _row.PropertyChanged += OnRowPropertyChanged;
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DownloadRowViewModel.Status)) return;
        if (_row.Status == SIDM.Core.Models.DownloadStatus.Completed)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var done = new DownloadCompletedDialog(_row.FileName, _row.TargetPath)
                {
                    Owner = Owner,
                };
                Close();
                done.Show();
                done.Activate();
            });
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

    /// <summary>
    /// Click handler for the Speed cell — flips between live (EMA) and
    /// long-run average. The label and value both update via bindings.
    /// </summary>
    private void OnToggleSpeedMode(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _row.ShowAverageSpeed = !_row.ShowAverageSpeed;
        e.Handled = true;
    }

    /// <summary>
    /// "Cancel download" button. Shows a confirm dialog and, on yes, pauses
    /// the engine and invokes the owner's cancellation callback (which
    /// removes the row from the queue + DB and deletes the partial file).
    /// </summary>
    private async void OnCancelDownload(object sender, RoutedEventArgs e)
    {
        // Fully qualify — System.Windows.Forms.MessageBox and
        // Wpf.Ui.Controls.MessageBox both ship in this assembly, so an
        // unqualified call is ambiguous.
        var confirm = System.Windows.MessageBox.Show(
            this,
            $"Cancel this download and delete the partial file from disk?\n\n{_row.FileName}\n{_row.TargetPath}",
            "Cancel download",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        // Stop the engine first — the owner's callback assumes nothing's
        // still writing into the sparse file when it tries to delete it.
        try { _engine.Pause(_row.Id); } catch { /* best effort */ }

        if (_onCancelConfirmed is not null)
        {
            try
            {
                await _onCancelConfirmed();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this,
                    $"Failed to cancel the download:\n{ex.Message}",
                    "Cancel download",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        Close();
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _row.PropertyChanged -= OnRowPropertyChanged;
        base.OnClosed(e);
    }
}
