using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.App.Services;
using SIDM.Core.Models;
using SIDM.Core.Persistence;

namespace SIDM.App.ViewModels;

public partial class DownloadsViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DownloadEngine _engine;
    private readonly UiProgressBus _bus;
    private readonly ILogger<DownloadsViewModel> _logger;

    public ObservableCollection<DownloadRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private DownloadRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _statusBarText = "Ready";

    public DownloadsViewModel(
        IServiceScopeFactory scopeFactory,
        DownloadEngine engine,
        UiProgressBus bus,
        ILogger<DownloadsViewModel> logger)
    {
        _scopeFactory = scopeFactory;
        _engine = engine;
        _bus = bus;
        _logger = logger;
    }

    public async Task LoadAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
        var all = await repo.GetAllAsync();

        // Eager-load segments per row (we need their byte counts for progress).
        foreach (var row in Rows) row.Dispose();
        Rows.Clear();

        foreach (var d in all)
        {
            var withSegs = await repo.GetAsync(d.Id) ?? d;
            Rows.Add(new DownloadRowViewModel(withSegs, _bus));
        }

        StatusBarText = $"{Rows.Count} download{(Rows.Count == 1 ? "" : "s")}";
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var dialog = new Views.AddDownloadDialog
        {
            Owner = Application.Current?.MainWindow,
        };

        if (dialog.ShowDialog() != true) return;
        var vm = dialog.ViewModel;
        if (!vm.IsValid) return;

        var download = new Download
        {
            Url = vm.Url,
            FileName = string.IsNullOrWhiteSpace(vm.FileName) ? AddDownloadViewModel.GuessFileNameFromUrl(vm.Url) : vm.FileName,
            TargetPath = vm.TargetPath,
            Status = DownloadStatus.Queued,
            SegmentCount = vm.Segments,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        long id;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            id = await repo.AddAsync(download);
            download = (await repo.GetAsync(id))!;
        }

        var row = new DownloadRowViewModel(download, _bus);
        Rows.Insert(0, row);
        StatusBarText = $"Queued {download.FileName}";

        await _engine.StartAsync(id);
        StatusBarText = $"Downloading {download.FileName}";

        // Periodically refresh row from DB to pick up status changes (cheap MVP — a
        // proper bus event would replace this in Phase 1.K+).
        _ = MonitorAsync(row);
    }

    [RelayCommand]
    private async Task PauseAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        if (_engine.Pause(row.Id))
        {
            StatusBarText = $"Paused {row.FileName}";
            await RefreshRowAsync(row);
        }
    }

    [RelayCommand]
    private async Task ResumeAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        await _engine.StartAsync(row.Id);
        StatusBarText = $"Resuming {row.FileName}";
        _ = MonitorAsync(row);
    }

    [RelayCommand]
    private async Task RemoveAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        _engine.Pause(row.Id);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
        await repo.RemoveAsync(row.Id);
        Rows.Remove(row);
        row.Dispose();
        StatusBarText = $"Removed {row.FileName}";
    }

    private async Task RefreshRowAsync(DownloadRowViewModel row)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
        var fresh = await repo.GetAsync(row.Id);
        if (fresh is not null) row.RefreshFrom(fresh);
    }

    private async Task MonitorAsync(DownloadRowViewModel row)
    {
        // Poll every second while the download is active. Replace with bus events later.
        while (_engine.IsActive(row.Id))
        {
            await Task.Delay(1000);
            await RefreshRowAsync(row);
        }
        await RefreshRowAsync(row);
        if (row.Status == DownloadStatus.Completed)
        {
            StatusBarText = $"Completed {row.FileName}";
        }
    }
}
