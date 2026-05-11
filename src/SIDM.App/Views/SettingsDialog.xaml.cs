using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SIDM.App.Services;
using SIDM.App.ViewModels;
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
    private readonly IServiceScopeFactory _scopeFactory;

    public SettingsViewModel ViewModel { get; } = new();

    public SettingsDialog(
        DownloadQueue queue,
        BandwidthSettingsService bandwidth,
        SchedulerService scheduler,
        VideoGrabberSettingsService videoGrabber,
        IServiceScopeFactory scopeFactory)
    {
        InitializeComponent();
        _queue = queue;
        _bandwidth = bandwidth;
        _scheduler = scheduler;
        _videoGrabber = videoGrabber;
        _scopeFactory = scopeFactory;
        DataContext = ViewModel;

        ViewModel.MaxConcurrent = _queue.MaxConcurrent;
        ViewModel.SetFromBytes(_bandwidth.CurrentBytesPerSecond);
        ViewModel.YtDlpPath = _videoGrabber.YtDlpPathOverride;
        ViewModel.FfmpegPath = _videoGrabber.FfmpegPathOverride;
        ViewModel.YtDlpStatus = BuildResolvedPathStatus();

        _ = LoadRulesAsync();
        _ = LoadCategoriesAsync();
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

        DialogResult = true;
        Close();
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
