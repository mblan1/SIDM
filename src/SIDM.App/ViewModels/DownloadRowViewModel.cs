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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    [NotifyPropertyChangedFor(nameof(TotalBytesDisplay))]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private long? _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    private long _bytesDownloaded;

    public double ProgressPercent => TotalBytes is { } total && total > 0
        ? Math.Min(100.0, (double)BytesDownloaded * 100.0 / total)
        : 0.0;

    /// <summary>
    /// True while the download is active but its total size is unknown — bound
    /// to <see cref="System.Windows.Controls.ProgressBar.IsIndeterminate"/> so
    /// the bar animates instead of sitting at 0% (e.g. HLS/DASH playlists
    /// before all segments report sizes, or HTTP responses with no
    /// Content-Length).
    /// </summary>
    public bool IsIndeterminate
    {
        get
        {
            var sizeKnown = TotalBytes is { } t && t > 0;
            if (sizeKnown) return false;
            return Status == DownloadStatus.Downloading
                || Status == DownloadStatus.Probing
                || Status == DownloadStatus.Queued;
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
    /// "1.2 MiB/s" while active; em-dash while paused/completed/failed/queued.
    /// IDM "Transfer rate" column.
    /// </summary>
    public string TransferRateDisplay =>
        Status == DownloadStatus.Downloading && _speedBytesPerSecond > 1
            ? FormatBytes((long)_speedBytesPerSecond) + "/s"
            : "—";

    /// <summary>
    /// Remaining time estimate (HH:MM:SS) while active and size is known; em-dash otherwise.
    /// IDM "Time left" column.
    /// </summary>
    public string TimeLeftDisplay
    {
        get
        {
            if (Status != DownloadStatus.Downloading) return "—";
            if (TotalBytes is not { } total || total <= 0) return "—";
            if (_speedBytesPerSecond < 1) return "—";
            var remaining = total - BytesDownloaded;
            if (remaining <= 0) return "—";
            var seconds = remaining / _speedBytesPerSecond;
            if (seconds < 0 || double.IsInfinity(seconds) || double.IsNaN(seconds)) return "—";
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
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
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressDisplay));
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(TotalBytesDisplay));
                OnPropertyChanged(nameof(LastTryDisplay));
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
