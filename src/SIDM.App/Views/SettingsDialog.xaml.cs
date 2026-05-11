using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SIDM.App.Services;
using SIDM.App.ViewModels;
using SIDM.Core.Persistence;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

public partial class SettingsDialog : FluentWindow
{
    private readonly DownloadQueue _queue;
    private readonly BandwidthSettingsService _bandwidth;
    private readonly SchedulerService _scheduler;
    private readonly IServiceScopeFactory _scopeFactory;

    public SettingsViewModel ViewModel { get; } = new();

    public SettingsDialog(
        DownloadQueue queue,
        BandwidthSettingsService bandwidth,
        SchedulerService scheduler,
        IServiceScopeFactory scopeFactory)
    {
        InitializeComponent();
        _queue = queue;
        _bandwidth = bandwidth;
        _scheduler = scheduler;
        _scopeFactory = scopeFactory;
        DataContext = ViewModel;

        ViewModel.MaxConcurrent = _queue.MaxConcurrent;
        ViewModel.SetFromBytes(_bandwidth.CurrentBytesPerSecond);

        _ = LoadRulesAsync();
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

        DialogResult = true;
        Close();
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
}
