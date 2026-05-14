using System.IO;
using System.Windows;
using Microsoft.Win32;
using SIDM.App.ViewModels;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

public partial class AddDownloadDialog : FluentWindow
{
    public AddDownloadViewModel ViewModel { get; } = new();

    public AddDownloadDialog()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    /// <summary>
    /// Configures the dialog as a standalone popup (no Owner, shows in taskbar,
    /// pops to foreground). Used by the IPC capture flow so the main window
    /// stays where it was — the user only sees the popup, not the whole app.
    /// Must be called before <c>ShowDialog()</c>.
    /// </summary>
    public void ConfigureAsStandalonePopup()
    {
        Owner = null;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Topmost flip in Loaded: bring the dialog above other foreground apps
        // (Windows allows this because it's in response to a user action — the
        // browser download click), then drop Topmost so it doesn't hang above
        // everything else for the rest of its lifetime.
        Loaded += (_, _) =>
        {
            Topmost = true;
            Activate();
            Topmost = false;
            Focus();
        };
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(ViewModel.TargetFolder)
                ? ViewModel.TargetFolder
                : AddDownloadViewModel.DefaultDownloadsFolder(),
        };
        if (dlg.ShowDialog(this) == true)
        {
            ViewModel.TargetFolder = dlg.FolderName;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsValid)
        {
            ViewModel.ErrorMessage = "Enter a valid http(s) URL and a target folder.";
            return;
        }

        try
        {
            Directory.CreateDirectory(ViewModel.TargetFolder);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"Cannot create folder: {ex.Message}";
            return;
        }

        DialogResult = true;
        Close();
    }
}
