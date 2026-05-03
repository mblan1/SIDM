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
    private readonly Dictionary<int, long> _segmentBytes = new();

    public DownloadRowViewModel(Download model, UiProgressBus bus)
    {
        _model = model;
        _bus = bus;
        _bytesDownloaded = model.Segments.Sum(s => s.BytesDownloaded);
        _status = model.Status;
        _totalBytes = model.TotalBytes;

        foreach (var s in model.Segments)
        {
            _segmentBytes[s.Idx] = s.BytesDownloaded;
        }

        _subscription = bus.Subscribe(model.Id, OnSegmentProgress);
    }

    public long Id => _model.Id;
    public string Url => _model.Url;
    public string FileName => _model.FileName;
    public string TargetPath => _model.TargetPath;

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

    private void OnSegmentProgress(int segmentIndex, long bytes)
    {
        _segmentBytes[segmentIndex] = bytes;
        var total = _segmentBytes.Values.Sum();

        // Marshal to UI thread.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            BytesDownloaded = total;
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressDisplay));
        }
        else
        {
            dispatcher.BeginInvoke(() =>
            {
                BytesDownloaded = total;
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressDisplay));
            });
        }
    }

    /// <summary>Refreshes from the latest persisted model (call after status changes).</summary>
    public void RefreshFrom(Download fresh)
    {
        Status = fresh.Status;
        TotalBytes = fresh.TotalBytes;
        if (fresh.Segments.Count > 0)
        {
            _segmentBytes.Clear();
            foreach (var s in fresh.Segments) _segmentBytes[s.Idx] = s.BytesDownloaded;
            BytesDownloaded = _segmentBytes.Values.Sum();
        }
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
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
