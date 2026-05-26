using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.App.Services;
using SIDM.Core.Models;
using Wpf.Ui.Controls;

namespace SIDM.App.ViewModels;

/// <summary>
/// One row in the downloads grid. Wraps a <see cref="Download"/> domain model
/// with UI-friendly derived properties and live progress updates from the bus.
/// </summary>
public partial class DownloadRowViewModel : ObservableObject, IDisposable
{
    private readonly Download _model;
    private readonly UiProgressBus _bus;
    private IDisposable? _subscription;
    private readonly Dictionary<int, SegmentProgressViewModel> _segmentByIdx = new();

    /// <summary>EMA window for transfer-rate smoothing. Larger = smoother, slower to react.</summary>
    private const double SpeedSmoothingAlpha = 0.3;

    private DateTimeOffset _lastSampleAt;
    private long _lastSampleBytes;
    private double _speedBytesPerSecond;

    // Anchor for the "avg speed since download started" calculation. Set
    // when Status first transitions to Downloading and reset on resume so
    // a paused-and-resumed download averages over the latest run, not
    // including the dead time in between.
    private DateTimeOffset? _runStartUtc;
    private long _bytesAtRunStart;

    public DownloadRowViewModel(Download model, UiProgressBus bus)
    {
        _model = model;
        _bus = bus;
        _bytesDownloaded = model.Segments.Sum(s => s.BytesDownloaded);
        _status = model.Status;
        _totalBytes = model.TotalBytes;
        _lastSampleAt = DateTimeOffset.UtcNow;
        _lastSampleBytes = _bytesDownloaded;

        SegmentRows = new ObservableCollection<SegmentProgressViewModel>();
        foreach (var s in model.Segments.OrderBy(s => s.Idx))
        {
            var seg = new SegmentProgressViewModel(s);
            _segmentByIdx[s.Idx] = seg;
            SegmentRows.Add(seg);
        }

        _subscription = bus.Subscribe(model.Id, OnSegmentProgress);
    }

    public long Id => _model.Id;
    public string Url => _model.Url;
    public string FileName => _model.FileName;
    public string TargetPath => _model.TargetPath;
    public int SegmentCount => _model.SegmentCount;
    public string? Mime => _model.Mime;
    public long? CategoryId => _model.CategoryId;

    /// <summary>File extension WITHOUT the leading dot, lowercased. Used by the
    /// icon mapper and the category filter. Empty string when no extension.</summary>
    public string ExtensionLower
    {
        get
        {
            var ext = System.IO.Path.GetExtension(_model.FileName);
            return string.IsNullOrEmpty(ext) ? string.Empty : ext.TrimStart('.').ToLowerInvariant();
        }
    }

    /// <summary>Wpf.Ui symbol — kept as a fallback if shell icon loading fails.</summary>
    public SymbolRegular FileIcon => FileTypeIcon.ForExtension(ExtensionLower);

    /// <summary>
    /// Real Windows shell icon for this file's extension — the same colored
    /// glyph Explorer shows. Lazily resolved through <see cref="FileIconProvider"/>
    /// and cached process-wide. Null if the shell call fails, in which case
    /// the XAML template falls back to the monochrome <see cref="FileIcon"/>.
    /// </summary>
    public System.Windows.Media.ImageSource? FileIconImage => FileIconProvider.GetIcon(ExtensionLower);

    public ObservableCollection<SegmentProgressViewModel> SegmentRows { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(TransferRateDisplay))]
    [NotifyPropertyChangedFor(nameof(TimeLeftDisplay))]
    [NotifyPropertyChangedFor(nameof(LastTryDisplay))]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private DownloadStatus _status;

    partial void OnStatusChanged(DownloadStatus value)
    {
        // Anchor the avg-speed window when the row enters Downloading;
        // clear it on every other state so a fresh resume measures from
        // the moment of resume, not from the original start.
        if (value == DownloadStatus.Downloading)
        {
            if (_runStartUtc is null)
            {
                _runStartUtc = DateTimeOffset.UtcNow;
                _bytesAtRunStart = BytesDownloaded;
            }
        }
        else
        {
            _runStartUtc = null;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    [NotifyPropertyChangedFor(nameof(TotalBytesDisplay))]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private long? _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    [NotifyPropertyChangedFor(nameof(DownloadedBytesDisplay))]
    [NotifyPropertyChangedFor(nameof(RemainingBytesDisplay))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    private long _bytesDownloaded;

    /// <summary>Just the downloaded byte count, formatted. Used by the progress
    /// dialog's stats row where the layout already labels it.</summary>
    public string DownloadedBytesDisplay => FormatBytes(BytesDownloaded);

    /// <summary>Remaining bytes formatted, or em-dash if the total is unknown
    /// (HLS playlists, no Content-Length, etc).</summary>
    public string RemainingBytesDisplay
    {
        get
        {
            if (TotalBytes is not { } total || total <= 0) return "—";
            var remaining = total - BytesDownloaded;
            return remaining > 0 ? FormatBytes(remaining) : "0 B";
        }
    }

    /// <summary>"X of N complete" — counts segments that have reported their
    /// whole byte range. Shown above the chunks list to give the user a
    /// glance-level summary without scrolling 8+ rows.</summary>
    public string SegmentSummary
    {
        get
        {
            if (SegmentRows.Count == 0) return string.Empty;
            var completed = SegmentRows.Count(s => s.Status == SegmentStatus.Completed);
            var active = SegmentRows.Count(s => s.Status == SegmentStatus.Active);
            return active > 0
                ? $"{completed} of {SegmentRows.Count} complete · {active} active"
                : $"{completed} of {SegmentRows.Count} complete";
        }
    }

    public double ProgressPercent => TotalBytes is { } total && total > 0
        ? Math.Min(100.0, (double)BytesDownloaded * 100.0 / total)
        : 0.0;

    /// <summary>
    /// Drives <see cref="System.Windows.Controls.ProgressBar.IsIndeterminate"/>.
    /// True in two cases:
    ///   1. The download is active but its total size is unknown — HLS/DASH
    ///      playlists before all segment sizes are known, HTTP responses
    ///      with no Content-Length, etc.
    ///   2. The download is actively Queued / Probing / Downloading but no
    ///      bytes have arrived yet. This is the "warm-up" window — yt-dlp
    ///      taking ~5 s to probe the URL, HLS parsing the playlist, etc. —
    ///      where the determinate bar would just sit at 0% and look stuck.
    /// Either case animates the bar so the user can see the download is
    /// still working.
    /// </summary>
    public bool IsIndeterminate
    {
        get
        {
            var active = Status == DownloadStatus.Downloading
                || Status == DownloadStatus.Probing
                || Status == DownloadStatus.Queued;
            if (!active) return false;

            var sizeKnown = TotalBytes is { } t && t > 0;
            if (!sizeKnown) return true;

            // Warm-up: size is known (picker provided it, or a probe came back)
            // but no bytes have transferred yet. Show the spinner so the user
            // gets a "working…" cue rather than a stuck 0%.
            return BytesDownloaded == 0;
        }
    }

    public string ProgressDisplay
    {
        get
        {
            var pct = ProgressPercent;
            return TotalBytes is { } total && total > 0
                ? string.Format(CultureInfo.CurrentCulture, "{0:F1}%  ({1} / {2})", pct, FormatBytes(BytesDownloaded), FormatBytes(total))
                : FormatBytes(BytesDownloaded);
        }
    }

    public string StatusDisplay => Status switch
    {
        DownloadStatus.Completed => "Complete",
        _ => Status.ToString(),
    };

    public string TotalBytesDisplay => TotalBytes is { } t ? FormatBytes(t) : "Unknown";

    /// <summary>
    /// IDM-style "Size" column that auto-updates while a download is in flight.
    /// - Active (Downloading/Probing/Paused): "12.3 MiB / 21.4 MiB" if we know the
    ///   total, otherwise just the live downloaded count.
    /// - Completed: final size from the model (capped to the on-disk file size
    ///   thanks to <c>CaptureFinalFileSizeIfMissing</c>).
    /// - Other terminal states: total if known, em-dash otherwise.
    /// </summary>
    public string SizeDisplay
    {
        get
        {
            switch (Status)
            {
                case DownloadStatus.Downloading:
                case DownloadStatus.Probing:
                case DownloadStatus.Paused:
                    if (TotalBytes is { } activeTotal && activeTotal > 0)
                        return $"{FormatBytes(BytesDownloaded)} / {FormatBytes(activeTotal)}";
                    return FormatBytes(BytesDownloaded);

                case DownloadStatus.Completed:
                    return TotalBytes is { } completedTotal && completedTotal > 0
                        ? FormatBytes(completedTotal)
                        : FormatBytes(BytesDownloaded);

                default:
                    return TotalBytes is { } total && total > 0 ? FormatBytes(total) : "—";
            }
        }
    }

    /// <summary>Last sampled transfer rate. Updated by <see cref="ApplySegmentProgress"/>.</summary>
    public double SpeedBytesPerSecond => _speedBytesPerSecond;

    /// <summary>
    /// Long-run bytes/sec averaged across the current Downloading run.
    /// Less reactive than <see cref="SpeedBytesPerSecond"/> but a better
    /// number for "what should I plan around" — small short-lived stalls
    /// don't drag it down the way they do the EMA.
    /// </summary>
    public double AverageSpeedBytesPerSecond
    {
        get
        {
            if (_runStartUtc is not { } start) return 0;
            var elapsed = (DateTimeOffset.UtcNow - start).TotalSeconds;
            if (elapsed < 0.1) return 0;
            var bytes = BytesDownloaded - _bytesAtRunStart;
            return bytes > 0 ? bytes / elapsed : 0;
        }
    }

    /// <summary>
    /// Whether the Speed cell shows the long-run average instead of the
    /// instantaneous (EMA) speed. Toggled by clicking the cell.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferRateDisplay))]
    [NotifyPropertyChangedFor(nameof(SpeedModeLabel))]
    [NotifyPropertyChangedFor(nameof(SpeedModeToggleLabel))]
    private bool _showAverageSpeed;

    /// <summary>
    /// "1.2 MiB/s" while active; em-dash while paused/completed/failed/queued.
    /// Reflects <see cref="ShowAverageSpeed"/> — flips between the EMA and
    /// the long-run average.
    /// </summary>
    public string TransferRateDisplay
    {
        get
        {
            if (Status != DownloadStatus.Downloading) return "—";
            var bps = ShowAverageSpeed ? AverageSpeedBytesPerSecond : _speedBytesPerSecond;
            return bps > 1 ? FormatBytes((long)bps) + "/s" : "—";
        }
    }

    /// <summary>Label that goes above the Speed value in the dialog —
    /// changes with the toggle so the user can tell which mode is on.</summary>
    public string SpeedModeLabel => ShowAverageSpeed ? "Avg speed" : "Speed";

    /// <summary>Hint text for the toggle — clicking flips to the other mode.</summary>
    public string SpeedModeToggleLabel => ShowAverageSpeed ? "Show current" : "Show average";

    /// <summary>
    /// Remaining time estimate while active and size is known; em-dash otherwise.
    /// Formatted as "45s", "5m 23s", or "1h 23m" — colon notation like 01:23
    /// reads as either 1 m 23 s or 1 h 23 m depending on context, which was
    /// the bug behind the "time not correct" report.
    /// </summary>
    public string TimeLeftDisplay
    {
        get
        {
            if (Status != DownloadStatus.Downloading) return "—";
            if (TotalBytes is not { } total || total <= 0) return "—";
            // Always base ETA on the instantaneous speed — it's what the
            // remaining bytes are about to be transferred at. Using the
            // long-run average would make ETA stale after a speed change.
            if (_speedBytesPerSecond < 1) return "—";
            var remaining = total - BytesDownloaded;
            if (remaining <= 0) return "—";
            var seconds = remaining / _speedBytesPerSecond;
            if (seconds < 0 || double.IsInfinity(seconds) || double.IsNaN(seconds)) return "—";
            return FormatDuration(TimeSpan.FromSeconds(seconds));
        }
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return "<1s";
        if (ts.TotalMinutes < 1) return $"{ts.Seconds}s";
        if (ts.TotalHours < 1) return $"{ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalDays < 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{(int)ts.TotalDays}d {ts.Hours}h";
    }

    /// <summary>
    /// Timestamp of the last attempt — completion time when finished, otherwise
    /// the most recently-known activity time (started or created). IDM "Last Try" column.
    /// </summary>
    public string LastTryDisplay
    {
        get
        {
            var ts = _model.CompletedUtc ?? _model.StartedUtc ?? _model.CreatedUtc;
            return ts == default ? "—" : ts.LocalDateTime.ToString("MMM dd HH:mm", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>
    /// One-liner blurb that fits the IDM "Description" column — MIME if we have
    /// it, else the URL's host. Goal: give the user a hint about what this is
    /// without showing the full URL (which we used to dedicate a column to).
    /// </summary>
    public string DescriptionDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Mime)) return Mime!;
            if (Uri.TryCreate(Url, UriKind.Absolute, out var u)) return u.Host;
            return string.Empty;
        }
    }

    private void OnSegmentProgress(int segmentIndex, long bytes)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplySegmentProgress(segmentIndex, bytes);
        }
        else
        {
            dispatcher.BeginInvoke(() => ApplySegmentProgress(segmentIndex, bytes));
        }
    }

    private void ApplySegmentProgress(int segmentIndex, long bytes)
    {
        if (_segmentByIdx.TryGetValue(segmentIndex, out var seg))
        {
            seg.UpdateBytes(bytes);
        }
        BytesDownloaded = _segmentByIdx.Values.Sum(s => s.BytesDownloaded);
        UpdateTransferRate();
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressDisplay));
        OnPropertyChanged(nameof(TransferRateDisplay));
        OnPropertyChanged(nameof(TimeLeftDisplay));
        OnPropertyChanged(nameof(SegmentSummary));
    }

    /// <summary>
    /// EMA-smoothed bytes-per-second over the gap since the last progress
    /// sample. The progress bus fires per-segment so samples can be noisy —
    /// the EMA tames that without lagging too far behind reality.
    /// </summary>
    private void UpdateTransferRate()
    {
        var now = DateTimeOffset.UtcNow;
        var dtSeconds = (now - _lastSampleAt).TotalSeconds;
        // Ignore samples that are too close together (sub-100ms) — math gets
        // jumpy when dt is tiny and bytes haven't really moved.
        if (dtSeconds < 0.1) return;
        var dBytes = BytesDownloaded - _lastSampleBytes;
        if (dBytes < 0) dBytes = 0; // shouldn't happen, but be defensive
        var instant = dBytes / dtSeconds;
        _speedBytesPerSecond = _speedBytesPerSecond <= 0
            ? instant
            : (SpeedSmoothingAlpha * instant) + ((1 - SpeedSmoothingAlpha) * _speedBytesPerSecond);
        _lastSampleAt = now;
        _lastSampleBytes = BytesDownloaded;
    }

    /// <summary>Refreshes from the latest persisted model (call after status changes).</summary>
    public void RefreshFrom(Download fresh)
    {
        // Mirror the freshly-persisted timestamps + category into our own
        // model copy so derived displays (LastTryDisplay, CategoryDisplay,
        // DescriptionDisplay) reflect the latest state.
        _model.StartedUtc = fresh.StartedUtc;
        _model.CompletedUtc = fresh.CompletedUtc;
        _model.CategoryId = fresh.CategoryId;
        _model.Mime = fresh.Mime;
        _model.ErrorMessage = fresh.ErrorMessage;
        _model.FileName = fresh.FileName;
        _model.TargetPath = fresh.TargetPath;

        Status = fresh.Status;
        TotalBytes = fresh.TotalBytes;
        if (fresh.Segments.Count > 0)
        {
            // Rebuild on shape change (split happens once on first probe).
            var freshOrdered = fresh.Segments.OrderBy(s => s.Idx).ToList();
            var dispatcher = Application.Current?.Dispatcher;
            void apply()
            {
                if (freshOrdered.Count != SegmentRows.Count
                    || freshOrdered.Any(s => !_segmentByIdx.ContainsKey(s.Idx)))
                {
                    SegmentRows.Clear();
                    _segmentByIdx.Clear();
                    foreach (var s in freshOrdered)
                    {
                        var vm = new SegmentProgressViewModel(s);
                        _segmentByIdx[s.Idx] = vm;
                        SegmentRows.Add(vm);
                    }
                }
                else
                {
                    foreach (var s in freshOrdered)
                    {
                        var vm = _segmentByIdx[s.Idx];
                        vm.UpdateBytes(s.BytesDownloaded);
                        vm.UpdateStatus(s.Status);
                    }
                }
                BytesDownloaded = _segmentByIdx.Values.Sum(s => s.BytesDownloaded);
                // Drive the speed/ETA recompute here too — the bus path
                // (ApplySegmentProgress) is one source of samples, but the
                // periodic DB polling in MonitorAsync is the other, and a
                // download whose bus events are missed (extension HLS,
                // resumed-from-db state, etc.) would otherwise show "—" for
                // Speed and Time left forever even though bytes are moving.
                UpdateTransferRate();
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressDisplay));
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(TotalBytesDisplay));
                OnPropertyChanged(nameof(LastTryDisplay));
                OnPropertyChanged(nameof(SegmentSummary));
                OnPropertyChanged(nameof(TransferRateDisplay));
                OnPropertyChanged(nameof(TimeLeftDisplay));
            }
            if (dispatcher is null || dispatcher.CheckAccess()) apply();
            else dispatcher.BeginInvoke(apply);
        }
        else
        {
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressDisplay));
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(TotalBytesDisplay));
            OnPropertyChanged(nameof(LastTryDisplay));
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.CurrentCulture, "{0:F1} {1}", value, units[unit]);
    }
}
