using System.Windows;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

public partial class WelcomeDialog : FluentWindow
{
    /// <summary>True when the user clicked "Open Settings" instead of "Get started".</summary>
    public bool ShouldOpenSettings { get; private set; }

    public WelcomeDialog()
    {
        InitializeComponent();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        ShouldOpenSettings = true;
        DialogResult = true;
        Close();
    }

    private void OnGetStarted(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
