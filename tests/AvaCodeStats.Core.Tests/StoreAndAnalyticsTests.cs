using AvaCodeStats.Core.Storage;
using AvaCodeStats.Core.Tokens;

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

    [Fact]
    public void Burned_fraction_adds_up_across_a_reset()
    {
        var next = End;
        var samples = new[]
        {
            At(10, 100), At(30, 300),                       // before the window: baseline 300
            At(24 * 29, 700),                               // +400
            At(5, 50, next, next.AddMonths(1)),             // reset, then 50
            At(20, 250, next, next.AddMonths(1)),           // +200
        };
        var burned = UsageAnalytics.BurnedFraction(samples, Start.AddHours(40), next.AddHours(30));
        Assert.Equal(0.65, burned, 6); // (400 + 50 + 200) / 1000
    }

    [Fact]
    public void Burned_fraction_without_baseline_counts_a_period_starting_inside_the_window()
    {
        Assert.Equal(0.2, UsageAnalytics.BurnedFraction([At(10, 100), At(20, 200)], Start.AddHours(-5), Start.AddHours(30)), 6);
        // Period began before the window and no earlier reading: the first reading is only the baseline.
        Assert.Equal(0.1, UsageAnalytics.BurnedFraction([At(10, 100), At(20, 200)], Start.AddHours(5), Start.AddHours(30)), 6);
    }

    [Fact]
    public void Used_over_time_drops_to_zero_at_each_reset()
    {
        var next = End;
        var points = UsageAnalytics.UsedOverTime([At(10, 100), At(24 * 29, 700), At(5, 50, next, next.AddMonths(1))]);
        Assert.Equal([0.1, 0.7, 0.7, 0, 0.05], points.Select(p => Math.Round(p.Fraction, 6)));
        Assert.Equal(next, points[2].At);
        Assert.Equal(next, points[3].At);
    }

    [Fact]
    public void Jittery_reset_times_stay_one_period()
    {
        // Claude reports the reset with sub-second jitter on every read.
        UsageSnapshot J(double hours, double used, double jitterMs) =>
            At(hours, used, Start.AddMilliseconds(jitterMs), End.AddMilliseconds(jitterMs));
        var samples = new[] { J(1, 100, 145), J(2, 100, 924), J(3, 200, 413), J(4, 300, 54) };

        Assert.Equal(0.2, UsageAnalytics.BurnedFraction(samples, Start.AddHours(1.5), Start.AddHours(5)), 6);
        Assert.DoesNotContain(UsageAnalytics.UsedOverTime(samples), p => p.Fraction == 0);
        Assert.Equal(200, UsageAnalytics.BurnedWithin(samples, J(5, 300, 7), TimeSpan.FromHours(3.5)));

        var store = new UsageStore(_db);
        foreach (var s in samples) store.Add(s);
        var period = Assert.Single(store.GetPeriodSummaries(samples[0].Key));
        Assert.Equal(300, period.Used);
    }

    [Fact]
    public void Pace_over_time_falls_between_readings_and_restarts_at_a_reset()
    {
        // 30-day period, 1000 requests: day 3 used 100 (10%), day 15 used 300, then 50 on day 1 of the next.
        var next = End;
        var points = UsageAnalytics.PaceOverTime(
            [At(24 * 3, 100), At(24 * 15, 300), At(24, 50, next, next.AddMonths(1))], nowUtc: next.AddDays(2));
        var deltas = points.Select(p => Math.Round(p.Delta, 4)).ToArray();

        Assert.Equal(0, deltas[0], 4);                       // 10% used, 10% of the month gone
        Assert.Equal(Math.Round(0.1 - 0.5, 4), deltas[1]);   // day 15, before the reading
        Assert.Equal(Math.Round(0.3 - 0.5, 4), deltas[2]);   // day 15, after it
        Assert.Equal(-0.7, deltas[3]);                       // end of November: 30% used of 100% gone
        Assert.Equal(0, deltas[4]);                          // reset
        Assert.Equal(next.AddDays(2), points[^1].At);        // extended to now
        Assert.True(deltas[^1] < deltas[^2]);
    }

    [Fact]
    public void Time_average_weights_by_time_and_clips_to_the_range()
    {
        var t0 = Start;
        var line = new[] { (t0, 0.0), (t0.AddHours(10), 1.0), (t0.AddHours(20), 1.0) };
        Assert.Equal(0.75, UsageAnalytics.TimeAverage(line, t0, t0.AddHours(20))!.Value, 6);
        Assert.Equal(1.0, UsageAnalytics.TimeAverage(line, t0.AddHours(10), t0.AddHours(40))!.Value, 6);
        Assert.Null(UsageAnalytics.TimeAverage(line, t0.AddHours(30), t0.AddHours(40)));
    }

    [Fact]
    public void Token_events_filter_by_source_in_sql()
    {
        var tokens = new TokenStore(_db);
        TokenEvent E(string source, int i) => new(source, $"e{i}", Start.AddHours(i), "s", "m", 1, 2, 3, 4, 5);
        tokens.Upsert([E("a:1", 1), E("b:1", 2), E("c:1", 3), E("a:1", 4)]);

        var subset = tokens.GetEvents(Start, End, new HashSet<string> { "a:1", "c:1" });
        Assert.Equal(["a:1", "c:1", "a:1"], subset.Select(e => e.Source));
        Assert.Equal(5, subset[0].Output);
        Assert.Empty(tokens.GetEvents(Start, End, new HashSet<string>()));
        Assert.Equal(4, tokens.GetEvents(Start, End).Count);
    }

    [Fact]
    public void Scan_reads_large_appends_in_chunks_and_keeps_a_partial_last_line()
    {
        var log = _db + ".jsonl";
        try
        {
            var tokens = new TokenStore(_db) { ScanChunkBytes = 8 };
            var seen = new List<string>();
            string Collect(IEnumerable<string> lines, string? ctx)
            {
                seen.AddRange(lines);
                return (int.Parse(ctx ?? "0") + 1).ToString();
            }

            File.WriteAllText(log, "one\na-line-longer-than-a-chunk\nthree\npart");
            tokens.ScanAppended(log, Collect);
            Assert.Equal(["one", "a-line-longer-than-a-chunk", "three"], seen);

            seen.Clear();
            File.AppendAllText(log, "ial\n");
            string? context = null;
            tokens.ScanAppended(log, (lines, ctx) => { context = ctx; return Collect(lines, ctx); });
            Assert.Equal(["partial"], seen);
            Assert.NotNull(context); // carried over from the earlier calls

            seen.Clear();
            tokens.ScanAppended(log, Collect);
            Assert.Empty(seen);
        }
        finally
        {
            File.Delete(log);
        }
    }

    private sealed class FakeProvider(string account, Func<int, IReadOnlyList<UsageSnapshot>> fetch) : Providers.IUsageProvider
    {
        private int _calls;
        public string ProviderId => "test";
        public string AccountId => account;
        public string DisplayName => "Test";

        public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
        {
            await Task.Yield();
            return fetch(Interlocked.Increment(ref _calls));
        }
    }

    [Fact]
    public async Task Collector_keeps_good_providers_when_one_fails_and_clears_the_error_later()
    {
        var failing = true;
        var collector = new UsageCollector(new UsageStore(_db), new TokenStore(_db))
        {
            Providers =
            [
                new FakeProvider("ok", _ => [At(1, 10) with { AccountId = "ok" }]),
                new FakeProvider("bad", _ => failing ? throw new InvalidOperationException("boom") : []),
            ],
        };

        await collector.CollectAsync();
        Assert.Single(collector.Latest);
        Assert.Equal("ok", collector.Latest.Values.Single().AccountId);
        Assert.Equal("boom", collector.Errors["test:bad"]);
        Assert.NotNull(collector.LastCollectedUtc);

        failing = false;
        await collector.CollectAsync();
        Assert.Empty(collector.Errors);
    }

    [Fact]
    public async Task Collector_can_be_read_while_a_poll_writes()
    {
        IReadOnlyList<UsageSnapshot> Batch(int call) =>
            Enumerable.Range(0, 150).Select(i => At(1, i) with { MetricId = $"m{call}-{i}" }).ToList();
        var collector = new UsageCollector(new UsageStore(_db), new TokenStore(_db))
        {
            Providers = [new FakeProvider("a", Batch), new FakeProvider("b", _ => throw new InvalidOperationException("x"))],
        };

        using var done = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!done.IsCancellationRequested || reads == 0)
            {
                _ = collector.Latest.Values.Sum(s => s.Used);
                foreach (var kv in collector.Latest) _ = kv.Value.MetricId;
                _ = collector.Errors.Count + collector.Errors.Values.Count();
                _ = collector.PickTray(null);
                reads++;
            }
            return reads;
        });

        for (var i = 0; i < 3; i++) await collector.CollectAsync();
        done.Cancel();
        Assert.True(await reader > 0);
        Assert.Equal(450, collector.Latest.Count);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
