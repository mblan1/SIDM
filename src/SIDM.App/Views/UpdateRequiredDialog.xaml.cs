using System.ComponentModel;
using System.Windows;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

/// <summary>
/// Modal "you must update to keep using SIDM" gate. Shown when the startup
/// update check finds a newer version. Two outcomes:
///   - <see cref="Choice"/> == Update → caller applies the pending update +
///     restarts (Velopack kills this process),
///   - Choice == Exit → caller shuts the app down.
/// There is no third "dismiss and keep using the old version" option — the
/// X / Esc is treated as Exit so the user can't slip past the gate.
/// </summary>
public partial class UpdateRequiredDialog : FluentWindow
{
    public enum UpdateChoice { Exit, Update }

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Exit;

    public UpdateRequiredDialog(string? availableVersion)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(availableVersion))
        {
            HeadlineText.Text = $"SIDM {availableVersion} is available";
        }
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.Update;
        DialogResult = true;
        Close();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.Exit;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Closing via the title-bar X (or Alt+F4) counts as "Exit" — never as a
    /// silent dismiss that would let the user run the outdated build.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        // If neither button set it, the window was closed via X → treat as Exit.
        if (DialogResult is null)
        {
            Choice = UpdateChoice.Exit;
        }
        base.OnClosing(e);
    }
}
