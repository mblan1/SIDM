using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.App.Resources;
using SIDM.App.Services;
using SIDM.Core;

namespace SIDM.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly BrowserExtensionPresence _presence;

    public DownloadsViewModel Downloads { get; }
    public CategoriesViewModel Categories { get; }

    public MainViewModel(DownloadsViewModel downloads, CategoriesViewModel categories, BrowserExtensionPresence presence)
    {
        Downloads = downloads;
        Categories = categories;
        _presence = presence;

        // Banner visibility derives from "have we ever heard from a browser
        // extension". Flip when the first hello arrives.
        ShowBrowserExtensionBanner = !_presence.AnyConnected;
        _presence.FirstSeen += _ => Application.Current?.Dispatcher.BeginInvoke(() =>
            ShowBrowserExtensionBanner = !_presence.AnyConnected);
    }

    public string Title => string.Format(Strings.Main_Greeting_Format, AppInfo.DisplayName, AppInfo.Version);

    /// <summary>
    /// Bound to the install-extension banner above the toolbar. Visible until
    /// at least one browser extension has handshaked with SIDM. Dismissible
    /// per-session via <see cref="DismissBannerCommand"/>.
    /// </summary>
    [ObservableProperty]
    private bool _showBrowserExtensionBanner;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void DismissBanner() => ShowBrowserExtensionBanner = false;
}
