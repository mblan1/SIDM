using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.App.Services;
using SIDM.Core;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

/// <summary>
/// Per-browser install card. Mutable status fields are observable so the
/// dialog re-renders as a download progresses or as the presence service
/// flips the row to "Connected" after the first hello arrives.
/// </summary>
public sealed partial class BrowserInstallRowViewModel : ObservableObject
{
    public DetectedBrowser Browser { get; }
    public string DisplayName => Browser.Kind.DisplayName();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    [NotifyPropertyChangedFor(nameof(IsButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(ButtonAppearance))]
    private string _statusLine = "Not installed";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    [NotifyPropertyChangedFor(nameof(IsButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(ButtonAppearance))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    [NotifyPropertyChangedFor(nameof(IsButtonEnabled))]
    private bool _isWorking;

    public string ButtonLabel => IsConnected ? "Connected" : (IsWorking ? "Installing…" : "Install");
    public bool IsButtonEnabled => !IsConnected && !IsWorking;
    public string ButtonAppearance => IsConnected ? "Secondary" : "Primary";

    public BrowserInstallRowViewModel(DetectedBrowser browser, bool isConnected)
    {
        Browser = browser;
        _isConnected = isConnected;
        _statusLine = isConnected ? "Connected — capturing downloads" : "Not installed yet";
    }
}

public partial class BrowserExtensionInstallDialog : FluentWindow
{
    private readonly BrowserExtensionInstaller _installer;
    private readonly BrowserExtensionPresence _presence;

    public ObservableCollection<BrowserInstallRowViewModel> Rows { get; } = new();

    public BrowserExtensionInstallDialog(BrowserExtensionInstaller installer, BrowserExtensionPresence presence)
    {
        InitializeComponent();
        _installer = installer;
        _presence = presence;

        var detected = BrowserDetector.DetectInstalled();
        if (detected.Count == 0)
        {
            // Show a synthetic "no browser detected" row.
            var stub = new BrowserInstallRowViewModel(
                new DetectedBrowser(BrowserKind.Chrome, string.Empty),
                isConnected: false)
            {
                StatusLine = "No supported browsers found on this machine.",
            };
            Rows.Add(stub);
        }
        else
        {
            foreach (var browser in detected)
            {
                Rows.Add(new BrowserInstallRowViewModel(browser, _presence.IsConnected(browser.Kind)));
            }
        }

        BrowsersList.ItemsSource = Rows;

        // Live-flip rows to "Connected" as soon as the extension says hello.
        _presence.FirstSeen += OnPresenceFirstSeen;
    }

    private void OnPresenceFirstSeen(BrowserKind kind)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var r in Rows)
            {
                if (r.Browser.Kind == kind)
                {
                    r.IsConnected = true;
                    r.IsWorking = false;
                    r.StatusLine = "Connected — capturing downloads";
                }
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _presence.FirstSeen -= OnPresenceFirstSeen;
        base.OnClosed(e);
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn) return;
        if (btn.Tag is not BrowserInstallRowViewModel row) return;
        if (string.IsNullOrEmpty(row.Browser.ExecutablePath)) return;

        row.IsWorking = true;
        row.StatusLine = "Starting…";

        var progress = new Progress<InstallProgress>(p =>
        {
            row.StatusLine = p.Message;
        });

        var result = await _installer.InstallAsync(row.Browser, progress);

        row.IsWorking = false;
        if (result.Step == InstallStep.Complete)
        {
            row.StatusLine = "Browser opened — finish the install there, then come back.";
        }
        else if (result.Step == InstallStep.Failed)
        {
            row.StatusLine = result.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
