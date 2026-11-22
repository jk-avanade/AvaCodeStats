using AvaCodeStats.Core.Providers.GitHubCopilot;

namespace AvaCodeStats.Core.Tests;

public class CopilotQuotaParserTests
{
    // Shape of GET https://api.github.com/copilot_internal/user (trimmed).
    private const string Json = """
        {
          "login": "octocat",
          "access_type_sku": "free_ms_mvp_quota",
          "copilot_plan": "individual_max",
          "quota_reset_date": "2026-12-01",
          "quota_reset_date_utc": "2026-12-01T00:00:00Z",
          "quota_snapshots": {
            "chat": { "entitlement": 0, "quota_remaining": 0.0, "remaining": 0, "unlimited": true, "overage_count": 0,
                      "timestamp_utc": "2026-11-22T23:26:57.825Z" },
            "completions": { "entitlement": 0, "quota_remaining": 0.0, "remaining": 0, "unlimited": true, "overage_count": 0,
                      "timestamp_utc": "2026-11-22T23:26:57.825Z" },
            "premium_interactions": { "entitlement": 20000, "quota_remaining": 2264.6, "remaining": 2264,
                      "unlimited": false, "overage_count": 0, "percent_remaining": 11.3,
                      "timestamp_utc": "2026-11-22T23:26:57.825Z" }
          }
        }
        """;

    [Fact]
    public void Parses_limited_quotas_only_by_default()
    {
        var result = CopilotQuotaParser.Parse(Json, "fallback", DateTimeOffset.UtcNow);

        var s = Assert.Single(result);
        Assert.Equal("github-copilot", s.ProviderId);
        Assert.Equal("octocat", s.AccountId);
        Assert.Equal("premium_interactions", s.MetricId);
        Assert.Equal("Premium requests", s.MetricName);
        Assert.Equal(20000, s.Entitlement);
        Assert.Equal(17735.4, s.Used, 6);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), s.PeriodEndUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), s.PeriodStartUtc);
        Assert.Equal("individual_max / free_ms_mvp_quota", s.PlanName);
    }

    [Fact]
    public void Can_include_unlimited_quotas()
    {
        var result = CopilotQuotaParser.Parse(Json, "fallback", DateTimeOffset.UtcNow, includeUnlimited: true);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Falls_back_to_first_of_next_month_without_reset_date()
    {
        const string json = """{ "quota_snapshots": { "chat": { "entitlement": 50, "quota_remaining": 40 } } }""";
        var s = Assert.Single(CopilotQuotaParser.Parse(json, "me", new DateTimeOffset(2026, 2, 14, 5, 0, 0, TimeSpan.Zero)));

        Assert.Equal("me", s.AccountId);
        Assert.Equal(10, s.Used);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), s.PeriodEndUtc);
    }
}
