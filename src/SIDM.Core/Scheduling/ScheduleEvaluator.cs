using System.Globalization;
using SIDM.Core.Models;

namespace SIDM.Core.Scheduling;

/// <summary>
/// Outcome of evaluating the user's <see cref="ScheduleRule"/>s at a given
/// instant. <see cref="Allowed"/> is the binary "can downloads run now"
/// decision; the optional caps are the most-restrictive value across all
/// matching rules (0 = no override).
/// </summary>
/// <param name="Allowed">True if downloads are permitted right now.</param>
/// <param name="MaxConcurrent">Cap on concurrent downloads (0 = no override).</param>
/// <param name="BandwidthBytesPerSecond">Aggregate cap in bytes/sec (0 = no override).</param>
public sealed record ScheduleDecision(bool Allowed, int MaxConcurrent, long BandwidthBytesPerSecond)
{
    public static ScheduleDecision AlwaysAllowed { get; } = new(true, 0, 0);
}

/// <summary>
/// Pure decision function: given the persisted rule list and the current
/// time, return whether downloads may run and which overrides apply.
///
/// Semantics:
/// - No enabled rules → <see cref="ScheduleDecision.AlwaysAllowed"/> (the
///   feature is opt-in; absence of rules means "no restriction").
/// - At least one enabled rule exists → allowed only when at least one rule
///   matches the current time + day. Otherwise blocked.
/// - When multiple rules match, the most restrictive override wins:
///   <c>min(non-zero MaxConcurrent)</c> and <c>min(non-zero Bandwidth)</c>.
///
/// Time windows are inclusive of both endpoints. Wrap-around (StartTime
/// greater than EndTime, e.g. 22:00→06:00) is supported: the window starts
/// on a day the <see cref="ScheduleRule.DaysOfWeek"/> mask permits and runs
/// to the following day's EndTime.
/// </summary>
public static class ScheduleEvaluator
{
    public static ScheduleDecision Evaluate(IReadOnlyList<ScheduleRule> rules, DateTimeOffset now)
    {
        var enabled = rules.Where(r => r.Enabled).ToList();
        if (enabled.Count == 0) return ScheduleDecision.AlwaysAllowed;

        var matches = enabled.Where(r => IsRuleActive(r, now)).ToList();
        if (matches.Count == 0) return new ScheduleDecision(false, 0, 0);

        var maxConcurrent = MinNonZero(matches.Select(r => r.MaxConcurrent));
        var bandwidthKiBps = MinNonZero(matches.Select(r => r.BandwidthKiBps));
        return new ScheduleDecision(
            Allowed: true,
            MaxConcurrent: maxConcurrent,
            BandwidthBytesPerSecond: bandwidthKiBps * 1024L);
    }

    public static bool IsRuleActive(ScheduleRule rule, DateTimeOffset now)
    {
        if (!rule.Enabled) return false;
        if (!TryParseHHmm(rule.StartTime, out var start)) return false;
        if (!TryParseHHmm(rule.EndTime, out var end)) return false;

        var nowTime = now.TimeOfDay;
        var today = ToFlag(now.DayOfWeek);
        var yesterday = ToFlag(now.AddDays(-1).DayOfWeek);

        if (start <= end)
        {
            // Same-day window. Today must be in the mask AND now must be inside [start, end].
            return (rule.DaysOfWeek & today) != 0
                && nowTime >= start && nowTime <= end;
        }

        // Wrap-around window (e.g. 22:00 → 06:00). Window is active if either:
        //   1) we are past Start today (and the rule fires on today's DoW), or
        //   2) we are before End today (and the rule fired yesterday on yesterday's DoW).
        var afterStartToday = nowTime >= start && (rule.DaysOfWeek & today) != 0;
        var beforeEndCarryOver = nowTime <= end && (rule.DaysOfWeek & yesterday) != 0;
        return afterStartToday || beforeEndCarryOver;
    }

    public static bool TryParseHHmm(string value, out TimeSpan time)
    {
        if (TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out time))
        {
            return true;
        }
        // Tolerate single-digit hours like "9:30".
        return TimeSpan.TryParseExact(value, "h\\:mm", CultureInfo.InvariantCulture, out time);
    }

    private static DaysOfWeek ToFlag(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Sunday => DaysOfWeek.Sunday,
        DayOfWeek.Monday => DaysOfWeek.Monday,
        DayOfWeek.Tuesday => DaysOfWeek.Tuesday,
        DayOfWeek.Wednesday => DaysOfWeek.Wednesday,
        DayOfWeek.Thursday => DaysOfWeek.Thursday,
        DayOfWeek.Friday => DaysOfWeek.Friday,
        DayOfWeek.Saturday => DaysOfWeek.Saturday,
        _ => DaysOfWeek.None,
    };

    /// <summary>Returns the smallest positive value, or 0 if none are positive.</summary>
    private static int MinNonZero(IEnumerable<int> values)
    {
        var min = 0;
        foreach (var v in values)
        {
            if (v <= 0) continue;
            if (min == 0 || v < min) min = v;
        }
        return min;
    }
}
