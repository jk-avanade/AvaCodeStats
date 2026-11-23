using System.Windows.Media;
using AvaCodeStats.Core;
using AvaCodeStats.Core.Providers.GitHubCopilot;
using AvaCodeStats.Theming;
using AvaCodeStats.Tray;

namespace AvaCodeStats.Views;

/// <summary>One quota, ready to show (Limits page and summary popup).</summary>
internal sealed class LimitItem(UsageSnapshot snapshot, PaceInfo pace, DateTimeOffset now)
{
    public UsageSnapshot Snapshot { get; } = snapshot;
    public PaceInfo Pace { get; } = pace;
    public SubscriptionKey Key => Snapshot.Key;

    public string Provider => ProviderFactory.DisplayName(Snapshot.ProviderId);
    public string Title => Snapshot.MetricName;
    /// <summary>Set when the list holds several accounts of this provider, so rows can be told apart.</summary>
    public bool ShowAccount { get; set; }
    public string ShortTitle => $"{Provider} · {Snapshot.MetricName}" + (ShowAccount ? $" ({Snapshot.AccountId})" : "");
    public double LeftFraction => 1 - Math.Clamp(Snapshot.UsedFraction, 0, 1);
    public double TimeLeftFraction => 1 - Pace.ElapsedFraction;
    public string LeftPercent => $"{LeftFraction * 100:0}%";
    public PaceStatus Status => Pace.Status;

    /// <summary>Counted quotas (Copilot) show counts; percentage-only limits (Claude, Codex) don't.</summary>
    public string Usage => Snapshot.ProviderId == CopilotQuotaParser.ProviderId || Snapshot.Entitlement != 100
        ? $"{Snapshot.Used:N0} of {Snapshot.Entitlement:N0} used"
        : $"{Snapshot.Used:0.#}% used";

    public string PaceText => PaceCalculator.Describe(Pace);
    public string ResetText => $"Resets in {Fmt.Countdown(Snapshot.PeriodEndUtc - now)}";
    public string ResetShort => $"↻ {Fmt.Countdown(Snapshot.PeriodEndUtc - now)}";
    public string ResetTooltip => $"Resets {Snapshot.PeriodEndUtc.ToLocalTime():dddd d MMM HH:mm}";

    public Brush StatusBrush => ThemeService.Brush(ThemeService.Current.ForStatus(Status));
    public Brush ProviderBrush => ThemeService.Brush(ThemeService.Current.ForProvider(Snapshot.ProviderId));

    public string StatusGlyph => Status switch
    {
        PaceStatus.UnderPace or PaceStatus.OnPace => Glyphs.Check,
        PaceStatus.SlightlyOver => Glyphs.Warning,
        PaceStatus.FarOver or PaceStatus.Exhausted => Glyphs.Error,
        _ => Glyphs.Info,
    };

    public static IReadOnlyList<LimitItem> For(AppHost host, string profile)
    {
        var now = DateTimeOffset.UtcNow;
        var s = host.Settings;
        return host.Collector.Latest.Values
            .Where(x => !x.Unlimited && x.Entitlement > 0 && s.InProfile(profile, x.ProviderId, x.AccountId)
                        && s.Subscriptions.Any(c => c.Enabled && c.Provider == x.ProviderId && c.Account == x.AccountId))
            .OrderBy(x => x.ProviderId).ThenBy(x => x.AccountId)
            .ThenBy(x => x.PeriodEndUtc - x.PeriodStartUtc).ThenBy(x => x.MetricName)
            .Select(x => new LimitItem(x, PaceCalculator.Calculate(x, now, s.Thresholds), now))
            .ToList()
            .With(list =>
            {
                var multi = list.GroupBy(i => i.Snapshot.ProviderId).Where(g => g.Select(i => i.Snapshot.AccountId).Distinct().Count() > 1)
                    .Select(g => g.Key).ToHashSet();
                foreach (var i in list) i.ShowAccount = multi.Contains(i.Snapshot.ProviderId);
            });
    }
}

/// <summary>Quotas of one provider account.</summary>
internal sealed class LimitGroup(string providerId, string account, string? plan, string? profile, IReadOnlyList<LimitItem> items)
{
    public string Name { get; } = ProviderFactory.DisplayName(providerId) + (account == "default" ? "" : $" · {account}");
    public string Meta { get; } = string.Join("  ·  ", new[] { plan, profile }.Where(x => !string.IsNullOrEmpty(x)));
    public Brush Dot { get; } = ThemeService.Brush(ThemeService.Current.ForProvider(providerId));
    public IReadOnlyList<LimitItem> Items { get; } = items;

    public static IReadOnlyList<LimitGroup> From(IEnumerable<LimitItem> items, AppSettings settings, bool showProfile) =>
        items.GroupBy(i => (i.Snapshot.ProviderId, i.Snapshot.AccountId))
            .Select(g => new LimitGroup(g.Key.ProviderId, g.Key.AccountId, g.First().Snapshot.PlanName,
                showProfile ? Profiles.Label(settings.ProfileOf(g.Key.ProviderId, g.Key.AccountId)) : null, g.ToList()))
            .ToList();
}
