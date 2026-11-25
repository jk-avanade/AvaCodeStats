namespace AvaCodeStats.Core;

/// <summary>
/// One reading of one quota (metric) of one subscription account, at one point in time.
/// Provider-agnostic: every subscription type (GitHub Copilot, and later others) maps into this.
/// </summary>
public sealed record UsageSnapshot(
    string ProviderId,
    string AccountId,
    string MetricId,
    string MetricName,
    double Used,
    double Entitlement,
    bool Unlimited,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    DateTimeOffset TimestampUtc,
    string? PlanName = null)
{
    public SubscriptionKey Key => new(ProviderId, AccountId, MetricId);

    public double Remaining => Math.Max(0, Entitlement - Used);

    public double UsedFraction => Entitlement <= 0 ? 0 : Used / Entitlement;

    /// <summary>
    /// Providers report the reset time with jitter (Claude by fractions of a second, Codex by a second), so two
    /// readings belong to the same period when their starts are this close.
    /// </summary>
    public static readonly TimeSpan PeriodJitter = TimeSpan.FromMinutes(2);

    public bool SamePeriodAs(UsageSnapshot other) => SamePeriod(PeriodStartUtc, other.PeriodStartUtc);

    public static bool SamePeriod(DateTimeOffset a, DateTimeOffset b) => (a - b).Duration() <= PeriodJitter;

    /// <summary>True when this reading is in a later period than <paramref name="earlier"/> (a reset happened between them).</summary>
    public bool IsLaterPeriodThan(UsageSnapshot earlier) => PeriodStartUtc - earlier.PeriodStartUtc > PeriodJitter;
}

/// <summary>Identifies one tracked quota: provider + account + metric.</summary>
public readonly record struct SubscriptionKey(string ProviderId, string AccountId, string MetricId)
{
    public override string ToString() => $"{ProviderId}:{AccountId}:{MetricId}";

    public static SubscriptionKey? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(':');
        return parts.Length == 3 ? new SubscriptionKey(parts[0], parts[1], parts[2]) : null;
    }
}
