using System.IO;
using System.Windows;
using Microsoft.Win32;
using SIDM.Core.Models;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

/// <summary>
/// Modal editor for a single <see cref="Category"/>. Used by the Settings
/// dialog for both Add and Edit flows.
/// </summary>
public partial class CategoryEditorDialog : FluentWindow
{
    public CategoryEditorDialog()
    {
        InitializeComponent();
        NameBox.Text = "New category";
    }

    public void LoadFrom(Category category)
    {
        NameBox.Text = category.Name;
        ExtensionsBox.Text = category.Extensions ?? "";
        PathBox.Text = category.DefaultPath ?? "";
    }

    public Category ToCategory() => new()
    {
        Name = NameBox.Text?.Trim() ?? "Category",
        Extensions = string.IsNullOrWhiteSpace(ExtensionsBox.Text) ? null : ExtensionsBox.Text!.Trim(),
        DefaultPath = string.IsNullOrWhiteSpace(PathBox.Text) ? null : PathBox.Text!.Trim(),
    };

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(PathBox.Text) ? PathBox.Text : "",
        };
        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FolderName;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ErrorText.Text = "Name is required.";
            return;
        }
        if (string.IsNullOrWhiteSpace(ExtensionsBox.Text))
        {
            ErrorText.Text = "List at least one extension.";
            return;
        }
        DialogResult = true;
        Close();
    }
}
