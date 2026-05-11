using System.Windows;
using SIDM.Core.Models;
using SIDM.Core.Scheduling;
using Wpf.Ui.Controls;

namespace SIDM.App.Views;

/// <summary>
/// Modal editor for a single <see cref="ScheduleRule"/>. Used by the Settings
/// dialog for both Add and Edit flows. Owns its own validation and returns
/// the built rule via <see cref="ToRule"/> on accept.
/// </summary>
public partial class ScheduleRuleEditorDialog : FluentWindow
{
    public ScheduleRuleEditorDialog()
    {
        InitializeComponent();
        // Sensible defaults for a brand-new rule.
        StartTimeBox.Text = "22:00";
        EndTimeBox.Text = "06:00";
        NameBox.Text = "New rule";
        MaxConcurrentBox.Text = "0";
        BandwidthBox.Text = "0";
        MonBox.IsChecked = TueBox.IsChecked = WedBox.IsChecked = ThuBox.IsChecked = FriBox.IsChecked = true;
    }

    public void LoadFrom(ScheduleRule rule)
    {
        NameBox.Text = rule.Name;
        EnabledBox.IsChecked = rule.Enabled;
        StartTimeBox.Text = rule.StartTime;
        EndTimeBox.Text = rule.EndTime;
        MaxConcurrentBox.Text = rule.MaxConcurrent.ToString();
        BandwidthBox.Text = rule.BandwidthKiBps.ToString();
        SetDayChecks(rule.DaysOfWeek);
    }

    public ScheduleRule ToRule() => new()
    {
        Name = NameBox.Text?.Trim() ?? "Rule",
        Enabled = EnabledBox.IsChecked == true,
        StartTime = StartTimeBox.Text?.Trim() ?? "",
        EndTime = EndTimeBox.Text?.Trim() ?? "",
        DaysOfWeek = CollectDays(),
        MaxConcurrent = ParseNonNegative(MaxConcurrentBox.Text),
        BandwidthKiBps = ParseNonNegative(BandwidthBox.Text),
    };

    private void OnPickWeekdays(object sender, RoutedEventArgs e) => SetDayChecks(DaysOfWeek.Weekdays);
    private void OnPickWeekends(object sender, RoutedEventArgs e) => SetDayChecks(DaysOfWeek.Weekends);
    private void OnPickAllDays(object sender, RoutedEventArgs e) => SetDayChecks(DaysOfWeek.AllDays);

    private void SetDayChecks(DaysOfWeek d)
    {
        MonBox.IsChecked = (d & DaysOfWeek.Monday) != 0;
        TueBox.IsChecked = (d & DaysOfWeek.Tuesday) != 0;
        WedBox.IsChecked = (d & DaysOfWeek.Wednesday) != 0;
        ThuBox.IsChecked = (d & DaysOfWeek.Thursday) != 0;
        FriBox.IsChecked = (d & DaysOfWeek.Friday) != 0;
        SatBox.IsChecked = (d & DaysOfWeek.Saturday) != 0;
        SunBox.IsChecked = (d & DaysOfWeek.Sunday) != 0;
    }

    private DaysOfWeek CollectDays()
    {
        var d = DaysOfWeek.None;
        if (MonBox.IsChecked == true) d |= DaysOfWeek.Monday;
        if (TueBox.IsChecked == true) d |= DaysOfWeek.Tuesday;
        if (WedBox.IsChecked == true) d |= DaysOfWeek.Wednesday;
        if (ThuBox.IsChecked == true) d |= DaysOfWeek.Thursday;
        if (FriBox.IsChecked == true) d |= DaysOfWeek.Friday;
        if (SatBox.IsChecked == true) d |= DaysOfWeek.Saturday;
        if (SunBox.IsChecked == true) d |= DaysOfWeek.Sunday;
        return d;
    }

    private static int ParseNonNegative(string? raw)
    {
        if (int.TryParse(raw, out var v) && v >= 0) return v;
        return 0;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ErrorText.Text = "Name is required.";
            return;
        }
        if (!ScheduleEvaluator.TryParseHHmm(StartTimeBox.Text?.Trim() ?? "", out _))
        {
            ErrorText.Text = "Start time must be HH:mm (24-hour).";
            return;
        }
        if (!ScheduleEvaluator.TryParseHHmm(EndTimeBox.Text?.Trim() ?? "", out _))
        {
            ErrorText.Text = "End time must be HH:mm (24-hour).";
            return;
        }
        if (CollectDays() == DaysOfWeek.None)
        {
            ErrorText.Text = "Pick at least one day of the week.";
            return;
        }

        DialogResult = true;
        Close();
    }
}
