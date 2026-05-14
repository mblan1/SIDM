using System.Windows;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

/// <summary>
/// Confirmation prompt shown before the toolbar Remove action. Lets the user
/// choose between just clearing the SIDM entries and also wiping the actual
/// downloaded file(s) from disk. The caller reads <see cref="DeleteFiles"/>
/// after <c>ShowDialog()</c> returns <c>true</c>.
/// </summary>
public partial class RemoveConfirmDialog : FluentWindow
{
    public RemoveConfirmDialog(int count)
    {
        InitializeComponent();
        HeadlineText.Text = count == 1
            ? "Remove this download?"
            : $"Remove {count} downloads?";
        Title = count == 1 ? "Remove download" : $"Remove {count} downloads";
    }

    /// <summary>True when the user ticked the "also delete file(s)" checkbox.</summary>
    public bool DeleteFiles { get; private set; }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DeleteFiles = DeleteFilesCheckBox.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
