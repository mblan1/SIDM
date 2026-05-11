using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SIDM.App.ViewModels;

/// <summary>
/// Backs the Settings dialog. Holds the current value of each user-tweakable
/// knob — max concurrent downloads and global bandwidth cap — as plain
/// properties. The dialog's owner persists them via
/// <see cref="Services.DownloadQueue"/> and
/// <see cref="Services.BandwidthSettingsService"/> on accept.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private int _maxConcurrent = 4;

    /// <summary>Bandwidth cap displayed in KiB/s; <c>0</c> means unlimited.</summary>
    [ObservableProperty]
    private long _bandwidthKiBPerSec;

    [ObservableProperty]
    private string? _errorMessage;

    public string BandwidthDisplay => BandwidthKiBPerSec <= 0
        ? "Unlimited"
        : string.Format(CultureInfo.CurrentCulture, "{0} KiB/s ({1:F1} MiB/s)",
            BandwidthKiBPerSec, BandwidthKiBPerSec / 1024.0);

    partial void OnBandwidthKiBPerSecChanged(long value) => OnPropertyChanged(nameof(BandwidthDisplay));

    public long BandwidthBytesPerSecond => Math.Max(0, BandwidthKiBPerSec) * 1024L;

    public void SetFromBytes(long bytesPerSecond)
    {
        BandwidthKiBPerSec = bytesPerSecond <= 0 ? 0 : bytesPerSecond / 1024;
    }
}
