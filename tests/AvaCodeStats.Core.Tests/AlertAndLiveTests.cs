using AvaCodeStats.Core.Providers.Codex;

namespace AvaCodeStats.Core.Tests;

public class AlertAndLiveTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snap(double used, string metric = "m") =>
        new("test", "me", metric, "Metric", used, 100, false, Start, End, Start);

    private static (List<Alert> alerts, Dictionary<string, string> fired) Run(IEnumerable<UsageSnapshot> snaps, AppSettings s, DateTimeOffset now, Dictionary<string, string>? fired = null)
    {
        fired ??= [];
        var alerts = AlertRules.Evaluate(snaps, s, now, k => fired.GetValueOrDefault(k), (k, v) => fired[k] = v).ToList();
        return (alerts, fired);
    }

    [Fact]
    public void Jittered_reset_times_do_not_refire_but_a_new_period_does()
    {
        var s = new AppSettings { WarnAtPercent = 75, CriticalAtPercent = 90, NotifyWhenFarOverPace = false };
        var now = Start.AddDays(28);
        UsageSnapshot Jittered(double used, TimeSpan jitter) => Snap(used) with { PeriodStartUtc = Start + jitter, PeriodEndUtc = End + jitter };

        var (first, fired) = Run([Jittered(80, TimeSpan.FromMilliseconds(145))], s, now);
        Assert.Single(first);
        Assert.Empty(Run([Jittered(81, TimeSpan.FromMilliseconds(924))], s, now, fired).alerts);
        Assert.Empty(Run([Jittered(82, TimeSpan.FromSeconds(-1))], s, now, fired).alerts);
        Assert.Empty(Run([Jittered(83, UsageSnapshot.PeriodJitter)], s, now, fired).alerts);

        var next = Snap(80) with { PeriodStartUtc = End.AddMilliseconds(300), PeriodEndUtc = End.AddMonths(1) };
        Assert.Equal([AlertLevel.Warning], Run([next], s, End.AddDays(28), fired).alerts.Select(a => a.Level));
        Assert.Empty(Run([next with { PeriodStartUtc = End }], s, End.AddDays(28), fired).alerts);
    }

    [Fact]
    public void Legacy_exact_period_overload_still_fires_once()
    {
        var s = new AppSettings { WarnAtPercent = 75, CriticalAtPercent = 90, NotifyWhenFarOverPace = false };
        var fired = new HashSet<string>();
        Assert.Single(AlertRules.Evaluate([Snap(80)], s, Start.AddDays(28), fired.Contains, k => fired.Add(k)));
        Assert.Empty(AlertRules.Evaluate([Snap(81)], s, Start.AddDays(28), fired.Contains, k => fired.Add(k)));
    }

    [Fact]
    public void Warning_then_critical_fire_once_each_per_period()
    {
        var s = new AppSettings { WarnAtPercent = 75, CriticalAtPercent = 90, NotifyWhenFarOverPace = false };
        var now = Start.AddDays(28);

        var (first, fired) = Run([Snap(80)], s, now);
        Assert.Equal([AlertLevel.Warning], first.Select(a => a.Level));

        var (again, _) = Run([Snap(82)], s, now, fired);
        Assert.Empty(again);

        var (critical, _) = Run([Snap(95)], s, now, fired);
        Assert.Equal([AlertLevel.Critical], critical.Select(a => a.Level));

        // A new period starts over.
        var next = Snap(80) with { PeriodStartUtc = End, PeriodEndUtc = End.AddMonths(1) };
        Assert.Single(Run([next], s, End.AddDays(28), fired).alerts);
    }

    [Fact]
    public void Jumping_straight_to_critical_skips_the_warning()
    {
        var s = new AppSettings { WarnAtPercent = 75, CriticalAtPercent = 90, NotifyWhenFarOverPace = false };
        var (alerts, fired) = Run([Snap(95)], s, Start.AddDays(29));
        Assert.Equal([AlertLevel.Critical], alerts.Select(a => a.Level));
        Assert.Empty(Run([Snap(96)], s, Start.AddDays(29), fired).alerts);
    }

    [Fact]
    public void Far_over_pace_alerts_when_enabled()
    {
        var s = new AppSettings { WarnAtPercent = 0, CriticalAtPercent = 0, NotifyWhenFarOverPace = true };
        var (alerts, _) = Run([Snap(50)], s, Start.AddDays(5)); // 50% used, 17% of the month gone
        Assert.Equal([AlertLevel.FarOverPace], alerts.Select(a => a.Level));
    }

    [Fact]
    public void Tray_picks_the_quota_most_over_pace()
    {
        var store = new Storage.UsageStore(Path.Combine(Path.GetTempPath(), $"acs-{Guid.NewGuid():N}.db"));
        var collector = new UsageCollector(store, new Tokens.TokenStore(Path.Combine(Path.GetTempPath(), $"acs-{Guid.NewGuid():N}.db")));
        var now = DateTimeOffset.UtcNow;
        UsageSnapshot S(string metric, double used) =>
            new("test", "me", metric, metric, used, 100, false, now.AddDays(-15), now.AddDays(15), now);
        foreach (var s in new[] { S("calm", 20), S("hot", 90), S("mild", 55) }) store.Add(s);
        collector.LoadFromHistory();

        Assert.Equal("hot", collector.PickTray(null)!.MetricId);
        Assert.Equal("calm", collector.PickTray("test:me:calm")!.MetricId);
        Assert.Equal("mild", collector.PickTray(null, s => s.MetricId != "hot")!.MetricId);
    }

    [Fact]
    public void Codex_live_usage_parses_with_log_metric_ids()
    {
        const string json = """
            {"plan_type":"prolite","rate_limit":{"allowed":true,
              "primary_window":{"used_percent":20,"limit_window_seconds":604800,"reset_after_seconds":139201,"reset_at":1795532151},
              "secondary_window":{"used_percent":3,"limit_window_seconds":18000,"reset_at":1795400000}}}
            """;
        var limits = CodexProvider.ParseLive(json, "default", DateTimeOffset.UtcNow);

        Assert.Equal(["primary_10080", "secondary_300"], limits.Select(l => l.MetricId));
        Assert.Equal(["Weekly", "5-hour"], limits.Select(l => l.MetricName));
        Assert.Equal(20, limits[0].Used);
        Assert.Equal(TimeSpan.FromDays(7), limits[0].PeriodEndUtc - limits[0].PeriodStartUtc);
        Assert.Equal("ChatGPT prolite", limits[0].PlanName);
    }
}
