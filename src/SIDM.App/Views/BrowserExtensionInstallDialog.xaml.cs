using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Media = System.Windows.Media;
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
    /// <summary>Bundled extension version SIDM is currently shipping.
    /// Pulled from <see cref="AppInfo.Version"/>.</summary>
    public string BundledVersion => SIDM.Core.AppInfo.Version;

    public DetectedBrowser Browser { get; }
    public string DisplayName => Browser.Kind.DisplayName();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonLabel))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonAppearance))]
    [NotifyPropertyChangedFor(nameof(ShowSecondaryButton))]
    private string _statusLine = "Not installed";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonLabel))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonAppearance))]
    [NotifyPropertyChangedFor(nameof(ShowSecondaryButton))]
    [NotifyPropertyChangedFor(nameof(VersionLine))]
    [NotifyPropertyChangedFor(nameof(VersionLineVisibility))]
    [NotifyPropertyChangedFor(nameof(IsOutdated))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonLabel))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(ShowSecondaryButton))]
    private bool _isWorking;

    /// <summary>Last version the extension reported via hello. Null when no
    /// version is on file (never connected, or pre-0.1.6 SIDM that didn't
    /// record it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonLabel))]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonAppearance))]
    [NotifyPropertyChangedFor(nameof(VersionLine))]
    [NotifyPropertyChangedFor(nameof(VersionLineVisibility))]
    [NotifyPropertyChangedFor(nameof(IsOutdated))]
    private string? _installedVersion;

    /// <summary>True when we know the installed extension is older than
    /// what SIDM bundles (and the row is connected so the comparison
    /// makes sense).</summary>
    public bool IsOutdated =>
        IsConnected
        && !string.IsNullOrEmpty(InstalledVersion)
        && CompareVersions(InstalledVersion!, BundledVersion) < 0;

    /// <summary>Primary action label. Drives the right-hand button.</summary>
    public string PrimaryButtonLabel
    {
        get
        {
            if (IsWorking) return "Working…";
            if (!IsConnected) return "Install";
            if (IsOutdated) return "Update";
            return "Connected";
        }
    }

    public bool IsPrimaryButtonEnabled =>
        !IsWorking && (!IsConnected || IsOutdated);

    public string PrimaryButtonAppearance =>
        IsOutdated ? "Primary"
        : IsConnected ? "Secondary"
        : "Primary";

    /// <summary>Secondary "Reload" link — visible when the row is
    /// connected (regardless of up-to-date status). Lets the user
    /// kick the extension manually after editing the unpacked folder.</summary>
    public bool ShowSecondaryButton => IsConnected && !IsWorking;

    /// <summary>
    /// "Installed 0.1.4  →  bundled 0.1.5" or "Installed 0.1.5" depending on
    /// state. Hidden when we don't yet know the installed version (never
    /// connected). Drives the small info line under the browser name.
    /// </summary>
    public string VersionLine
    {
        get
        {
            if (!IsConnected || string.IsNullOrEmpty(InstalledVersion)) return string.Empty;
            return IsOutdated
                ? $"Installed v{InstalledVersion}  →  update to v{BundledVersion}"
                : $"Installed v{InstalledVersion}";
        }
    }

    public Visibility VersionLineVisibility =>
        IsConnected && !string.IsNullOrEmpty(InstalledVersion) ? Visibility.Visible : Visibility.Collapsed;

    public BrowserInstallRowViewModel(DetectedBrowser browser, bool isConnected, string? installedVersion)
    {
        Browser = browser;
        _isConnected = isConnected;
        _installedVersion = installedVersion;
        _statusLine = isConnected ? "Connected — capturing downloads" : "Not installed yet";
    }

    /// <summary>
    /// Lexicographic dotted-int comparison good enough for our 0.1.x stream.
    /// "0.1.5" &gt; "0.1.4"; "0.2.0" &gt; "0.1.99". Pre-release tags
    /// ("-beta") fall back to ordinal compare on the leftover, which is
    /// acceptable for the manage-extension UX (no false-update prompts).
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 0;
        var pa = SplitToInts(a);
        var pb = SplitToInts(b);
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        // Numeric parts equal — fall back to ordinal string compare so
        // "1.0.0-beta" sorts before "1.0.0".
        return string.CompareOrdinal(a, b);
    }

    private static int[] SplitToInts(string v)
    {
        // Take only the leading numeric-dot prefix — drop any "-beta" / "+build".
        var clean = new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (clean.Length == 0) return Array.Empty<int>();
        return clean.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .ToArray();
    }
}

/// <summary>
/// One step in the per-browser install guide. Number is rendered as a
/// circular badge; <see cref="Path"/> is optional and when non-null shows
/// the file/folder + Copy/Open-folder buttons beneath the description.
/// </summary>
public sealed class GuideStep
{
    public string Number { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? Path { get; init; }
    public bool IsWarning { get; init; }

    public Visibility PathVisibility => string.IsNullOrEmpty(Path) ? Visibility.Collapsed : Visibility.Visible;
    public Media.Brush BadgeBackground => IsWarning
        ? new Media.SolidColorBrush(Media.Color.FromRgb(0xC4, 0x2B, 0x1C))   // red — warning
        : new Media.SolidColorBrush(Media.Color.FromRgb(0x00, 0x67, 0xC0));  // accent blue — step
}

public partial class BrowserExtensionInstallDialog : FluentWindow
{
    private readonly BrowserExtensionInstaller _installer;
    private readonly BrowserExtensionPresence _presence;

    public ObservableCollection<BrowserInstallRowViewModel> Rows { get; } = new();
    public ObservableCollection<GuideStep> GuideSteps { get; } = new();

    private BrowserInstallRowViewModel? _activeGuideRow;

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
                isConnected: false,
                installedVersion: null)
            {
                StatusLine = "No supported browsers found on this machine.",
            };
            Rows.Add(stub);
        }
        else
        {
            foreach (var browser in detected)
            {
                Rows.Add(new BrowserInstallRowViewModel(
                    browser,
                    _presence.IsConnected(browser.Kind),
                    _presence.GetLastSeenVersion(browser.Kind)));
            }
        }

        BrowsersList.ItemsSource = Rows;
        GuideStepsList.ItemsSource = GuideSteps;

        // Live-flip rows to "Connected" as soon as the extension says hello.
        _presence.FirstSeen += OnPresenceFirstSeen;
        // Refresh the version badge whenever the extension reports a new
        // version (typically after the user clicks Update + reloads it in
        // Chrome). Cheaper than polling.
        _presence.VersionChanged += OnPresenceVersionChanged;
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
                    r.InstalledVersion = _presence.GetLastSeenVersion(kind);
                }
            }

            // If the guide is open for this browser, surface a success banner.
            if (_activeGuideRow is not null && _activeGuideRow.Browser.Kind == kind)
            {
                ShowGuideStatus($"Connected — {kind.DisplayName()} is capturing downloads. You can close this window.");
            }
        });
    }

    private void OnPresenceVersionChanged(BrowserKind kind)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var r in Rows)
            {
                if (r.Browser.Kind == kind)
                {
                    r.InstalledVersion = _presence.GetLastSeenVersion(kind);
                    if (!r.IsOutdated && r.IsConnected)
                    {
                        // Just upgraded — show a brief confirmation in the status line.
                        r.StatusLine = $"Connected — running v{r.InstalledVersion}";
                    }
                }
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _presence.FirstSeen -= OnPresenceFirstSeen;
        _presence.VersionChanged -= OnPresenceVersionChanged;
        base.OnClosed(e);
    }

    /// <summary>
    /// Primary action — branches on the row's state. First-time install
    /// shows the full guide; in-place update on a connected row
    /// re-extracts files and tells the browser to reload (no guide noise).
    /// </summary>
    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn) return;
        if (btn.Tag is not BrowserInstallRowViewModel row) return;
        if (string.IsNullOrEmpty(row.Browser.ExecutablePath)) return;

        // Connected + outdated → update path (silent re-extract, reload prompt).
        if (row.IsConnected && row.IsOutdated)
        {
            await DoUpdateAsync(row);
            return;
        }

        // First-time install: full guide.
        await DoInstallAsync(row);
    }

    private async Task DoInstallAsync(BrowserInstallRowViewModel row)
    {
        row.IsWorking = true;
        row.StatusLine = "Starting…";

        // Swap to the guide view straight away — the user gets the steps to
        // read while we extract in the background.
        ShowGuide(row);
        ShowGuideStatus("Preparing extension…");

        var progress = new Progress<InstallProgress>(p =>
        {
            row.StatusLine = p.Message;
            ShowGuideStatus(p.Message);
        });

        var result = await _installer.InstallAsync(row.Browser, progress);

        row.IsWorking = false;
        if (result.Step == InstallStep.Complete)
        {
            row.StatusLine = "Browser opened — follow the steps in SIDM.";
            ShowGuideStatus($"Extension ready. {row.Browser.Kind.DisplayName()} is open at the extensions page — follow the steps below.");
        }
        else if (result.Step == InstallStep.Failed)
        {
            row.StatusLine = result.Message;
            ShowGuideStatus(result.Message);
        }
    }

    /// <summary>
    /// Re-extracts the latest extension pack on top of the existing folder
    /// and opens the browser's extensions page so the user can click the
    /// reload arrow. The extension is already loaded from the same folder
    /// path, so a click on the reload arrow picks up the new files. When
    /// the extension reconnects with a new <c>clientVersion</c>, the row
    /// flips out of "Update" via <see cref="OnPresenceVersionChanged"/>.
    /// </summary>
    private async Task DoUpdateAsync(BrowserInstallRowViewModel row)
    {
        row.IsWorking = true;
        var originalStatus = row.StatusLine;
        row.StatusLine = $"Updating to v{row.BundledVersion}…";

        var result = await _installer.InstallAsync(row.Browser);

        row.IsWorking = false;
        if (result.Step == InstallStep.Complete)
        {
            row.StatusLine = $"Updated — click the reload arrow next to SIDM in {row.Browser.Kind.DisplayName()}.";
        }
        else if (result.Step == InstallStep.Failed)
        {
            row.StatusLine = result.Message;
        }
        else
        {
            row.StatusLine = originalStatus;
        }
    }

    /// <summary>
    /// Reload button — opens the browser's extensions page so the user can
    /// click the per-extension reload arrow. Doesn't re-extract: this is
    /// for the case where they've manually edited the unpacked folder or
    /// the extension is misbehaving and they want to kick it.
    /// </summary>
    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn) return;
        if (btn.Tag is not BrowserInstallRowViewModel row) return;
        if (string.IsNullOrEmpty(row.Browser.ExecutablePath)) return;

        try
        {
            BrowserExtensionInstaller.OpenExtensionPage(row.Browser);
            row.StatusLine = $"Opened {row.Browser.Kind.DisplayName()} — click the reload arrow next to SIDM.";
        }
        catch (Exception ex)
        {
            row.StatusLine = $"Couldn't open browser: {ex.Message}";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // -- Guide view ----------------------------------------------------------

    private void ShowGuide(BrowserInstallRowViewModel row)
    {
        _activeGuideRow = row;

        var kind = row.Browser.Kind;
        var folder = BrowserExtensionInstaller.ExtensionFolder(kind);

        GuideHeader.Text = $"Install SIDM in {kind.DisplayName()}";
        GuideSubheader.Text = BrowserExtensionInstaller.HasOneClickInstall(kind)
            ? "We've opened the official store listing. Click \"Add\" in the browser to finish — this row will flip to \"Connected\" automatically."
            : "Follow the steps below to load the SIDM extension. This row flips to \"Connected\" automatically once the extension says hello.";

        GuideStatusBanner.Visibility = Visibility.Collapsed;
        GuideStatusText.Text = string.Empty;

        GuideSteps.Clear();
        foreach (var step in BuildSteps(kind, folder))
        {
            GuideSteps.Add(step);
        }

        ListView.Visibility = Visibility.Collapsed;
        GuideView.Visibility = Visibility.Visible;
        DialogTitleBar.Title = $"Install in {kind.DisplayName()}";
    }

    private void ShowGuideStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            GuideStatusBanner.Visibility = Visibility.Collapsed;
            return;
        }
        GuideStatusText.Text = message;
        GuideStatusBanner.Visibility = Visibility.Visible;
    }

    private void OnBackToList(object sender, RoutedEventArgs e)
    {
        _activeGuideRow = null;
        GuideView.Visibility = Visibility.Collapsed;
        ListView.Visibility = Visibility.Visible;
        DialogTitleBar.Title = "Install browser extension";
    }

    private void OnOpenExtensionPage(object sender, RoutedEventArgs e)
    {
        if (_activeGuideRow is null) return;
        try
        {
            BrowserExtensionInstaller.OpenExtensionPage(_activeGuideRow.Browser);
        }
        catch (Exception ex)
        {
            ShowGuideStatus($"Could not open browser: {ex.Message}");
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string path && !string.IsNullOrEmpty(path))
        {
            try
            {
                Clipboard.SetText(path);
                ShowGuideStatus("Path copied to clipboard.");
            }
            catch (Exception ex)
            {
                ShowGuideStatus($"Could not copy path: {ex.Message}");
            }
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string path || string.IsNullOrEmpty(path)) return;

        // For Firefox the path points at manifest.json — Explorer opens the
        // parent folder, but with the manifest selected to make it obvious.
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }
            var folder = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            else
            {
                ShowGuideStatus("Folder isn't ready yet — extraction may still be in progress.");
            }
        }
        catch (Exception ex)
        {
            ShowGuideStatus($"Could not open folder: {ex.Message}");
        }
    }

    // -- Per-browser step content -------------------------------------------

    private static IReadOnlyList<GuideStep> BuildSteps(BrowserKind kind, string folder)
    {
        // Once a store listing is live the entire guide collapses to a
        // single "click Add" step — no developer-mode dance needed.
        if (BrowserExtensionInstaller.HasOneClickInstall(kind))
        {
            return new[]
            {
                new GuideStep
                {
                    Number = "1",
                    Title = $"Approve the install in {kind.DisplayName()}",
                    Description = kind.IsChromium()
                        ? "Click \"Add to Chrome\" (or your browser's equivalent) on the store page we just opened, and confirm the permissions prompt."
                        : "Click \"Add to Firefox\" on the page we just opened, and confirm the permissions prompt.",
                },
            };
        }

        var browserName = kind.DisplayName();

        if (kind == BrowserKind.Firefox)
        {
            // Manifest path is what the "Load Temporary Add-on" picker wants.
            var manifestPath = System.IO.Path.Combine(folder, "manifest.json");
            return new[]
            {
                new GuideStep
                {
                    Number = "!",
                    Title = "About this install",
                    Description = "Release Firefox only allows temporary installs of unsigned add-ons — the extension is removed when Firefox restarts and you'll need to repeat these steps. Once SIDM is approved on addons.mozilla.org this becomes a single click.",
                    IsWarning = true,
                },
                new GuideStep
                {
                    Number = "1",
                    Title = "Open about:debugging",
                    Description = $"We've already opened {browserName} to about:debugging for you. If you closed it, click \"Open page in browser\" at the bottom of this window.",
                },
                new GuideStep
                {
                    Number = "2",
                    Title = "Pick \"This Firefox\"",
                    Description = "In the left sidebar of the about:debugging page, click \"This Firefox\".",
                },
                new GuideStep
                {
                    Number = "3",
                    Title = "Click \"Load Temporary Add-on…\"",
                    Description = "Near the top of the page, click \"Load Temporary Add-on…\". A file picker opens.",
                },
                new GuideStep
                {
                    Number = "4",
                    Title = "Select this manifest file",
                    Description = "Paste the path below into the file picker (or click \"Open folder\" to reveal it in Explorer, then drag manifest.json into the picker).",
                    Path = manifestPath,
                },
                new GuideStep
                {
                    Number = "5",
                    Title = "Done",
                    Description = "The SIDM add-on appears under \"Temporary Extensions\". This row will flip to \"Connected\" automatically the moment the extension says hello to SIDM.",
                },
            };
        }

        // Chromium (Chrome, Edge, Brave) — same flow, scheme differs only
        // in the address bar.
        var scheme = kind == BrowserKind.Edge ? "edge"
                   : kind == BrowserKind.Brave ? "brave"
                   : "chrome";
        return new[]
        {
            new GuideStep
            {
                Number = "1",
                Title = $"Open {scheme}://extensions/",
                Description = $"We've already opened {browserName} to the extensions page for you. If you closed it, click \"Open page in browser\" at the bottom of this window.",
            },
            new GuideStep
            {
                Number = "2",
                Title = "Turn on Developer mode",
                Description = "On the extensions page, flip the \"Developer mode\" toggle in the top-right. Three new buttons appear in the top-left: \"Load unpacked\", \"Pack extension\", \"Update\".",
            },
            new GuideStep
            {
                Number = "3",
                Title = "Click \"Load unpacked\"",
                Description = "Click the \"Load unpacked\" button. A folder picker opens.",
            },
            new GuideStep
            {
                Number = "4",
                Title = "Select this folder",
                Description = "Paste the path below into the folder picker's address bar and confirm. The SIDM extension card appears in your extensions list.",
                Path = folder,
            },
            new GuideStep
            {
                Number = "5",
                Title = "Done",
                Description = "Keep the extension enabled. This row will flip to \"Connected\" automatically the moment the extension says hello to SIDM.",
            },
        };
    }
}
