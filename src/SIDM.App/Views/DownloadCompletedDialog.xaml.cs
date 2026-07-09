using System.Diagnostics;
using System.IO;
using System.Windows;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

public partial class DownloadCompletedDialog : FluentWindow
{
    private readonly string _targetPath;

    public DownloadCompletedDialog(string fileName, string targetPath)
    {
        InitializeComponent();
        _targetPath = targetPath;
        FileNameText.Text = fileName;
        TargetPathText.Text = targetPath;
        Title = $"Download complete — {fileName}";
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_targetPath))
        {
            var folder = Path.GetDirectoryName(_targetPath);
            var choice = System.Windows.MessageBox.Show(this,
                $"The file isn't at its recorded location — it may have been moved, renamed, or removed:\n\n{_targetPath}\n\nOpen the containing folder instead?",
                "Snw Internet Download Manager",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (choice == System.Windows.MessageBoxResult.Yes
                && !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                try { Process.Start("explorer.exe", $"\"{folder}\""); Close(); } catch { /* best effort */ }
            }
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_targetPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Could not open file:\n{ex.Message}", "Snw Internet Download Manager",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        Close();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_targetPath))
            {
                Process.Start("explorer.exe", $"/select,\"{_targetPath}\"");
            }
            else
            {
                var folder = Path.GetDirectoryName(_targetPath);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    Process.Start("explorer.exe", $"\"{folder}\"");
                }
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Could not open folder:\n{ex.Message}", "Snw Internet Download Manager",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
