using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SIDM.App.Services;
using SIDM.App.ViewModels;
using SIDM.Core;
using SIDM.Core.Persistence;
using SIDM.VideoGrabber;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

public partial class SettingsDialog : FluentWindow
{
    private readonly DownloadQueue _queue;
    private readonly BandwidthSettingsService _bandwidth;
    private readonly SchedulerService _scheduler;
    private readonly VideoGrabberSettingsService _videoGrabber;
    private readonly UpdaterService _updater;
    private readonly CrashReportingService _crashReporting;
    private readonly ThemeService _theme;
    private readonly CloseBehaviorService _closeBehavior;
    private readonly BrowserExtensionInstaller _extensionInstaller;
    private readonly BrowserExtensionPresence _extensionPresence;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;

    public SettingsViewModel ViewModel { get; } = new();

    public SettingsDialog(
        DownloadQueue queue,
        BandwidthSettingsService bandwidth,
        SchedulerService scheduler,
        VideoGrabberSettingsService videoGrabber,
        UpdaterService updater,
        CrashReportingService crashReporting,
        ThemeService theme,
        CloseBehaviorService closeBehavior,
        BrowserExtensionInstaller extensionInstaller,
        BrowserExtensionPresence extensionPresence,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory)
    {
        InitializeComponent();
        _queue = queue;
        _bandwidth = bandwidth;
        _scheduler = scheduler;
        _videoGrabber = videoGrabber;
        _updater = updater;
        _crashReporting = crashReporting;
        _theme = theme;
        _closeBehavior = closeBehavior;
        _extensionInstaller = extensionInstaller;
        _extensionPresence = extensionPresence;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        DataContext = ViewModel;

        ViewModel.MaxConcurrent = _queue.MaxConcurrent;
        ViewModel.SetFromBytes(_bandwidth.CurrentBytesPerSecond);
        ViewModel.YtDlpPath = _videoGrabber.YtDlpPathOverride;
        ViewModel.FfmpegPath = _videoGrabber.FfmpegPathOverride;
        ViewModel.YtDlpStatus = BuildResolvedPathStatus();
        ViewModel.UpdateFeedUrl = _updater.FeedUrl;
        ViewModel.AutoCheckUpdates = _updater.AutoCheckOnStartup;
        ViewModel.CrashReportsEnabled = _crashReporting.IsEnabled;
        ViewModel.CrashReportsDsn = _crashReporting.Dsn;
        ViewModel.ThemeIndex = (int)_theme.Current;
        ViewModel.CloseBehaviorIndex = (int)_closeBehavior.Current;

        _ = LoadRulesAsync();
        _ = LoadCategoriesAsync();
        LoadBrowserExtensions();

        // Live-update the rows when the extension first connects while
        // Settings is open. Unsubscribe on Close so the dialog can be GC'd.
        _extensionPresence.FirstSeen += OnExtensionFirstSeen;
        Closed += (_, _) => _extensionPresence.FirstSeen -= OnExtensionFirstSeen;
    }

    /// <summary>
    /// Builds one row per supported BrowserKind, marking each as installed
    /// (per BrowserDetector) and connected (per BrowserExtensionPresence).
    /// Non-detected browsers stay in the list so the user can see the full
    /// coverage matrix at a glance.
    /// </summary>
    private void LoadBrowserExtensions()
    {
        var installed = BrowserDetector.DetectInstalled()
            .Select(b => b.Kind)
            .ToHashSet();
        ViewModel.BrowserExtensions.Clear();
        foreach (var kind in Enum.GetValues<BrowserKind>())
        {
            ViewModel.BrowserExtensions.Add(new BrowserExtensionRowViewModel(
                kind,
                isInstalled: installed.Contains(kind),
                lastSeen: _extensionPresence.GetLastSeen(kind)));
        }
    }

    private void OnExtensionFirstSeen(BrowserKind kind)
    {
        // Marshal back to the UI thread — FirstSeen fires from the IPC worker.
        Dispatcher.Invoke(() =>
        {
            var row = ViewModel.BrowserExtensions.FirstOrDefault(r => r.Kind == kind);
            row?.Update(isInstalled: row.IsInstalled, lastSeen: _extensionPresence.GetLastSeen(kind));
        });
    }

    private void OnManageBrowserExtensions(object sender, RoutedEventArgs e)
    {
        var dlg = new BrowserExtensionInstallDialog(_extensionInstaller, _extensionPresence) { Owner = this };
        dlg.ShowDialog();
        // Refresh in case install state changed (e.g., extension was newly
        // sideloaded between Open and Close of the inner dialog).
        LoadBrowserExtensions();
    }

    private string BuildResolvedPathStatus()
    {
        var resolvedYt = _videoGrabber.ResolveYtDlp();
        var resolvedFf = _videoGrabber.ResolveFfmpeg();
        if (resolvedYt is null) return "yt-dlp.exe: not found";
        var ff = resolvedFf is null ? "ffmpeg: not found (downloads still work for non-merged formats)" : $"ffmpeg: {resolvedFf}";
        return $"yt-dlp.exe: {resolvedYt}\n{ff}";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnAccept(object sender, RoutedEventArgs e)
    {
        if (ViewModel.MaxConcurrent < 1)
        {
            ViewModel.ErrorMessage = "Maximum concurrent downloads must be at least 1.";
            return;
        }
        if (ViewModel.BandwidthKiBPerSec < 0)
        {
            ViewModel.ErrorMessage = "Bandwidth cap cannot be negative.";
            return;
        }

        // Apply the in-memory caps now (so existing downloads feel the change),
        // then persist for next launch.
        _queue.MaxConcurrent = ViewModel.MaxConcurrent;
        await _bandwidth.SetAsync(ViewModel.BandwidthBytesPerSecond);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await settings.SetAsync(DownloadQueue.MaxConcurrentSettingKey, ViewModel.MaxConcurrent);

        // Keep the scheduler's "user baseline" in sync so it doesn't lock in
        // the now-stale value next time it re-applies a rule override.
        _scheduler.SetUserBaseline(ViewModel.MaxConcurrent, ViewModel.BandwidthBytesPerSecond);

        // Persist VideoGrabber binary paths.
        await _videoGrabber.SetYtDlpPathAsync(ViewModel.YtDlpPath);
        await _videoGrabber.SetFfmpegPathAsync(ViewModel.FfmpegPath);

        // Persist updater preferences.
        await _updater.SetFeedUrlAsync(ViewModel.UpdateFeedUrl);
        await _updater.SetAutoCheckAsync(ViewModel.AutoCheckUpdates);

        // Persist crash-reporting preferences. Order matters: write the DSN
        // first so the Start() that follows a flip-to-enabled reads the new
        // value.
        await _crashReporting.SetDsnAsync(ViewModel.CrashReportsDsn);
        await _crashReporting.SetEnabledAsync(ViewModel.CrashReportsEnabled);

        await _theme.SetAsync((AppTheme)ViewModel.ThemeIndex);
        await _closeBehavior.SaveAsync((CloseBehavior)ViewModel.CloseBehaviorIndex);

        DialogResult = true;
        Close();
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        // The user may have typed a feed URL but not hit Save yet; honor the
        // current text-box value so "Check now" works without a save round-trip.
        await _updater.SetFeedUrlAsync(ViewModel.UpdateFeedUrl);
        ViewModel.UpdateStatus = "Checking…";
        ViewModel.UpdateReadyToApply = false;

        var result = await _updater.CheckAsync();
        ViewModel.UpdateStatus = result.Message;
        ViewModel.UpdateReadyToApply = result.State == UpdateCheckState.UpdateAvailable;
    }

    private void OnApplyUpdate(object sender, RoutedEventArgs e)
    {
        // ApplyUpdatesAndRestart kills the current process and relaunches the
        // new one — the OS dispatches a fresh SIDM.App.exe under the hood.
        // There's no path back from here; nothing to await.
        var applied = _updater.ApplyPendingAndRestart();
        if (!applied)
        {
            ViewModel.UpdateStatus = "No pending update to apply.";
            ViewModel.UpdateReadyToApply = false;
        }
    }

    private void OnBrowseYtDlp(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "yt-dlp executable|yt-dlp.exe|All files|*.*",
            InitialDirectory = TryGetDirectory(ViewModel.YtDlpPath),
        };
        if (dlg.ShowDialog(this) == true)
        {
            ViewModel.YtDlpPath = dlg.FileName;
        }
    }

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "ffmpeg executable|ffmpeg.exe|All files|*.*",
            InitialDirectory = TryGetDirectory(ViewModel.FfmpegPath),
        };
        if (dlg.ShowDialog(this) == true)
        {
            ViewModel.FfmpegPath = dlg.FileName;
        }
    }

    private async void OnTestYtDlp(object sender, RoutedEventArgs e)
    {
        var resolved = YtDlpPathResolver.ResolveYtDlp(ViewModel.YtDlpPath);
        if (resolved is null)
        {
            ViewModel.YtDlpStatus = "yt-dlp.exe not found at the given path and not on PATH.";
            return;
        }
        ViewModel.YtDlpStatus = $"Checking {resolved}…";
        var version = await YtDlpPathResolver.TryGetYtDlpVersionAsync(resolved);
        ViewModel.YtDlpStatus = version is null
            ? $"{resolved} did not respond to --version."
            : $"OK — yt-dlp {version} at {resolved}";
    }

    // yt-dlp's stable channel publishes a single Windows .exe asset on every
    // release tag, mirrored to /latest/. We pin to /latest/ rather than a
    // version because the rate at which yt-dlp issues patches outpaces our
    // ability to keep a hardcoded version current.
    private const string YtDlpReleaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    /// <summary>SIDM-managed install location — matches one of the paths the
    /// resolver searches, so a successful install is picked up automatically
    /// when the user override is empty.</summary>
    private static string ManagedYtDlpPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SIDM", "bin", "yt-dlp.exe");

    /// <summary>
    /// Downloads yt-dlp.exe from GitHub Releases into the SIDM-managed bin
    /// folder. Clears any user-pinned override so the resolver picks up the
    /// fresh managed copy on the next download.
    /// </summary>
    private async void OnInstallYtDlp(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn) return;
        var originalContent = btn.Content;
        btn.IsEnabled = false;
        btn.Content = "Installing…";
        ViewModel.YtDlpStatus = $"Downloading yt-dlp.exe from {YtDlpReleaseUrl}…";

        try
        {
            var dest = ManagedYtDlpPath;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await DownloadFileAsync(YtDlpReleaseUrl, dest);

            // Surface the install location in the path field so the user
            // can see where the binary landed without having to dig
            // through %LocalAppData%. Persisted as the override so the
            // resolver returns the same path on the next download.
            // Uninstall clears this when it deletes the file.
            ViewModel.YtDlpPath = dest;
            await _videoGrabber.SetYtDlpPathAsync(dest);

            var version = await YtDlpPathResolver.TryGetYtDlpVersionAsync(dest);
            ViewModel.YtDlpStatus = version is null
                ? $"Installed to {dest}, but it didn't respond to --version."
                : $"Installed yt-dlp {version} at {dest}";
        }
        catch (Exception ex)
        {
            ViewModel.YtDlpStatus = $"Install failed: {ex.Message}";
        }
        finally
        {
            btn.Content = originalContent;
            btn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Removes the SIDM-managed yt-dlp.exe (only that one — never deletes a
    /// user-pinned path). If the user also has an override set pointing at
    /// the managed location, we clear that too so the UI is self-consistent.
    /// </summary>
    private async void OnUninstallYtDlp(object sender, RoutedEventArgs e)
    {
        var dest = ManagedYtDlpPath;
        if (!File.Exists(dest))
        {
            ViewModel.YtDlpStatus = "Nothing to uninstall — no SIDM-managed yt-dlp.exe was found.";
            return;
        }

        try
        {
            File.Delete(dest);

            // If the override points at the now-deleted file, clear it so the
            // resolver falls through to PATH instead of returning a stale
            // path that no longer exists.
            if (string.Equals(ViewModel.YtDlpPath?.Trim(), dest, StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.YtDlpPath = string.Empty;
                await _videoGrabber.SetYtDlpPathAsync(null);
            }

            ViewModel.YtDlpStatus = BuildResolvedPathStatus();
        }
        catch (Exception ex)
        {
            ViewModel.YtDlpStatus = $"Uninstall failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-runs the resolver and rebuilds the status line. Cheap; useful after
    /// the user installs yt-dlp themselves (via choco / winget) or drops a
    /// portable copy somewhere on PATH.
    /// </summary>
    private void OnRefreshYtDlp(object sender, RoutedEventArgs e)
    {
        ViewModel.YtDlpStatus = BuildResolvedPathStatus();
    }

    /// <summary>
    /// Plain streamed download — same pattern as <see cref="BrowserExtensionInstaller"/>.
    /// Writes to a <c>.partial</c> sidecar and moves on completion so a
    /// crash mid-download doesn't leave a half-written exe.
    /// </summary>
    private async Task DownloadFileAsync(string url, string destination)
    {
        var client = _httpClientFactory.CreateClient("sidm-download");
        client.Timeout = TimeSpan.FromMinutes(10);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var tempPath = destination + ".partial";
        await using (var src = await response.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(tempPath))
        {
            await src.CopyToAsync(dst);
        }

        if (File.Exists(destination)) File.Delete(destination);
        File.Move(tempPath, destination);
    }

    // ---- ffmpeg manage actions ------------------------------------------

    // yt-dlp/FFmpeg-Builds publishes a static Windows build on every rolling
    // "latest" tag. We pull the smaller "essentials" zip and pluck out just
    // bin/ffmpeg.exe — yt-dlp doesn't need ffprobe/ffplay for muxing video+audio.
    private const string FfmpegReleaseUrl =
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

    private static string ManagedFfmpegPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SIDM", "bin", "ffmpeg.exe");

    /// <summary>
    /// Downloads the yt-dlp/FFmpeg-Builds zip and extracts ONLY bin/ffmpeg.exe
    /// into the SIDM-managed bin folder. The other binaries (ffprobe, ffplay)
    /// aren't needed for yt-dlp's video+audio merge and would just bloat disk.
    /// </summary>
    private async void OnInstallFfmpeg(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn) return;
        var originalContent = btn.Content;
        btn.IsEnabled = false;
        btn.Content = "Installing…";
        ViewModel.YtDlpStatus = "Downloading ffmpeg (this is a ~70 MB zip)…";

        var tempZip = Path.Combine(Path.GetTempPath(), $"sidm-ffmpeg-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadFileAsync(FfmpegReleaseUrl, tempZip);
            ViewModel.YtDlpStatus = "Extracting ffmpeg.exe from the bundle…";

            Directory.CreateDirectory(Path.GetDirectoryName(ManagedFfmpegPath)!);

            using (var archive = System.IO.Compression.ZipFile.OpenRead(tempZip))
            {
                // Zip layout: ffmpeg-master-2026-…-gpl/bin/ffmpeg.exe
                var entry = archive.Entries.FirstOrDefault(en =>
                    en.FullName.Replace('\\', '/').EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    ViewModel.YtDlpStatus =
                        "Install failed: the downloaded zip didn't contain bin/ffmpeg.exe.";
                    return;
                }
                if (File.Exists(ManagedFfmpegPath)) File.Delete(ManagedFfmpegPath);
                entry.ExtractToFile(ManagedFfmpegPath);
            }

            // Surface the install location in the path field. Same
            // reasoning as the yt-dlp install: makes it obvious where the
            // binary landed, and Uninstall clears it when it deletes.
            ViewModel.FfmpegPath = ManagedFfmpegPath;
            await _videoGrabber.SetFfmpegPathAsync(ManagedFfmpegPath);

            ViewModel.YtDlpStatus = $"Installed ffmpeg at {ManagedFfmpegPath}";
        }
        catch (Exception ex)
        {
            ViewModel.YtDlpStatus = $"ffmpeg install failed: {ex.Message}";
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* tmp leak ok */ }
            btn.Content = originalContent;
            btn.IsEnabled = true;
        }
    }

    private async void OnUninstallFfmpeg(object sender, RoutedEventArgs e)
    {
        var dest = ManagedFfmpegPath;
        if (!File.Exists(dest))
        {
            ViewModel.YtDlpStatus = "Nothing to uninstall — no SIDM-managed ffmpeg.exe was found.";
            return;
        }
        try
        {
            File.Delete(dest);
            if (string.Equals(ViewModel.FfmpegPath?.Trim(), dest, StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.FfmpegPath = string.Empty;
                await _videoGrabber.SetFfmpegPathAsync(null);
            }
            ViewModel.YtDlpStatus = BuildResolvedPathStatus();
        }
        catch (Exception ex)
        {
            ViewModel.YtDlpStatus = $"ffmpeg uninstall failed: {ex.Message}";
        }
    }

    private void OnRefreshFfmpeg(object sender, RoutedEventArgs e)
    {
        ViewModel.YtDlpStatus = BuildResolvedPathStatus();
    }

    private static string TryGetDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            if (File.Exists(path)) return Path.GetDirectoryName(path) ?? "";
            if (Directory.Exists(path)) return path;
        }
        catch { }
        return "";
    }

    private async Task LoadRulesAsync()
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduleRuleRepository>();
            var rules = await repo.GetAllAsync();
            ViewModel.Rules.Clear();
            foreach (var r in rules) ViewModel.Rules.Add(new ScheduleRuleRowViewModel(r));
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to load schedule rules: {ex.Message}";
        }
    }

    private async void OnAddRule(object sender, RoutedEventArgs e)
    {
        var rule = ShowRuleEditor(seed: null);
        if (rule is null) return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduleRuleRepository>();
        await repo.AddAsync(rule);
        await LoadRulesAsync();
    }

    private async void OnEditRule(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRule is null) return;

        // Hydrate the current row back into a domain model for the editor.
        var existing = ViewModel.SelectedRule.ToRule();
        var edited = ShowRuleEditor(seed: existing);
        if (edited is null) return;

        edited.Id = existing.Id;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduleRuleRepository>();
        await repo.UpdateAsync(edited);
        await LoadRulesAsync();
    }

    private async void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRule is null) return;
        var id = ViewModel.SelectedRule.Id;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduleRuleRepository>();
        await repo.RemoveAsync(id);
        await LoadRulesAsync();
    }

    private SIDM.Core.Models.ScheduleRule? ShowRuleEditor(SIDM.Core.Models.ScheduleRule? seed)
    {
        var dlg = new ScheduleRuleEditorDialog { Owner = this };
        if (seed is not null) dlg.LoadFrom(seed);
        return dlg.ShowDialog() == true ? dlg.ToRule() : null;
    }

    private async Task LoadCategoriesAsync()
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICategoryRepository>();
            var cats = await repo.GetAllAsync();
            ViewModel.Categories.Clear();
            foreach (var c in cats) ViewModel.Categories.Add(new CategoryRowViewModel(c));
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to load categories: {ex.Message}";
        }
    }

    private async void OnAddCategory(object sender, RoutedEventArgs e)
    {
        var cat = ShowCategoryEditor(seed: null);
        if (cat is null) return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICategoryRepository>();
        await repo.AddAsync(cat);
        await LoadCategoriesAsync();
    }

    private async void OnEditCategory(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCategory is null) return;
        var existing = ViewModel.SelectedCategory.ToCategory();
        var edited = ShowCategoryEditor(seed: existing);
        if (edited is null) return;

        edited.Id = existing.Id;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICategoryRepository>();
        await repo.UpdateAsync(edited);
        await LoadCategoriesAsync();
    }

    private async void OnDeleteCategory(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCategory is null) return;
        var id = ViewModel.SelectedCategory.Id;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICategoryRepository>();
        await repo.RemoveAsync(id);
        await LoadCategoriesAsync();
    }

    private SIDM.Core.Models.Category? ShowCategoryEditor(SIDM.Core.Models.Category? seed)
    {
        var dlg = new CategoryEditorDialog { Owner = this };
        if (seed is not null) dlg.LoadFrom(seed);
        return dlg.ShowDialog() == true ? dlg.ToCategory() : null;
    }
}
