namespace AvaCodeStats.Core;

public enum PaceStatus
{
    Unknown,
    /// <summary>Burning slower than an even pace: budget is being banked.</summary>
    UnderPace,
    /// <summary>Within the on-pace tolerance.</summary>
    OnPace,
    /// <summary>Burning faster than pace, but not by much.</summary>
    SlightlyOver,
    /// <summary>Burning much faster than pace: will run out before the reset.</summary>
    FarOver,
    /// <summary>Quota is used up.</summary>
    Exhausted,
}

/// <summary>How many "days of allowance" away from pace count as on pace / far over.</summary>
public sealed record PaceThresholds(double OnPaceDays = 0.5, double FarOverDays = 2.0)
{
    public static PaceThresholds Default { get; } = new();
}

/// <summary>
/// Pace = an even burn from period start (0) to period end (full entitlement).
/// Everything is expressed both in units (requests) and in "days of allowance",
/// where one day of allowance = entitlement / days in period.
/// </summary>
public sealed record PaceInfo(
    double ElapsedFraction,
    double ExpectedUsed,
    double Delta,
    double DailyAllowance,
    double DaysOverPace,
    double DaysLeft,
    double DailyBudgetLeft,
    double ProjectedUsedAtReset,
    DateTimeOffset? ProjectedExhaustionUtc,
    PaceStatus Status)
{
    public double PaceFraction => ElapsedFraction;
}

public static class PaceCalculator
{
    public static PaceInfo Calculate(UsageSnapshot s, DateTimeOffset now, PaceThresholds? thresholds = null)
    {
        thresholds ??= PaceThresholds.Default;

        var total = (s.PeriodEndUtc - s.PeriodStartUtc).TotalDays;
        if (total <= 0 || s.Unlimited || s.Entitlement <= 0)
        {
            return new PaceInfo(0, 0, 0, 0, 0, 0, 0, s.Used, null, PaceStatus.Unknown);
        }

        var elapsedDays = Math.Clamp((now - s.PeriodStartUtc).TotalDays, 0, total);
        var elapsed = elapsedDays / total;
        var daysLeft = total - elapsedDays;

        var expected = s.Entitlement * elapsed;
        var delta = s.Used - expected;
        var dailyAllowance = s.Entitlement / total;
        var daysOver = delta / dailyAllowance;
        var dailyBudgetLeft = daysLeft > 0 ? s.Remaining / daysLeft : 0;

        // Linear projection at the average burn so far.
        var avgPerDay = elapsedDays > 0 ? s.Used / elapsedDays : 0;
        var projected = s.Used + avgPerDay * daysLeft;
        DateTimeOffset? exhaustion = null;
        if (avgPerDay > 0 && projected > s.Entitlement && s.Remaining > 0)
        {
            exhaustion = now.AddDays(s.Remaining / avgPerDay);
        }

        PaceStatus status;
        if (s.Remaining <= 0) status = PaceStatus.Exhausted;
        else if (daysOver < -thresholds.OnPaceDays) status = PaceStatus.UnderPace;
        else if (daysOver <= thresholds.OnPaceDays) status = PaceStatus.OnPace;
        else if (daysOver <= thresholds.FarOverDays) status = PaceStatus.SlightlyOver;
        else status = PaceStatus.FarOver;

        return new PaceInfo(elapsed, expected, delta, dailyAllowance, daysOver, daysLeft,
            dailyBudgetLeft, projected, exhaustion, status);
    }

    /// <summary>Human wording for the pace delta, e.g. "4.4 days over pace".</summary>
    public static string Describe(PaceInfo p) => p.Status switch
    {
        PaceStatus.Unknown => "No pace (unlimited or unknown quota)",
        PaceStatus.Exhausted => "Quota used up",
        PaceStatus.OnPace => "On pace",
        _ when p.DaysOverPace > 0 => $"{p.DaysOverPace:0.0} days over pace",
        _ => $"{-p.DaysOverPace:0.0} days under pace",
    };
}
