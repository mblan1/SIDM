using System.IO;
using System.Windows;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

/// <summary>
/// User's chosen action for a name-collision at add-download time.
/// </summary>
public enum FileExistsChoice
{
    /// <summary>User dismissed the dialog (Esc / Cancel button / X). Caller
    /// should abort the add and leave the existing file/row untouched.</summary>
    Cancel,
    /// <summary>Wipe the existing file (and DB row, if any) and reuse the
    /// same TargetPath for the new download.</summary>
    Replace,
    /// <summary>Auto-rename the new download with a " (1)" / " (2)" suffix
    /// so both files coexist.</summary>
    SaveAsNew,
}

/// <summary>
/// Modal "this file already exists" prompt shown when the Add-download
/// flow detects a collision (file on disk OR an existing SIDM row pointing
/// at the same TargetPath). Three outcomes: Replace, Save as new, Cancel.
/// </summary>
public partial class FileExistsDialog : FluentWindow
{
    public FileExistsChoice Choice { get; private set; } = FileExistsChoice.Cancel;

    public FileExistsDialog(string fileName, string targetPath, string suggestedNewName)
    {
        InitializeComponent();
        FileNameText.Text = fileName;
        FolderText.Text = Path.GetDirectoryName(targetPath) ?? string.Empty;
        SuggestedNameText.Text = suggestedNewName;
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        Choice = FileExistsChoice.Replace;
        DialogResult = true;
        Close();
    }

    private void OnSaveAsNew(object sender, RoutedEventArgs e)
    {
        Choice = FileExistsChoice.SaveAsNew;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = FileExistsChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
