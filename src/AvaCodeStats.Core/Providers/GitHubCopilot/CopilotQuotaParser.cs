using System.Globalization;
using System.Text.Json;

namespace AvaCodeStats.Core.Providers.GitHubCopilot;

/// <summary>Parses the response of <c>GET https://api.github.com/copilot_internal/user</c>.</summary>
public static class CopilotQuotaParser
{
    public const string ProviderId = "github-copilot";

    private static readonly Dictionary<string, string> MetricNames = new()
    {
        ["premium_interactions"] = "Premium requests",
        ["chat"] = "Chat",
        ["completions"] = "Code completions",
    };

    /// <param name="includeUnlimited">Also return quotas that are unlimited or have no entitlement.</param>
    public static IReadOnlyList<UsageSnapshot> Parse(string json, string account, DateTimeOffset now, bool includeUnlimited = false)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var login = Str(root, "login") ?? account;
        var plan = string.Join(" / ", new[] { Str(root, "copilot_plan"), Str(root, "access_type_sku") }
            .Where(s => !string.IsNullOrEmpty(s)));

        var periodEnd = ResetDate(root, now);
        var periodStart = periodEnd.AddMonths(-1);

        if (!root.TryGetProperty("quota_snapshots", out var quotas) || quotas.ValueKind != JsonValueKind.Object)
            return [];

        var result = new List<UsageSnapshot>();
        foreach (var q in quotas.EnumerateObject())
        {
            var v = q.Value;
            if (v.ValueKind != JsonValueKind.Object) continue;

            var unlimited = v.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True;
            var entitlement = Num(v, "entitlement");
            if (!includeUnlimited && (unlimited || entitlement <= 0)) continue;

            // quota_remaining keeps decimals (token-based billing), remaining is rounded.
            var remaining = v.TryGetProperty("quota_remaining", out _) ? Num(v, "quota_remaining") : Num(v, "remaining");
            var used = Math.Max(0, entitlement - remaining) + Num(v, "overage_count");

            var ts = v.TryGetProperty("timestamp_utc", out var t) && t.TryGetDateTimeOffset(out var parsed) ? parsed : now;

            result.Add(new UsageSnapshot(
                ProviderId, login, q.Name,
                MetricNames.GetValueOrDefault(q.Name, q.Name),
                Math.Round(used, 2), entitlement, unlimited,
                periodStart, periodEnd, ts, plan.Length > 0 ? plan : null));
        }
        return result;
    }

    private static DateTimeOffset ResetDate(JsonElement root, DateTimeOffset now)
    {
        if (Str(root, "quota_reset_date_utc") is { } utc
            && DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r1))
            return r1.ToUniversalTime();

        if (Str(root, "quota_reset_date") is { } date
            && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // Copilot resets on the 1st of each month (UTC).
        var u = now.ToUniversalTime();
        return new DateTimeOffset(u.Year, u.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
