using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.App.Resources;
using SIDM.App.Services;
using SIDM.Core;

namespace SIDM.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly BrowserExtensionPresence _presence;
    private readonly UpdaterService _updater;

    public DownloadsViewModel Downloads { get; }
    public CategoriesViewModel Categories { get; }

    public MainViewModel(DownloadsViewModel downloads, CategoriesViewModel categories, BrowserExtensionPresence presence, UpdaterService updater)
    {
        Downloads = downloads;
        Categories = categories;
        _presence = presence;
        _updater = updater;

        // Banner visibility derives from "have we ever heard from a browser
        // extension". Flip when the first hello arrives.
        ShowBrowserExtensionBanner = !_presence.AnyConnected;
        _presence.FirstSeen += _ => Application.Current?.Dispatcher.BeginInvoke(() =>
            ShowBrowserExtensionBanner = !_presence.AnyConnected);

        // Update banner — shown when a check (startup or the periodic re-check)
        // finds a newer version. Reflect whatever the updater already knows,
        // then keep it live via the event.
        ApplyUpdateResult(_updater.LastResult);
        _updater.UpdateAvailable += r => Application.Current?.Dispatcher.BeginInvoke(() => ApplyUpdateResult(r));
    }

    private void ApplyUpdateResult(UpdateCheckResult? result)
    {
        if (result is { State: UpdateCheckState.UpdateAvailable })
        {
            UpdateAvailableVersion = result.AvailableVersion;
            UpdateBannerText = result.AvailableVersion is { } v
                ? $"SIDM {v} is available."
                : "A new version of SIDM is available.";
            IsUpdateAvailable = true;
        }
    }

    /// <summary>True once a newer version has been found — drives the update
    /// banner + button in the main window.</summary>
    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _updateBannerText = "A new version of SIDM is available.";

    /// <summary>Version string for the available update; passed to the update
    /// dialog so it can show "SIDM x.y.z is available".</summary>
    public string? UpdateAvailableVersion { get; private set; }

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
