using AvaCodeStats.Core;

namespace AvaCodeStats.Core.Tests;

public class PaceCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero); // 30 days

    private static UsageSnapshot Snap(double used, double entitlement = 3000) =>
        new("test", "me", "premium", "Premium", used, entitlement, false, Start, End, Start);

    [Fact]
    public void Halfway_with_half_used_is_on_pace()
    {
        var p = PaceCalculator.Calculate(Snap(1500), Start.AddDays(15));

        Assert.Equal(0.5, p.ElapsedFraction, 6);
        Assert.Equal(1500, p.ExpectedUsed, 6);
        Assert.Equal(100, p.DailyAllowance, 6);
        Assert.Equal(0, p.DaysOverPace, 6);
        Assert.Equal(PaceStatus.OnPace, p.Status);
        Assert.Equal(3000, p.ProjectedUsedAtReset, 6);
        Assert.Null(p.ProjectedExhaustionUtc);
    }

    [Fact]
    public void Burning_fast_reports_days_over_pace_and_exhaustion_date()
    {
        // Day 10 of 30, used 2000 of 3000: pace is 1000, so 1000 over = 10 days of allowance.
        var now = Start.AddDays(10);
        var p = PaceCalculator.Calculate(Snap(2000), now);

        Assert.Equal(10, p.DaysOverPace, 6);
        Assert.Equal(PaceStatus.FarOver, p.Status);
        Assert.Equal(50, p.DailyBudgetLeft, 6); // 1000 left over 20 days
        Assert.Equal(6000, p.ProjectedUsedAtReset, 6);
        Assert.Equal(now.AddDays(5), p.ProjectedExhaustionUtc); // 200/day, 1000 left
    }

    [Fact]
    public void Slightly_over_and_under_use_thresholds()
    {
        var now = Start.AddDays(15);
        Assert.Equal(PaceStatus.SlightlyOver, PaceCalculator.Calculate(Snap(1600), now).Status); // +1 day
        Assert.Equal(PaceStatus.UnderPace, PaceCalculator.Calculate(Snap(1300), now).Status);    // -2 days
        Assert.Equal(PaceStatus.OnPace, PaceCalculator.Calculate(Snap(1540), now).Status);       // +0.4 day
    }

    [Fact]
    public void Used_up_is_exhausted()
    {
        var p = PaceCalculator.Calculate(Snap(3000), Start.AddDays(20));
        Assert.Equal(PaceStatus.Exhausted, p.Status);
        Assert.Equal(0, p.DailyBudgetLeft);
    }

    [Fact]
    public void Unlimited_has_no_pace()
    {
        var s = Snap(10) with { Unlimited = true };
        Assert.Equal(PaceStatus.Unknown, PaceCalculator.Calculate(s, Start.AddDays(3)).Status);
    }

    [Fact]
    public void Time_outside_period_is_clamped()
    {
        Assert.Equal(1, PaceCalculator.Calculate(Snap(100), End.AddDays(2)).ElapsedFraction);
        Assert.Equal(0, PaceCalculator.Calculate(Snap(0), Start.AddDays(-1)).ElapsedFraction);
    }
}
