using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.App.Services;
using SIDM.App.Views;
using SIDM.Core.Models;
using SIDM.Core.Persistence;
using SIDM.Ipc;
using SIDM.VideoGrabber;

namespace SIDM.App.ViewModels;

public partial class DownloadsViewModel : ObservableObject, IDownloadIntake
{
    /// <summary>Settings key prefix for the remembered folder of a given file extension.</summary>
    private const string FolderKeyPrefix = "download.folder.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DownloadEngine _engine;
    private readonly DownloadQueue _queue;
    private readonly UiProgressBus _bus;
    private readonly DownloadCreatedNotifier _notifier;
    private readonly ILogger<DownloadsViewModel> _logger;

    public ObservableCollection<DownloadRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private DownloadRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _statusBarText = "Ready";

    public DownloadsViewModel(
        IServiceScopeFactory scopeFactory,
        DownloadEngine engine,
        DownloadQueue queue,
        UiProgressBus bus,
        DownloadCreatedNotifier notifier,
        ILogger<DownloadsViewModel> logger)
    {
        _scopeFactory = scopeFactory;
        _engine = engine;
        _queue = queue;
        _bus = bus;
        _notifier = notifier;
        _logger = logger;
        _notifier.Created += OnDownloadCreated;
        _engine.Finished += OnEngineFinished;
    }

    private void OnEngineFinished(long downloadId, DownloadStatus status)
    {
        // Engine reports terminal state on a threadpool worker. Marshal to the
        // dispatcher and refresh the matching row so the UI sees the final
        // status without waiting for the next MonitorAsync tick.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(async () =>
        {
            var row = Rows.FirstOrDefault(r => r.Id == downloadId);
            if (row is null) return;
            await RefreshRowAsync(row);
            if (status == DownloadStatus.Completed) StatusBarText = $"Completed {row.FileName}";
            else if (status == DownloadStatus.Failed) StatusBarText = $"Failed: {row.FileName}";
            else if (status == DownloadStatus.Paused) StatusBarText = $"Paused {row.FileName}";
        });
    }

    private void OnDownloadCreated(long downloadId)
    {
        // Publishers may be on any thread — IPC dispatch runs on a threadpool worker.
        // Marshal to the WPF dispatcher before touching the ObservableCollection.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _ = AddRowFromDbAsync(downloadId);
        }
        else
        {
            dispatcher.BeginInvoke(() => _ = AddRowFromDbAsync(downloadId));
        }
    }

    private async Task AddRowFromDbAsync(long downloadId)
    {
        // Dedupe — UI-initiated downloads add the row themselves AND publish, so
        // this callback can race with that path. Skip if we already have it.
        if (Rows.Any(r => r.Id == downloadId)) return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
        var fresh = await repo.GetAsync(downloadId);
        if (fresh is null) return;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Rows.Any(r => r.Id == downloadId)) return;
            var row = new DownloadRowViewModel(fresh, _bus);
            Rows.Insert(0, row);
            StatusBarText = $"Captured {row.FileName}";
            _ = MonitorAsync(row);
        });
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
            var row = new DownloadRowViewModel(withSegs, _bus);
            Rows.Add(row);

            // Persisted Queued/Downloading rows are still in flight (the queue
            // and auto-resume service take care of restarting them) — keep the
            // status display fresh.
            if (row.Status == DownloadStatus.Queued || row.Status == DownloadStatus.Downloading)
            {
                _ = MonitorAsync(row);
            }
        }

        StatusBarText = $"{Rows.Count} download{(Rows.Count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Implements <see cref="IDownloadIntake"/>. Called by the IPC dispatcher on
    /// a worker thread when a browser extension forwards a download capture.
    /// Marshals to the UI thread, shows the popup, and on accept creates the
    /// row + starts the engine + opens the progress window.
    /// </summary>
    public async Task<DownloadIntakeResult> PromptAsync(DownloadRequestMessage request, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return new DownloadIntakeResult(null, "Canceled");
        }

        return await dispatcher.InvokeAsync(async () =>
        {
            var row = await ShowDialogAndStartAsync(request);
            return row is null
                ? new DownloadIntakeResult(null, "Canceled")
                : new DownloadIntakeResult(row.Id, row.Status.ToString());
        }).Task.Unwrap();
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        await ShowDialogAndStartAsync(seed: null);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        // The dialog is transient — DI builds a fresh instance with the live
        // DownloadQueue / BandwidthSettingsService so the user sees current
        // values, not whatever was set when the app started.
        var dlg = _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<Views.SettingsDialog>();
        dlg.Owner = Application.Current?.MainWindow;
        dlg.ShowDialog();
    }

    /// <summary>
    /// Core entry point for the IDM-style flow: show the seed-filled popup,
    /// remember the folder per extension, create the row, start the engine,
    /// and open the per-download progress window. Returns the row on accept,
    /// null on cancel.
    /// </summary>
    private async Task<DownloadRowViewModel?> ShowDialogAndStartAsync(DownloadRequestMessage? seed)
    {
        var owner = Application.Current?.MainWindow;

        var dialog = new AddDownloadDialog
        {
            Owner = owner,
        };

        if (seed is not null)
        {
            dialog.ViewModel.Url = seed.Url;
            dialog.ViewModel.FileName = !string.IsNullOrWhiteSpace(seed.FileName)
                ? seed.FileName!
                : AddDownloadViewModel.GuessFileNameFromUrl(seed.Url);
            dialog.ViewModel.ExpectedLength = seed.ExpectedLength;
            dialog.ViewModel.Mime = seed.Mime;

            // Pre-fill the folder: priority is
            //   1) explicitly-remembered folder for this extension (user's history),
            //   2) category default path for this extension,
            //   3) browser-suggested folder,
            //   4) the AddDownloadViewModel default (~\Downloads).
            var rememberedFolder = await TryGetRememberedFolderAsync(dialog.ViewModel.Extension);
            if (!string.IsNullOrWhiteSpace(rememberedFolder))
            {
                dialog.ViewModel.TargetFolder = rememberedFolder!;
            }
            else if (await TryGetCategoryFolderAsync(dialog.ViewModel.FileName) is { } catFolder)
            {
                dialog.ViewModel.TargetFolder = catFolder;
            }
            else if (!string.IsNullOrWhiteSpace(seed.SuggestedFolder))
            {
                dialog.ViewModel.TargetFolder = seed.SuggestedFolder!;
            }
        }
        else
        {
            // UI-button flow: pre-fill from clipboard URL? skipped for MVP.
            // Still respect any extension-remembered default folder as the
            // filename is typed.
        }

        if (dialog.ShowDialog() != true) return null;
        var vm = dialog.ViewModel;
        if (!vm.IsValid) return null;

        // Persist this folder as the remembered location for this extension.
        await RememberFolderAsync(vm.Extension, vm.TargetFolder);

        var fileName = string.IsNullOrWhiteSpace(vm.FileName)
            ? AddDownloadViewModel.GuessFileNameFromUrl(vm.Url)
            : vm.FileName;
        var targetPath = Path.Combine(vm.TargetFolder, fileName);

        var headers = seed is null ? null : MergeHeaders(seed.Headers, seed.Referer, seed.UserAgent);

        var isVideo = YouTubeUrlDetector.IsVideoUrl(vm.Url);

        var download = new Download
        {
            Url = vm.Url,
            FileName = fileName,
            TargetPath = targetPath,
            Status = DownloadStatus.Queued,
            SegmentCount = vm.Segments,
            CreatedUtc = DateTimeOffset.UtcNow,
            Mime = vm.Mime ?? seed?.Mime,
            TotalBytes = vm.ExpectedLength ?? seed?.ExpectedLength,
            CategoryId = await TryGetCategoryIdAsync(fileName),
            SourceKind = isVideo ? SourceKind.YouTube : SourceKind.Direct,
            HeadersJson = headers is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(headers) : null,
            CookiesJson = seed?.Cookies is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(seed.Cookies) : null,
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

        // The notifier dedupes against Rows so this is a no-op for this path.
        // Kept for symmetry with the legacy direct-IPC path.
        _notifier.Publish(id);

        // The queue decides whether to start immediately (slot available) or
        // park the id until an active download finishes.
        await _queue.EnqueueAsync(id);
        StatusBarText = _queue.RunningCount > _queue.MaxConcurrent
            ? $"Queued {download.FileName} (waiting for slot)"
            : $"Downloading {download.FileName}";

        _ = MonitorAsync(row);

        // Open the per-download progress window with chunks.
        ShowProgressDialog(row);

        return row;
    }

    private void ShowProgressDialog(DownloadRowViewModel row)
    {
        var owner = Application.Current?.MainWindow;
        var dlg = new DownloadProgressDialog(row, _engine)
        {
            Owner = owner,
        };
        dlg.Show();
    }

    [RelayCommand]
    private async Task PauseAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        await _queue.PauseAsync(row.Id);
        StatusBarText = $"Paused {row.FileName}";
        await RefreshRowAsync(row);
    }

    [RelayCommand]
    private async Task ResumeAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;

        // Flip DB status to Queued so MonitorAsync's loop predicate keeps
        // running even if the queue parks us behind active downloads. The
        // engine will move us to Downloading when our slot opens.
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var fresh = await repo.GetAsync(row.Id);
            if (fresh is not null && fresh.Status != DownloadStatus.Downloading)
            {
                fresh.Status = DownloadStatus.Queued;
                fresh.ErrorMessage = null;
                await repo.UpdateAsync(fresh);
                row.RefreshFrom(fresh);
            }
        }

        await _queue.EnqueueAsync(row.Id);
        StatusBarText = _queue.RunningCount >= _queue.MaxConcurrent
            ? $"Queued {row.FileName} (waiting for slot)"
            : $"Resuming {row.FileName}";
        _ = MonitorAsync(row);
    }

    [RelayCommand]
    private async Task RemoveAsync(DownloadRowViewModel? row)
    {
        if (row is null) return;
        _queue.Remove(row.Id);
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
        // Poll every second while the row is either queued (waiting for a slot)
        // or actively downloading. The engine.Finished event handles terminal
        // status; this loop just keeps the row VM in sync with the database
        // while progress is in flight. Replace with bus events later.
        while (row.Status == DownloadStatus.Queued
               || row.Status == DownloadStatus.Probing
               || row.Status == DownloadStatus.Downloading
               || _engine.IsActive(row.Id))
        {
            await Task.Delay(1000);
            await RefreshRowAsync(row);
        }
        await RefreshRowAsync(row);
    }

    private async Task<string?> TryGetRememberedFolderAsync(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            return await settings.GetAsync<string>(FolderKeyPrefix + extension);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read remembered folder for .{Ext}", extension);
            return null;
        }
    }

    /// <summary>
    /// Returns the default save folder for the category claiming this file's
    /// extension, or null if no category matches (or the category has no
    /// default path).
    /// </summary>
    private async Task<string?> TryGetCategoryFolderAsync(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICategoryRepository>();
            var all = await repo.GetAllAsync();
            var match = SIDM.Core.Scheduling.CategoryMatcher.Match(all, fileName);
            return string.IsNullOrWhiteSpace(match?.DefaultPath) ? null : match.DefaultPath;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve category folder for {FileName}", fileName);
            return null;
        }
    }

    /// <summary>
    /// Returns the matching category's id, or null. Called when persisting a
    /// new download so the row carries the category link for UI badges later.
    /// </summary>
    private async Task<long?> TryGetCategoryIdAsync(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICategoryRepository>();
            var all = await repo.GetAllAsync();
            return SIDM.Core.Scheduling.CategoryMatcher.Match(all, fileName)?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve category id for {FileName}", fileName);
            return null;
        }
    }

    private async Task RememberFolderAsync(string extension, string folder)
    {
        if (string.IsNullOrWhiteSpace(extension) || string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            await settings.SetAsync(FolderKeyPrefix + extension, folder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remember folder for .{Ext}", extension);
        }
    }

    private static Dictionary<string, string>? MergeHeaders(
        Dictionary<string, string>? extra, string? referer, string? userAgent)
    {
        var merged = extra is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(extra, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(referer) && !merged.ContainsKey("Referer"))
            merged["Referer"] = referer;
        if (!string.IsNullOrWhiteSpace(userAgent) && !merged.ContainsKey("User-Agent"))
            merged["User-Agent"] = userAgent;
        return merged.Count == 0 ? null : merged;
    }
}
