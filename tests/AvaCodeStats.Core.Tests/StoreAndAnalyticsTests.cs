using AvaCodeStats.Core.Storage;

namespace AvaCodeStats.Core.Tests;

public sealed class StoreAndAnalyticsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"avacodestats-test-{Guid.NewGuid():N}.db");

    private static UsageSnapshot At(double hours, double used, DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new("test", "me", "premium", "Premium", used, 1000, false, start ?? Start, end ?? End, (start ?? Start).AddHours(hours));

    [Fact]
    public void Skips_unchanged_readings_within_heartbeat()
    {
        var store = new UsageStore(_db);
        Assert.True(store.Add(At(1, 10)));
        Assert.False(store.Add(At(1.2, 10)));  // same value, 12 minutes later
        Assert.True(store.Add(At(1.3, 11)));   // changed
        Assert.True(store.Add(At(2.5, 11)));   // unchanged, but heartbeat elapsed

        var samples = store.GetSamples(At(0, 0).Key, Start, End);
        Assert.Equal(3, samples.Count);
        Assert.Equal(11, store.GetLatest(At(0, 0).Key)!.Used);
    }

    [Fact]
    public void Summarises_each_period()
    {
        var store = new UsageStore(_db);
        var octStart = Start.AddMonths(-1);
        store.Add(At(5, 300, octStart, Start));
        store.Add(At(500, 950, octStart, Start));
        store.Add(At(5, 40));

        var periods = store.GetPeriodSummaries(At(0, 0).Key);
        Assert.Equal(2, periods.Count);
        Assert.Equal(950, periods[0].Used);
        Assert.Equal(0.95, periods[0].UsedFraction, 6);
        Assert.Equal(40, periods[1].Used);
    }

    [Fact]
    public void Daily_burns_are_differences_of_each_days_last_reading_with_gaps_spread()
    {
        var samples = new[] { At(2, 10), At(20, 30), At(26, 35), At(30, 70), At(80, 100) };
        var days = UsageAnalytics.DailyBurns(samples, Start, End, TimeZoneInfo.Utc);

        Assert.Equal([30d, 40d, 15d, 15d], days.Select(d => d.Burned));
        Assert.Equal([false, false, true, true], days.Select(d => d.Estimated));
        Assert.Equal(new DateOnly(2026, 11, 1), days[0].Day);
    }

    [Fact]
    public void Burned_within_window_uses_latest_sample_before_cutoff()
    {
        var samples = new[] { At(1, 10), At(20, 50), At(40, 60) };
        var current = At(50, 90);

        Assert.Equal(40, UsageAnalytics.BurnedWithin(samples, current, TimeSpan.FromHours(24))); // vs 50 at h20
        Assert.Equal(90, UsageAnalytics.BurnedWithin(samples, At(10, 90), TimeSpan.FromHours(24))); // window covers reset
    }

    [Fact]
    public void History_starting_mid_period_is_spread_from_reset()
    {
        var days = UsageAnalytics.DailyBurns([At(24 * 4 + 5, 400)], Start, End, TimeZoneInfo.Utc);
        Assert.Equal(5, days.Count);
        Assert.All(days, d => { Assert.Equal(80, d.Burned); Assert.True(d.Estimated); });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
