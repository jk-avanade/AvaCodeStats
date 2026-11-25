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
            .Where(s => s.SamePeriodAs(current) && s.TimestampUtc <= cutoff)
            .OrderByDescending(s => s.TimestampUtc)
            .FirstOrDefault();

        if (baseline is null)
        {
            // Window reaches back before the period started: everything since reset counts.
            return current.PeriodStartUtc >= cutoff ? current.Used : null;
        }
        return Math.Max(0, current.Used - baseline.Used);
    }

    /// <summary>
    /// Share of the quota used over time, for a chart across periods: every reading, plus a drop to 0 at each
    /// reset so the line shows the sawtooth of the periods. Pass the current reading to extend it to now.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset At, double Fraction)> UsedOverTime(IEnumerable<UsageSnapshot> samples, UsageSnapshot? current = null)
    {
        var all = (current is null ? samples : samples.Append(current))
            .GroupBy(s => s.TimestampUtc).Select(g => g.Last())
            .OrderBy(s => s.TimestampUtc);
        var points = new List<(DateTimeOffset, double)>();
        UsageSnapshot? previous = null;
        foreach (var s in all)
        {
            if (previous is not null && s.IsLaterPeriodThan(previous))
            {
                // Flat until the reset, then straight down to 0.
                var reset = s.PeriodStartUtc < previous.TimestampUtc ? previous.TimestampUtc : s.PeriodStartUtc;
                points.Add((reset, previous.UsedFraction));
                points.Add((reset, 0));
            }
            points.Add((s.TimestampUtc, s.UsedFraction));
            previous = s;
        }
        return points;
    }

    /// <summary>
    /// How far ahead of (+) or behind (−) an even pace a quota was over time: share used minus share of the period
    /// gone, from −1 (nothing used at the reset) to +1 (all used at once). Between readings nothing was used, so
    /// the line falls as time passes and steps up at each reading; at a reset it falls to the end of the period
    /// and starts again from 0. Pass the current reading and now to extend it to now.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset At, double Delta)> PaceOverTime(
        IEnumerable<UsageSnapshot> samples, UsageSnapshot? current = null, DateTimeOffset? nowUtc = null)
    {
        var all = (current is null ? samples : samples.Append(current))
            .Where(s => s.Entitlement > 0 && s.PeriodEndUtc > s.PeriodStartUtc)
            .GroupBy(s => s.TimestampUtc).Select(g => g.Last())
            .OrderBy(s => s.TimestampUtc);
        var points = new List<(DateTimeOffset, double)>();
        UsageSnapshot? previous = null;
        foreach (var s in all)
        {
            if (previous is not null)
            {
                if (s.IsLaterPeriodThan(previous))
                {
                    var reset = s.PeriodStartUtc < previous.TimestampUtc ? previous.TimestampUtc : s.PeriodStartUtc;
                    var end = previous.PeriodEndUtc < reset ? previous.PeriodEndUtc : reset;
                    points.Add((end, Delta(previous, end)));
                    points.Add((reset, -Elapsed(s, reset)));
                }
                else
                {
                    points.Add((s.TimestampUtc, Delta(previous, s.TimestampUtc)));
                }
            }
            points.Add((s.TimestampUtc, Delta(s, s.TimestampUtc)));
            previous = s;
        }
        if (previous is not null && nowUtc is { } now && now > previous.TimestampUtc)
        {
            var until = now < previous.PeriodEndUtc ? now : previous.PeriodEndUtc;
            points.Add((until, Delta(previous, until)));
        }
        return points;

        static double Elapsed(UsageSnapshot s, DateTimeOffset at) =>
            Math.Clamp((at - s.PeriodStartUtc) / (s.PeriodEndUtc - s.PeriodStartUtc), 0, 1);
        static double Delta(UsageSnapshot s, DateTimeOffset at) => Math.Clamp(s.UsedFraction, 0, 1) - Elapsed(s, at);
    }

    /// <summary>Time-weighted average of a line between two times, over the part of it the line covers; null when it covers none.</summary>
    public static double? TimeAverage(IReadOnlyList<(DateTimeOffset At, double Value)> points, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        double area = 0, span = 0;
        for (var i = 1; i < points.Count; i++)
        {
            var (a, va) = points[i - 1];
            var (b, vb) = points[i];
            if (b <= a || b <= fromUtc || a >= toUtc) continue;
            var length = (b - a).TotalSeconds;
            double At(DateTimeOffset t) => va + (vb - va) * ((t - a).TotalSeconds / length);
            var x0 = a < fromUtc ? fromUtc : a;
            var x1 = b > toUtc ? toUtc : b;
            var seconds = (x1 - x0).TotalSeconds;
            area += (At(x0) + At(x1)) / 2 * seconds;
            span += seconds;
        }
        return span > 0 ? area / span : null;
    }

    /// <summary>
    /// Share of the quota burned between <paramref name="fromUtc"/> and <paramref name="toUtc"/>, added up across
    /// resets (so it can pass 1). The baseline is the last reading at or before the start; without one, a period
    /// that starts inside the window counts from 0, and otherwise counting starts at the first reading.
    /// </summary>
    public static double BurnedFraction(IEnumerable<UsageSnapshot> samples, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var ordered = samples.OrderBy(s => s.TimestampUtc).ToList();
        var previous = ordered.LastOrDefault(s => s.TimestampUtc <= fromUtc);
        double total = 0;
        foreach (var s in ordered.Where(s => s.TimestampUtc > fromUtc && s.TimestampUtc <= toUtc))
        {
            if (previous is null)
            {
                if (s.PeriodStartUtc >= fromUtc) total += s.UsedFraction;
            }
            else if (s.IsLaterPeriodThan(previous))
            {
                total += s.UsedFraction; // reset in between: counts from 0
            }
            else
            {
                total += Math.Max(0, s.UsedFraction - previous.UsedFraction);
            }
            previous = s;
        }
        return total;
    }
}
