using SIDM.Core.Models;
using SIDM.Core.Scheduling;

namespace SIDM.Core.Tests.Scheduling;

public class ScheduleEvaluatorTests
{
    [Fact]
    public void No_rules_means_always_allowed_with_no_overrides()
    {
        var decision = ScheduleEvaluator.Evaluate(Array.Empty<ScheduleRule>(), Now(DayOfWeek.Wednesday, 14, 0));

        decision.Allowed.Should().BeTrue();
        decision.MaxConcurrent.Should().Be(0);
        decision.BandwidthBytesPerSecond.Should().Be(0);
    }

    [Fact]
    public void Only_disabled_rules_means_always_allowed()
    {
        var rules = new[]
        {
            Rule("off", enabled: false, "00:00", "23:59", DaysOfWeek.AllDays),
        };

        ScheduleEvaluator.Evaluate(rules, Now(DayOfWeek.Wednesday, 14, 0))
            .Allowed.Should().BeTrue();
    }

    [Fact]
    public void Same_day_window_includes_both_endpoints()
    {
        var rule = Rule("nine to five", true, "09:00", "17:00", DaysOfWeek.Weekdays);

        ScheduleEvaluator.IsRuleActive(rule, Now(DayOfWeek.Tuesday, 9, 0)).Should().BeTrue("start is inclusive");
        ScheduleEvaluator.IsRuleActive(rule, Now(DayOfWeek.Tuesday, 17, 0)).Should().BeTrue("end is inclusive");
        ScheduleEvaluator.IsRuleActive(rule, Now(DayOfWeek.Tuesday, 12, 0)).Should().BeTrue();

        ScheduleEvaluator.IsRuleActive(rule, Now(DayOfWeek.Tuesday, 8, 59)).Should().BeFalse();
        ScheduleEvaluator.IsRuleActive(rule, Now(DayOfWeek.Tuesday, 17, 1)).Should().BeFalse();
    }

    [Fact]
    public void Same_day_window_respects_day_of_week_mask()
    {
        var weekdaysOnly = Rule("biz", true, "09:00", "17:00", DaysOfWeek.Weekdays);

        ScheduleEvaluator.IsRuleActive(weekdaysOnly, Now(DayOfWeek.Saturday, 12, 0)).Should().BeFalse();
        ScheduleEvaluator.IsRuleActive(weekdaysOnly, Now(DayOfWeek.Friday, 12, 0)).Should().BeTrue();
    }

    [Fact]
    public void Wrap_around_window_handles_both_halves_correctly()
    {
        // 22:00 → 06:00 weekdays. The "weekday" applies to the START day.
        var night = Rule("night", true, "22:00", "06:00", DaysOfWeek.Weekdays);

        // Mid-evening Monday 22:30 — start triggers on Monday (a weekday).
        ScheduleEvaluator.IsRuleActive(night, Now(DayOfWeek.Monday, 22, 30)).Should().BeTrue();

        // Early Tuesday 04:00 — carry-over from Monday 22:00. Monday is a weekday → fires.
        ScheduleEvaluator.IsRuleActive(night, Now(DayOfWeek.Tuesday, 4, 0)).Should().BeTrue();

        // Saturday 04:00 — would only fire if Friday is a weekday. Friday IS a weekday → fires.
        ScheduleEvaluator.IsRuleActive(night, Now(DayOfWeek.Saturday, 4, 0)).Should().BeTrue();

        // Sunday 23:00 — Sunday is NOT a weekday → must not fire.
        ScheduleEvaluator.IsRuleActive(night, Now(DayOfWeek.Sunday, 23, 0)).Should().BeFalse();

        // Monday 04:00 — carry-over from Sunday. Sunday is NOT a weekday → must not fire.
        ScheduleEvaluator.IsRuleActive(night, Now(DayOfWeek.Monday, 4, 0)).Should().BeFalse();

        // Tuesday 14:00 — between windows → must not fire.
        ScheduleEvaluator.IsRuleActive(night, Now(DayOfWeek.Tuesday, 14, 0)).Should().BeFalse();
    }

    [Fact]
    public void Rule_present_but_none_matching_means_blocked()
    {
        var rules = new[]
        {
            Rule("evenings", true, "20:00", "23:00", DaysOfWeek.AllDays),
        };

        var decision = ScheduleEvaluator.Evaluate(rules, Now(DayOfWeek.Wednesday, 14, 0));
        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public void Matching_rule_returns_its_overrides()
    {
        var rules = new[]
        {
            RuleWithCaps("night", true, "00:00", "23:59", DaysOfWeek.AllDays, maxConcurrent: 2, bandwidthKiBps: 5120),
        };

        var decision = ScheduleEvaluator.Evaluate(rules, Now(DayOfWeek.Friday, 12, 0));

        decision.Allowed.Should().BeTrue();
        decision.MaxConcurrent.Should().Be(2);
        decision.BandwidthBytesPerSecond.Should().Be(5120L * 1024);
    }

    [Fact]
    public void Multiple_matches_apply_most_restrictive_overrides()
    {
        var rules = new[]
        {
            RuleWithCaps("loose", true, "00:00", "23:59", DaysOfWeek.AllDays, maxConcurrent: 6, bandwidthKiBps: 10000),
            RuleWithCaps("tight", true, "10:00", "16:00", DaysOfWeek.Weekdays, maxConcurrent: 2, bandwidthKiBps: 1000),
        };

        var decision = ScheduleEvaluator.Evaluate(rules, Now(DayOfWeek.Tuesday, 13, 0));

        decision.Allowed.Should().BeTrue();
        decision.MaxConcurrent.Should().Be(2, "the most restrictive matching rule wins");
        decision.BandwidthBytesPerSecond.Should().Be(1000L * 1024);
    }

    [Fact]
    public void Zero_override_means_no_override_and_does_not_dominate_min()
    {
        var rules = new[]
        {
            RuleWithCaps("explicit", true, "00:00", "23:59", DaysOfWeek.AllDays, maxConcurrent: 5, bandwidthKiBps: 0),
            RuleWithCaps("unlimited", true, "00:00", "23:59", DaysOfWeek.AllDays, maxConcurrent: 0, bandwidthKiBps: 8000),
        };

        var decision = ScheduleEvaluator.Evaluate(rules, Now(DayOfWeek.Wednesday, 12, 0));

        decision.MaxConcurrent.Should().Be(5);
        decision.BandwidthBytesPerSecond.Should().Be(8000L * 1024);
    }

    [Fact]
    public void Malformed_time_means_rule_never_matches()
    {
        var rule = Rule("bad", true, "not-a-time", "17:00", DaysOfWeek.AllDays);

        ScheduleEvaluator.IsRuleActive(rule, Now(DayOfWeek.Monday, 12, 0)).Should().BeFalse();
    }

    [Fact]
    public void Single_digit_hour_is_tolerated()
    {
        var rule = Rule("morning", true, "9:00", "11:00", DaysOfWeek.AllDays);

        ScheduleEvaluator.IsRuleActive(rule, Now(DayOfWeek.Monday, 10, 0)).Should().BeTrue();
    }

    private static ScheduleRule Rule(string name, bool enabled, string start, string end, DaysOfWeek dow) =>
        new() { Name = name, Enabled = enabled, StartTime = start, EndTime = end, DaysOfWeek = dow };

    private static ScheduleRule RuleWithCaps(string name, bool enabled, string start, string end, DaysOfWeek dow,
        int maxConcurrent, int bandwidthKiBps) =>
        new()
        {
            Name = name,
            Enabled = enabled,
            StartTime = start,
            EndTime = end,
            DaysOfWeek = dow,
            MaxConcurrent = maxConcurrent,
            BandwidthKiBps = bandwidthKiBps,
        };

    /// <summary>Builds a fixed DateTimeOffset on the given day of week at HH:mm. Uses a reference week starting 2026-03-09 (Mon).</summary>
    private static DateTimeOffset Now(DayOfWeek dow, int hour, int minute)
    {
        // 2026-03-09 is a Monday. Offset 0..6 to land on the requested DoW.
        var monday = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);
        var dayDelta = ((int)dow - (int)DayOfWeek.Monday + 7) % 7;
        return new DateTimeOffset(monday.AddDays(dayDelta).AddHours(hour).AddMinutes(minute), TimeSpan.Zero);
    }
}
