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
    /// <summary>First row of its group's card: no divider above it.</summary>
    public bool IsFirst { get; set; }
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
            .ToList();
    }
}

/// <summary>
/// Quotas of one provider account, shown as one card. Groups and rows keep the order of the items given.
/// Folded, the card shows only the most pressing quota: worst pace, then least left, then soonest reset.
/// </summary>
internal sealed class LimitGroup
{
    private LimitGroup(AppSettings settings, string providerId, string account, string? plan, string? profile, IReadOnlyList<LimitItem> items, bool collapsed)
    {
        Key = $"{providerId}:{account}";
        Name = ProviderFactory.DisplayName(providerId) + Privacy.Suffix(settings, providerId, account);
        Meta = string.Join("  ·  ", new[] { plan, profile }.Where(x => !string.IsNullOrEmpty(x)));
        Dot = ThemeService.Brush(ThemeService.Current.ForProvider(providerId));
        Items = items;
        CanCollapse = items.Count > 1;
        IsCollapsed = collapsed && CanCollapse;
        Rows = IsCollapsed ? [Worst(items)] : items;
        for (var i = 0; i < Rows.Count; i++) Rows[i].IsFirst = i == 0;
    }

    /// <summary>"provider:account", as stored in the collapsed-group settings.</summary>
    public string Key { get; }
    public string Name { get; }
    public string Meta { get; }
    public Brush Dot { get; }
    public IReadOnlyList<LimitItem> Items { get; }
    /// <summary>What the card shows: every quota, or only the most pressing one when folded.</summary>
    public IReadOnlyList<LimitItem> Rows { get; }
    public bool CanCollapse { get; }
    public bool IsCollapsed { get; }
    public string Chevron => IsCollapsed ? Glyphs.ChevronRight : Glyphs.ChevronDown;
    public string ToggleTip => IsCollapsed ? $"Show all {Items.Count} limits" : "Show only the most pressing limit";
    public string HiddenText => IsCollapsed ? $"+{Items.Count - 1} more" : "";

    private static LimitItem Worst(IEnumerable<LimitItem> items) => items
        .OrderByDescending(i => AlertRules.Severity(i.Status))
        .ThenBy(i => i.LeftFraction)
        .ThenBy(i => i.Snapshot.PeriodEndUtc)
        .First();

    public static IReadOnlyList<LimitGroup> From(IEnumerable<LimitItem> items, AppSettings settings, bool showProfile, ICollection<string> collapsed) =>
        items.GroupBy(i => (i.Snapshot.ProviderId, i.Snapshot.AccountId))
            .Select(g => new LimitGroup(settings, g.Key.ProviderId, g.Key.AccountId, g.First().Snapshot.PlanName,
                showProfile ? Profiles.Label(settings.ProfileOf(g.Key.ProviderId, g.Key.AccountId)) : null, g.ToList(),
                collapsed.Contains($"{g.Key.ProviderId}:{g.Key.AccountId}")))
            .ToList();

    /// <summary>Folds or unfolds the group in <paramref name="collapsed"/> (a settings list).</summary>
    public void Toggle(List<string> collapsed)
    {
        if (!collapsed.Remove(Key)) collapsed.Add(Key);
    }
}
