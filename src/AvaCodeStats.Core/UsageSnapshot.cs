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
