using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.Core.Models;
using Media = System.Windows.Media;

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
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private SegmentStatus _status;

    public double Percent => Length > 0 ? Math.Min(100.0, (double)BytesDownloaded * 100.0 / Length) : 0.0;

    public string RangeDisplay => string.Format(CultureInfo.CurrentCulture, "#{0}  {1}–{2}", Idx, StartByte, EndByte);
    public string ProgressDisplay => string.Format(CultureInfo.CurrentCulture, "{0:F1}%  ({1} / {2})",
        Percent, FormatBytes(BytesDownloaded), FormatBytes(Length));

    /// <summary>Short label for the segment's current state — drives the
    /// per-chunk status pill in the dialog.</summary>
    public string StatusDisplay => Status switch
    {
        SegmentStatus.Pending   => "Pending",
        SegmentStatus.Active    => "Active",
        SegmentStatus.Completed => "Done",
        SegmentStatus.Failed    => "Failed",
        _ => Status.ToString(),
    };

    /// <summary>Color that ties the per-chunk ProgressBar fill to its state —
    /// green=done, blue=active, gray=pending, red=failed. Matches WPF-UI's
    /// accent palette so it doesn't clash with the rest of the chrome.</summary>
    public Media.Brush StatusBrush => Status switch
    {
        SegmentStatus.Completed => new Media.SolidColorBrush(Media.Color.FromRgb(0x16, 0xA3, 0x4A)), // green-600
        SegmentStatus.Active    => new Media.SolidColorBrush(Media.Color.FromRgb(0x00, 0x67, 0xC0)), // accent blue
        SegmentStatus.Failed    => new Media.SolidColorBrush(Media.Color.FromRgb(0xC4, 0x2B, 0x1C)), // red
        _                       => new Media.SolidColorBrush(Media.Color.FromRgb(0x70, 0x70, 0x70)), // pending/unknown
    };

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
