namespace SIDM.Core.Models;

[Flags]
public enum DaysOfWeek
{
    None = 0,
    Sunday = 1 << 0,
    Monday = 1 << 1,
    Tuesday = 1 << 2,
    Wednesday = 1 << 3,
    Thursday = 1 << 4,
    Friday = 1 << 5,
    Saturday = 1 << 6,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekends = Saturday | Sunday,
    AllDays = Weekdays | Weekends,
}

public class ScheduleRule
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>"HH:mm" 24-hour format.</summary>
    public required string StartTime { get; set; }
    /// <summary>"HH:mm" 24-hour format.</summary>
    public required string EndTime { get; set; }
    public DaysOfWeek DaysOfWeek { get; set; } = DaysOfWeek.AllDays;
    public int MaxConcurrent { get; set; } = 4;
    /// <summary>Total bandwidth limit in KiB/s during this window. Zero = unlimited.</summary>
    public int BandwidthKiBps { get; set; }
}
