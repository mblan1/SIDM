using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.App.Services;
using SIDM.App.Views;
using SIDM.Core.Abstractions;
using SIDM.Core.Http;
using SIDM.Core.Models;
using SIDM.Core.Persistence;
using SIDM.Ipc;
using SIDM.VideoGrabber;
using SIDM.VideoGrabber.Dash;
using SIDM.VideoGrabber.Hls;

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

    /// <summary>
    /// Mirrors the multi-selection from the downloads DataGrid. Kept in sync
    /// by MainWindow's SelectionChanged handler — WPF's DataGrid.SelectedItems
    /// isn't directly bindable, so we copy on every change. Bulk commands
    /// (PauseSelected/ResumeSelected/RemoveSelected) act on this collection.
    /// </summary>
    public ObservableCollection<DownloadRowViewModel> SelectedRows { get; } = new();

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

        // The toolbar's Pause/Resume/Remove buttons stay disabled until the
        // user has at least one row selected. When the selection changes,
        // re-query CanExecute on the bulk commands so WPF refreshes button
        // IsEnabled state. (DataGrid.SelectedItems isn't bindable so we
        // mirror it into SelectedRows from the code-behind handler.)
        SelectedRows.CollectionChanged += (_, _) => NotifySelectionCommandsChanged();
    }

    /// <summary>True when at least one row is selected — gates the bulk toolbar commands.</summary>
    private bool CanActOnSelection() => SelectedRows.Count > 0 || SelectedRow is not null;

    private void NotifySelectionCommandsChanged()
    {
        PauseSelectedCommand.NotifyCanExecuteChanged();
        ResumeSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRowChanged(DownloadRowViewModel? value) => NotifySelectionCommandsChanged();

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
            // Browser-capture path: minimize the main window before showing
            // the popup. Without this, MainWindow stays on top of the dialog
            // (especially right after launch, when MainWindow.Show() runs a
            // beat before the IPC message arrives — the dialog appears for a
            // moment then disappears behind the main window). The window
            // stays in the taskbar so the user can click back to it.
            var mw = Application.Current?.MainWindow;
            if (mw is not null && mw.IsVisible && mw.WindowState != WindowState.Minimized)
            {
                mw.WindowState = WindowState.Minimized;
            }

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
        var dialog = new AddDownloadDialog();

        if (seed is not null)
        {
            // IPC path (browser extension): standalone popup — do NOT bring the
            // whole main window forward, just show the dialog over whatever the
            // user is doing. Toolbar path keeps the centered-on-owner behavior.
            dialog.ConfigureAsStandalonePopup();
        }
        else
        {
            dialog.Owner = Application.Current?.MainWindow;
        }

        if (seed is not null)
        {
            dialog.ViewModel.Url = seed.Url;

            // Pick the best filename we can. Priority:
            //   1) what the extension captured (seed.FileName), if reliable,
            //   2) the URL's last path segment, if reliable,
            //   3) a filename embedded in the URL query — presigned CDN links
            //      (S3 response-content-disposition, Hugging Face Xet) whose
            //      path is a content hash but whose query carries the real name.
            //      Network-free, so it's tried before the probe.
            //   4) a HEAD/ranged-GET probe of Content-Disposition (recovers the
            //      real name for CDN-redirect URLs like GitHub release assets,
            //      whose path ends in a GUID — the originally reported bug).
            var candidate = !string.IsNullOrWhiteSpace(seed.FileName)
                ? seed.FileName!
                : AddDownloadViewModel.GuessFileNameFromUrl(seed.Url);
            var recovered = false;
            if (AddDownloadViewModel.LooksUnreliableFileName(candidate))
            {
                var fromQuery = FileNameResolver.FromUrlQuery(seed.Url);
                if (!string.IsNullOrWhiteSpace(fromQuery))
                {
                    candidate = fromQuery!;
                    recovered = true;
                }
                else
                {
                    var better = await TryRecoverFileNameAsync(seed);
                    if (!string.IsNullOrWhiteSpace(better))
                    {
                        candidate = better!;
                        recovered = true;
                    }
                }
            }
            dialog.ViewModel.FileName = candidate;
            dialog.ViewModel.ExpectedLength = seed.ExpectedLength;
            // Drop the extension-supplied MIME when we had to recover the
            // name — Chrome guesses "image/*" for an extension-less GUID,
            // which would otherwise show as a bogus "Type". The dialog
            // derives the type from the (now correct) file extension.
            dialog.ViewModel.Mime = recovered ? null : seed.Mime;

            // Surface the yt-dlp format the user picked in the browser
            // overlay, if any. The picker passes both the format selector
            // (sent to yt-dlp) and a pretty label (shown read-only in the
            // dialog) so the user sees exactly what they're about to fetch.
            // Use IsNullOrWhiteSpace (not ??) so an older extension that
            // sends an empty-string label still gets a useful fallback.
            if (!string.IsNullOrEmpty(seed.YtDlpFormat))
            {
                dialog.ViewModel.YtDlpFormat = seed.YtDlpFormat;
                var label = string.IsNullOrWhiteSpace(seed.YtDlpFormatLabel)
                    ? seed.YtDlpFormat
                    : seed.YtDlpFormatLabel;
                dialog.ViewModel.YtDlpFormatLabel = label;
            }

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

        // Collision check — the user picked a name that's already taken by
        // either a file on disk, an in-flight .sidmpart, or an existing
        // SIDM row pointing at this exact path. Prompt before persisting.
        var existingRow = Rows.FirstOrDefault(r =>
            string.Equals(r.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase));
        var partPath = targetPath + ".sidmpart";
        var hasCollision = File.Exists(targetPath)
                           || File.Exists(partPath)
                           || existingRow is not null;
        if (hasCollision)
        {
            var suggested = FindAvailableTargetPath(targetPath);
            var prompt = new Views.FileExistsDialog(
                fileName,
                targetPath,
                Path.GetFileName(suggested))
            {
                Owner = Application.Current?.MainWindow,
            };
            prompt.ShowDialog();

            switch (prompt.Choice)
            {
                case Views.FileExistsChoice.Cancel:
                    return null;

                case Views.FileExistsChoice.SaveAsNew:
                    targetPath = suggested;
                    fileName = Path.GetFileName(targetPath);
                    break;

                case Views.FileExistsChoice.Replace:
                    // Existing SIDM row first — that handles its own engine
                    // pause + .sidmpart delete + DB cleanup.
                    if (existingRow is not null)
                    {
                        await RemoveRowAsync(existingRow, deleteFile: true);
                    }
                    // Then any leftover bare files (could be a row we
                    // already removed, or something we never tracked).
                    await TryDeleteWithRetryAsync(targetPath);
                    await TryDeleteWithRetryAsync(partPath);
                    break;
            }
        }

        var headers = seed is null ? null : MergeHeaders(seed.Headers, seed.Referer, seed.UserAgent);

        // Streaming-manifest URLs always win over yt-dlp detection: a URL
        // ending in .m3u8 / .mpd is unambiguous even if the host (e.g.
        // vimeo.com) would otherwise be matched by the yt-dlp router.
        var sourceKind =
            DashUrlDetector.IsDashUrl(vm.Url) ? SourceKind.Dash
            : HlsUrlDetector.IsHlsUrl(vm.Url) ? SourceKind.Hls
            : YouTubeUrlDetector.IsVideoUrl(vm.Url) ? SourceKind.YouTube
            : SourceKind.Direct;

        // If the user picked a format in the browser overlay, force the
        // YouTube/yt-dlp route — the format selector is only meaningful
        // for that path. This also covers any sites where YouTubeUrlDetector
        // doesn't fire (e.g. picker was triggered manually) but the user
        // explicitly asked for yt-dlp by picking a format.
        if (!string.IsNullOrEmpty(vm.YtDlpFormat))
        {
            sourceKind = SourceKind.YouTube;
        }

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
            SourceKind = sourceKind,
            HeadersJson = headers is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(headers) : null,
            CookiesJson = seed?.Cookies is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(seed.Cookies) : null,
            SelectedYtDlpFormat = string.IsNullOrEmpty(vm.YtDlpFormat) ? null : vm.YtDlpFormat,
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
        // On the browser-capture path, PromptAsync minimizes the main window so
        // the popup isn't hidden behind it. A window owned by a *minimized*
        // window doesn't render in WPF — which is why the progress window
        // failed to appear for browser downloads. Only use the main window as
        // owner when it's actually a usable owner (visible + not minimized);
        // otherwise show standalone, mirroring AddDownloadDialog's popup mode.
        var mw = Application.Current?.MainWindow;
        var ownerUsable = mw is not null && mw.IsVisible && mw.WindowState != WindowState.Minimized;

        var dlg = new DownloadProgressDialog(
            row,
            _engine,
            onCancelConfirmed: () => RemoveRowAsync(row, deleteFile: true))
        {
            Owner = ownerUsable ? mw : null,
            ShowInTaskbar = !ownerUsable,
            WindowStartupLocation = ownerUsable
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };
        dlg.Show();
        dlg.Activate();

        if (!ownerUsable)
        {
            // Bring it above the browser that triggered the download, then drop
            // Topmost so it doesn't pin over everything afterwards.
            dlg.Topmost = true;
            dlg.Topmost = false;
        }
    }

    /// <summary>
    /// Public entry-point for "cancel + delete" used by the per-download
    /// progress dialog. Internally just <see cref="RemoveRowAsync"/> with
    /// the delete-file flag on — exposed so external callers don't have to
    /// reach into the private removal helper.
    /// </summary>
    public Task CancelAndDeleteAsync(DownloadRowViewModel row) =>
        RemoveRowAsync(row, deleteFile: true);

    /// <summary>
    /// Picks the set of rows to act on for a toolbar bulk command. Preference:
    /// multi-select if it has more than the single focus item, else fall back
    /// to the explicit row parameter (e.g. context-menu invocation) or the
    /// last-selected row. Returns empty when nothing's selected.
    /// </summary>
    private IReadOnlyList<DownloadRowViewModel> ResolveTargets(DownloadRowViewModel? fallback)
    {
        if (SelectedRows.Count > 0) return SelectedRows.ToList();
        if (fallback is not null) return new[] { fallback };
        return Array.Empty<DownloadRowViewModel>();
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task PauseSelectedAsync(DownloadRowViewModel? fallbackRow)
    {
        foreach (var row in ResolveTargets(fallbackRow))
        {
            await PauseAsync(row);
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task ResumeSelectedAsync(DownloadRowViewModel? fallbackRow)
    {
        foreach (var row in ResolveTargets(fallbackRow))
        {
            await ResumeAsync(row);
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task RemoveSelectedAsync(DownloadRowViewModel? fallbackRow)
    {
        // Snapshot — RemoveAsync mutates Rows and SelectedRows, so iterating
        // the live collection would skip or revisit entries.
        var targets = ResolveTargets(fallbackRow).ToList();
        if (targets.Count == 0) return;

        // Always confirm — Remove from this app is a one-click action and we
        // need an explicit signal before nuking the on-disk file.
        var confirm = new Views.RemoveConfirmDialog(targets.Count)
        {
            Owner = Application.Current?.MainWindow,
        };
        if (confirm.ShowDialog() != true) return;
        var deleteFiles = confirm.DeleteFiles;

        foreach (var row in targets)
        {
            await RemoveRowAsync(row, deleteFiles);
        }
    }

    /// <summary>
    /// Core single-row removal. Pulls the row out of the queue, deletes the DB
    /// record, drops the live VM entry, and (optionally) wipes the file from
    /// disk. The file-delete is best-effort — failures are logged but don't
    /// block the rest of the removal.
    /// </summary>
    private async Task RemoveRowAsync(DownloadRowViewModel row, bool deleteFile)
    {
        var path = row.TargetPath;
        var fileName = row.FileName;

        // For an in-flight cancel, the engine's writer still owns the
        // .sidmpart handle for a few hundred ms after Pause returns. Wait
        // for the engine to drop the active CancellationTokenSource so the
        // file handles release before we try to delete.
        if (deleteFile && _engine.IsActive(row.Id))
        {
            _engine.Pause(row.Id);
            await WaitForEngineToReleaseAsync(row.Id, timeoutMs: 3000);
        }

        _queue.Remove(row.Id);
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            await repo.RemoveAsync(row.Id);
        }
        Rows.Remove(row);
        row.Dispose();

        if (deleteFile && !string.IsNullOrWhiteSpace(path))
        {
            // Two candidates: the final TargetPath (only exists after a
            // successful finalize) and the .sidmpart file the engine
            // actively writes to. A canceled download leaves only the
            // .sidmpart; a completed one leaves only the final file.
            await TryDeleteWithRetryAsync(path);
            await TryDeleteWithRetryAsync(path + ".sidmpart");

            // For yt-dlp / HLS / DASH downloads, the engine never wrote to
            // row.TargetPath — yt-dlp uses its own %(title)s template, so
            // the only on-disk leftovers are .part / .ytdl scratch files
            // inside the output folder. Sweep them, scoped to "modified
            // recently" so we don't touch the user's other downloads.
            await SweepYtDlpPartialsAsync(path);
        }

        StatusBarText = deleteFile ? $"Removed and deleted {fileName}" : $"Removed {fileName}";
    }

    /// <summary>
    /// Removes <c>*.part</c> and <c>*.ytdl</c> files in the row's output
    /// directory that were modified in the last hour. yt-dlp uses these
    /// as scratch during in-progress downloads (one per video/audio
    /// stream pre-merge, plus a metadata sidecar). Time-window scoping
    /// keeps the sweep from clobbering an unrelated download that
    /// happens to be in the same folder.
    /// </summary>
    private async Task SweepYtDlpPartialsAsync(string targetPath)
    {
        var outputDir = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir)) return;

        var cutoff = DateTime.UtcNow.AddHours(-1);
        try
        {
            foreach (var pattern in new[] { "*.part", "*.ytdl" })
            {
                foreach (var file in Directory.GetFiles(outputDir, pattern))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTimeUtc < cutoff) continue;
                        await TryDeleteWithRetryAsync(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Couldn't inspect partial {File}", file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sweep failed for {Dir}", outputDir);
        }
    }

    /// <summary>
    /// Mirrors the rename SparseFileWriter does at finalize time —
    /// "Foo.exe" → "Foo (1).exe" → "Foo (2).exe", picking the first slot
    /// that doesn't collide on disk OR with an in-memory Rows entry. Used
    /// by the FileExistsDialog to suggest a non-colliding name when the
    /// user picks "Save as new".
    /// </summary>
    private string FindAvailableTargetPath(string targetPath)
    {
        if (!Collides(targetPath)) return targetPath;

        var dir = Path.GetDirectoryName(targetPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(targetPath);
        var ext = Path.GetExtension(targetPath); // includes the dot, or empty

        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Collides(candidate)) return candidate;
        }
        // Pathological — fall back to the original name and let the engine
        // ResolveCollision pick something at finalize time.
        return targetPath;
    }

    /// <summary>True if either the file (or its .sidmpart) is on disk, or
    /// a live SIDM row already points at this TargetPath.</summary>
    private bool Collides(string path) =>
        File.Exists(path)
        || File.Exists(path + ".sidmpart")
        || Rows.Any(r => string.Equals(r.TargetPath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Polls <see cref="DownloadEngine.IsActive"/> until it returns false or
    /// the timeout elapses. Used by cancel-and-delete so we don't race the
    /// engine's writer.DisposeAsync — File.Delete fails on a locked handle.
    /// </summary>
    private async Task WaitForEngineToReleaseAsync(long downloadId, int timeoutMs)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (_engine.IsActive(downloadId) && Environment.TickCount < deadline)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Best-effort delete with short retries. The engine's worker may still
    /// be flushing buffers when we get here; retrying for a second covers
    /// the common Windows "file in use" race without making the user see
    /// a flicker.
    /// </summary>
    private async Task TryDeleteWithRetryAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete {Path}", path);
                return;
            }
        }
        _logger.LogWarning("Giving up on deleting {Path} after retries", path);
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

    /// <summary>
    /// HEAD-probes the URL to recover the real filename from Content-Disposition
    /// when the captured/URL-derived name is unreliable (GUID / no extension).
    /// Bounded to 4 s so a slow server can't hang the dialog open. Returns null
    /// on timeout/failure so the caller keeps its existing guess.
    /// </summary>
    private async Task<string?> TryRecoverFileNameAsync(DownloadRequestMessage seed)
    {
        if (!Uri.TryCreate(seed.Url, UriKind.Absolute, out var uri)) return null;

        var headers = seed.Referer is { Length: > 0 } referer
            ? new Dictionary<string, string> { ["Referer"] = referer }
            : null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await using var scope = _scopeFactory.CreateAsyncScope();
            var probe = scope.ServiceProvider.GetRequiredService<IRangeProbe>();
            return await probe.ProbeFileNameAsync(uri, headers, seed.Cookies, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Filename recovery probe failed for {Url}", seed.Url);
            return null;
        }
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
