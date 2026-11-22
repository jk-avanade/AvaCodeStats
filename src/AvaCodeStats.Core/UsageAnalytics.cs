namespace AvaCodeStats.Core;

public sealed record DailyBurn(DateOnly Day, double Burned, bool Estimated = false);

public sealed record PeriodSummary(
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    double Used,
    double Entitlement,
    int SampleCount)
{
    public double UsedFraction => Entitlement <= 0 ? 0 : Used / Entitlement;
}

public static class UsageAnalytics
{
    /// <summary>
    /// Burn per local calendar day within one period. When readings are missing for some days
    /// (app not running, or history starts mid-period), the usage between two readings is spread
    /// evenly over those days and flagged <see cref="DailyBurn.Estimated"/>.
    /// </summary>
    public static IReadOnlyList<DailyBurn> DailyBurns(
        IEnumerable<UsageSnapshot> periodSamples,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        TimeZoneInfo zone)
    {
        DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, zone).DateTime);

        var lastByDay = new SortedDictionary<DateOnly, double>();
        foreach (var s in periodSamples
                     .Where(s => s.TimestampUtc >= periodStartUtc && s.TimestampUtc < periodEndUtc)
                     .OrderBy(s => s.TimestampUtc))
        {
            lastByDay[Day(s.TimestampUtc)] = s.Used; // ascending, so the last write is the day's latest value
        }

        var result = new List<DailyBurn>();
        // Usage is 0 at the reset, so the reset acts as a reading at the end of the day before.
        var previousDay = Day(periodStartUtc).AddDays(-1);
        double previous = 0;
        foreach (var (day, used) in lastByDay)
        {
            var gap = day.DayNumber - previousDay.DayNumber;
            var burned = Math.Max(0, used - previous);
            for (var d = previousDay.AddDays(1); d <= day; d = d.AddDays(1))
                result.Add(new DailyBurn(d, burned / gap, Estimated: gap > 1));
            previousDay = day;
            previous = used;
        }
        return result;
    }

    /// <summary>Burn over the last <paramref name="window"/>, or null when there is no sample old enough.</summary>
    public static double? BurnedWithin(IEnumerable<UsageSnapshot> periodSamples, UsageSnapshot current, TimeSpan window)
    {
        var cutoff = current.TimestampUtc - window;
        var baseline = periodSamples
            .Where(s => s.PeriodStartUtc == current.PeriodStartUtc && s.TimestampUtc <= cutoff)
            .OrderByDescending(s => s.TimestampUtc)
            .FirstOrDefault();

        if (baseline is null)
        {
            // Window reaches back before the period started: everything since reset counts.
            return current.PeriodStartUtc >= cutoff ? current.Used : null;
        }
        return Math.Max(0, current.Used - baseline.Used);
    }
}
