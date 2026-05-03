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
