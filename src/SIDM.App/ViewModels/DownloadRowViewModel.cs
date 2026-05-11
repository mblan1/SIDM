using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.App.Services;
using SIDM.Core.Models;

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

    public DownloadRowViewModel(Download model, UiProgressBus bus)
    {
        _model = model;
        _bus = bus;
        _bytesDownloaded = model.Segments.Sum(s => s.BytesDownloaded);
        _status = model.Status;
        _totalBytes = model.TotalBytes;

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

    public ObservableCollection<SegmentProgressViewModel> SegmentRows { get; }

    [ObservableProperty]
    private DownloadStatus _status;

    [ObservableProperty]
    private long? _totalBytes;

    [ObservableProperty]
    private long _bytesDownloaded;

    public double ProgressPercent => TotalBytes is { } total && total > 0
        ? Math.Min(100.0, (double)BytesDownloaded * 100.0 / total)
        : 0.0;

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

    public string StatusDisplay => Status.ToString();

    public string TotalBytesDisplay => TotalBytes is { } t ? FormatBytes(t) : "Unknown";

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
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressDisplay));
    }

    /// <summary>Refreshes from the latest persisted model (call after status changes).</summary>
    public void RefreshFrom(Download fresh)
    {
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
