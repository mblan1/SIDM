using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.Core.Models;

namespace SIDM.App.ViewModels;

/// <summary>
/// One chunk row in the per-download progress window. Tracks bytes downloaded
/// for a single <see cref="Segment"/> and exposes display strings the chunk
/// grid binds to.
/// </summary>
public partial class SegmentProgressViewModel : ObservableObject
{
    public SegmentProgressViewModel(Segment segment)
    {
        Idx = segment.Idx;
        StartByte = segment.StartByte;
        EndByte = segment.EndByte;
        _bytesDownloaded = segment.BytesDownloaded;
        _status = segment.Status;
    }

    public int Idx { get; }
    public long StartByte { get; }
    public long EndByte { get; }
    public long Length => EndByte - StartByte + 1;

    [ObservableProperty]
    private long _bytesDownloaded;

    [ObservableProperty]
    private SegmentStatus _status;

    public double Percent => Length > 0 ? Math.Min(100.0, (double)BytesDownloaded * 100.0 / Length) : 0.0;

    public string RangeDisplay => string.Format(CultureInfo.CurrentCulture, "#{0}  {1}–{2}", Idx, StartByte, EndByte);
    public string ProgressDisplay => string.Format(CultureInfo.CurrentCulture, "{0:F1}%  ({1} / {2})",
        Percent, FormatBytes(BytesDownloaded), FormatBytes(Length));

    public void UpdateBytes(long bytes)
    {
        if (bytes == BytesDownloaded) return;
        BytesDownloaded = bytes;
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(ProgressDisplay));
    }

    public void UpdateStatus(SegmentStatus status)
    {
        Status = status;
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
