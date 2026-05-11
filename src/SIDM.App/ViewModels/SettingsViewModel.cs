using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.Core.Models;

namespace SIDM.App.ViewModels;

/// <summary>
/// Backs the Settings dialog. Holds the current value of each user-tweakable
/// knob — max concurrent downloads, global bandwidth cap, and the list of
/// schedule rules. The dialog's owner persists changes via
/// <see cref="Services.DownloadQueue"/>,
/// <see cref="Services.BandwidthSettingsService"/>, and the schedule-rule
/// repository on accept.
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

    [ObservableProperty]
    private ScheduleRuleRowViewModel? _selectedRule;

    [ObservableProperty]
    private CategoryRowViewModel? _selectedCategory;

    [ObservableProperty]
    private string? _ytDlpPath;

    [ObservableProperty]
    private string? _ffmpegPath;

    [ObservableProperty]
    private string? _ytDlpStatus;

    [ObservableProperty]
    private string? _updateFeedUrl;

    [ObservableProperty]
    private bool _autoCheckUpdates;

    [ObservableProperty]
    private string? _updateStatus;

    [ObservableProperty]
    private bool _updateReadyToApply;

    [ObservableProperty]
    private bool _crashReportsEnabled;

    [ObservableProperty]
    private string? _crashReportsDsn;

    public ObservableCollection<ScheduleRuleRowViewModel> Rules { get; } = new();

    public ObservableCollection<CategoryRowViewModel> Categories { get; } = new();

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

/// <summary>
/// One row in the schedule-rules list. Wraps a <see cref="ScheduleRule"/> with
/// display-friendly properties.
/// </summary>
public partial class ScheduleRuleRowViewModel : ObservableObject
{
    private readonly ScheduleRule _model;

    public ScheduleRuleRowViewModel(ScheduleRule model)
    {
        _model = model;
    }

    public long Id => _model.Id;
    public string Name => _model.Name;
    public bool Enabled => _model.Enabled;
    public string Window => $"{_model.StartTime} – {_model.EndTime}";
    public string Days => FormatDays(_model.DaysOfWeek);
    public string Caps => FormatCaps(_model.MaxConcurrent, _model.BandwidthKiBps);

    public ScheduleRule ToRule() => new()
    {
        Id = _model.Id,
        Name = _model.Name,
        Enabled = _model.Enabled,
        StartTime = _model.StartTime,
        EndTime = _model.EndTime,
        DaysOfWeek = _model.DaysOfWeek,
        MaxConcurrent = _model.MaxConcurrent,
        BandwidthKiBps = _model.BandwidthKiBps,
    };

    private static string FormatDays(DaysOfWeek d)
    {
        if (d == DaysOfWeek.AllDays) return "Every day";
        if (d == DaysOfWeek.Weekdays) return "Weekdays";
        if (d == DaysOfWeek.Weekends) return "Weekends";

        var parts = new List<string>();
        if ((d & DaysOfWeek.Monday) != 0) parts.Add("Mon");
        if ((d & DaysOfWeek.Tuesday) != 0) parts.Add("Tue");
        if ((d & DaysOfWeek.Wednesday) != 0) parts.Add("Wed");
        if ((d & DaysOfWeek.Thursday) != 0) parts.Add("Thu");
        if ((d & DaysOfWeek.Friday) != 0) parts.Add("Fri");
        if ((d & DaysOfWeek.Saturday) != 0) parts.Add("Sat");
        if ((d & DaysOfWeek.Sunday) != 0) parts.Add("Sun");
        return parts.Count == 0 ? "(never)" : string.Join(", ", parts);
    }

    private static string FormatCaps(int maxConcurrent, int bandwidthKiBps)
    {
        var parts = new List<string>();
        if (maxConcurrent > 0) parts.Add($"max {maxConcurrent}");
        if (bandwidthKiBps > 0) parts.Add($"{bandwidthKiBps} KiB/s");
        return parts.Count == 0 ? "no override" : string.Join(", ", parts);
    }
}

/// <summary>One row in the categories list. Wraps a <see cref="Category"/> for display.</summary>
public partial class CategoryRowViewModel : ObservableObject
{
    private readonly Category _model;

    public CategoryRowViewModel(Category model)
    {
        _model = model;
    }

    public long Id => _model.Id;
    public string Name => _model.Name;
    public string Extensions => _model.Extensions ?? "";
    public string DefaultPath => _model.DefaultPath ?? "";

    public Category ToCategory() => new()
    {
        Id = _model.Id,
        Name = _model.Name,
        Extensions = _model.Extensions,
        DefaultPath = _model.DefaultPath,
        Color = _model.Color,
    };
}
