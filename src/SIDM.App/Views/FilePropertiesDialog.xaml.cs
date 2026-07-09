using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using SIDM.App.Services;
using SIDM.App.ViewModels;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

/// <summary>
/// Read-only metadata viewer shown when the user double-clicks a completed
/// (or otherwise terminal) row in the downloads grid. Mirrors the kind of
/// info Windows Explorer's Properties dialog shows — name, size, type,
/// location, plus the SIDM-specific bits (source URL, status, timestamps).
/// </summary>
public partial class FilePropertiesDialog : FluentWindow
{
    private readonly DownloadRowViewModel _row;

    public FilePropertiesDialog(DownloadRowViewModel row)
    {
        InitializeComponent();
        _row = row;
        Title = $"Properties — {row.FileName}";

        HeaderIcon.Source = FileIconProvider.GetIcon(row.ExtensionLower);
        FileNameText.Text = row.FileName;
        HeaderSubText.Text = $"{row.SizeDisplay}   ·   {ShellTypeName(row.ExtensionLower)}";

        TypeText.Text = ShellTypeName(row.ExtensionLower);
        SizeText.Text = row.SizeDisplay;
        StatusText.Text = row.StatusDisplay;
        LocationText.Text = row.TargetPath;
        UrlText.Text = row.Url;

        CreatedText.Text = FormatTime(GetCreated(row));
        CompletedText.Text = FormatTime(GetCompleted(row));
        DurationText.Text = FormatDuration(row);
    }

    private static string ShellTypeName(string extensionLower)
    {
        if (string.IsNullOrWhiteSpace(extensionLower)) return "File";
        return extensionLower.ToUpper(CultureInfo.CurrentCulture) + " file";
    }

    private static DateTimeOffset? GetCreated(DownloadRowViewModel row)
    {
        // We don't expose CreatedUtc directly on the VM (only LastTryDisplay).
        // Pull it off the actual file on disk as the user-facing "created"
        // timestamp — that's what they care about anyway.
        try
        {
            var fi = new FileInfo(row.TargetPath);
            if (fi.Exists) return new DateTimeOffset(fi.CreationTime);
        }
        catch { /* fall through */ }
        return null;
    }

    private static DateTimeOffset? GetCompleted(DownloadRowViewModel row)
    {
        try
        {
            var fi = new FileInfo(row.TargetPath);
            if (fi.Exists) return new DateTimeOffset(fi.LastWriteTime);
        }
        catch { /* fall through */ }
        return null;
    }

    private static string FormatTime(DateTimeOffset? t) =>
        t is null ? "—" : t.Value.LocalDateTime.ToString("MMM dd, yyyy  HH:mm:ss", CultureInfo.CurrentCulture);

    private static string FormatDuration(DownloadRowViewModel row)
    {
        var created = GetCreated(row);
        var completed = GetCompleted(row);
        if (created is null || completed is null) return "—";
        var d = completed.Value - created.Value;
        if (d.TotalSeconds < 0) return "—";
        return d.TotalHours >= 1
            ? $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s"
            : d.TotalMinutes >= 1
                ? $"{d.Minutes}m {d.Seconds}s"
                : $"{d.Seconds}s";
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_row.TargetPath))
        {
            System.Windows.MessageBox.Show(this,
                $"The file isn't at its recorded location — it may have been moved, renamed, or removed:\n\n{_row.TargetPath}",
                "Snw Internet Download Manager",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_row.TargetPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Could not open file:\n{ex.Message}",
                "Snw Internet Download Manager",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_row.TargetPath))
            {
                Process.Start("explorer.exe", $"/select,\"{_row.TargetPath}\"");
            }
            else
            {
                var folder = Path.GetDirectoryName(_row.TargetPath);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    Process.Start("explorer.exe", $"\"{folder}\"");
                }
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Could not open folder:\n{ex.Message}",
                "Snw Internet Download Manager",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_row.Url ?? string.Empty); }
        catch { /* clipboard can throw if another app is holding it; harmless */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
