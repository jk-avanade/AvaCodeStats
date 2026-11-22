using AvaCodeStats.Core.Providers.Claude;
using AvaCodeStats.Core.Providers.Codex;
using AvaCodeStats.Core.Tokens;

namespace AvaCodeStats.Core.Tests;

public class ToolLogTests
{
    [Fact]
    public void Claude_log_lines_become_token_events()
    {
        string[] lines =
        [
            """{"type":"user","message":{"role":"user","content":"hi"}}""",
            """{"type":"assistant","sessionId":"s1","timestamp":"2026-11-22T23:26:46.029Z","requestId":"r1","message":{"id":"msg_1","model":"claude-opus-5-5","usage":{"input_tokens":2,"cache_read_input_tokens":31825,"cache_creation_input_tokens":17786,"cache_creation":{"ephemeral_1h_input_tokens":17786,"ephemeral_5m_input_tokens":0},"output_tokens":594,"speed":"standard"}}}""",
            """{"type":"assistant","timestamp":"2026-11-22T23:27:00Z","message":{"id":"msg_2","model":"<synthetic>","usage":{"input_tokens":0,"output_tokens":0}}}""",
        ];

        var e = Assert.Single(ClaudeCodeProvider.ParseLogLines(lines, "claude-code:default"));
        Assert.Equal("msg_1", e.EventId);
        Assert.Equal("s1", e.SessionId);
        Assert.Equal(2, e.Input);
        Assert.Equal(31825, e.CacheRead);
        Assert.Equal(17786, e.CacheWrite1h);
        Assert.Equal(594, e.Output);
    }

    [Fact]
    public void Claude_limits_parse_weekly_and_model_scoped()
    {
        const string json = """
            {"five_hour":null,
             "limits":[
               {"kind":"weekly_all","group":"weekly","percent":6,"resets_at":"2026-11-29T10:00:00.694387+10:00"},
               {"kind":"weekly_scoped","group":"weekly","percent":0,"resets_at":"2026-11-29T10:00:00+10:00","scope":{"model":{"display_name":"Fable"}}}]}
            """;
        var limits = ClaudeCodeProvider.ParseLimits(json, "default", "max", DateTimeOffset.UtcNow);

        Assert.Equal(["Weekly", "Weekly · Fable"], limits.Select(l => l.MetricName));
        Assert.Equal(6, limits[0].Used);
        Assert.Equal(100, limits[0].Entitlement);
        Assert.Equal(TimeSpan.FromDays(7), limits[0].PeriodEndUtc - limits[0].PeriodStartUtc);
        Assert.Equal("Claude max", limits[0].PlanName);
    }

    [Fact]
    public void Codex_log_lines_give_tokens_model_and_latest_rate_limits()
    {
        string[] lines =
        [
            """{"timestamp":"2026-11-19T00:05:13.703Z","type":"session_meta","payload":{"id":"sess-1"}}""",
            """{"timestamp":"2026-11-19T00:06:00Z","type":"turn_context","payload":{"model":"gpt-6-astra"}}""",
            """{"timestamp":"2026-11-19T00:11:23.206Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":2405307},"last_token_usage":{"input_tokens":112678,"cached_input_tokens":111872,"cache_write_input_tokens":0,"output_tokens":190}},"rate_limits":{"primary":{"used_percent":11.0,"window_minutes":10080,"resets_at":1795532151},"plan_type":"prolite"}}}""",
        ];
        var state = CodexProvider.ParseState.From(null, "file-name");
        string? limits = null;

        var e = Assert.Single(CodexProvider.ParseLogLines(lines, "codex:default", state, ref limits));
        Assert.Equal("sess-1:2405307", e.EventId);
        Assert.Equal("gpt-6-astra", e.Model);
        Assert.Equal(806, e.Input);          // uncached only
        Assert.Equal(111872, e.CacheRead);
        Assert.Equal("sess-1|gpt-6-astra", state.ToString());

        Assert.NotNull(limits);
        var now = DateTimeOffset.FromUnixTimeSeconds(1795532151).AddDays(-2);
        var weekly = Assert.Single(CodexProvider.ParseLimits(limits!, "default", now));
        Assert.Equal("Weekly", weekly.MetricName);
        Assert.Equal(11, weekly.Used);
        Assert.Equal("ChatGPT prolite", weekly.PlanName);

        // After the window ends without a newer reading, usage counts as reset.
        var later = CodexProvider.ParseLimits(limits!, "default", now.AddDays(3)).Single();
        Assert.Equal(0, later.Used);
        Assert.True(later.PeriodEndUtc > now.AddDays(3));
    }

    [Fact]
    public void Pricing_uses_longest_prefix_cache_rates_and_fast_mode()
    {
        var prices = new PriceTable();
        Assert.Equal(4, prices.Find("claude-opus-5-5")!.Input);
        Assert.Equal(5, prices.Find("claude-opus-5")!.Input);
        Assert.Null(prices.Find("gpt-6-astra"));

        var e = new TokenEvent("claude-code:me", "1", DateTimeOffset.UtcNow, "s", "claude-opus-5-5",
            Input: 1_000_000, CacheRead: 1_000_000, CacheWrite5m: 1_000_000, CacheWrite1h: 1_000_000, Output: 1_000_000);
        var c = prices.Cost(e);
        Assert.Equal(4, c.Input, 6);
        Assert.Equal(0.20, c.CacheRead, 6);
        Assert.Equal(5 + 8, c.CacheWrite, 6); // 1.25× and 2× input
        Assert.Equal(20, c.Output, 6);
        Assert.Equal(3.80, c.CacheSavings, 6);
        Assert.Equal(2 * c.Total, prices.Cost(e with { Speed = "fast" }).Total, 6);

        var custom = new PriceTable([new ModelPrice { Model = "gpt-6", Input = 1, Output = 2, CacheRead = 0.1 }]);
        Assert.True(custom.Cost(e with { Model = "gpt-6-astra" }).Priced);
    }

    [Fact]
    public void Report_groups_by_provider_model_and_day()
    {
        var day = new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);
        TokenEvent E(string source, string model, string session, int hours, long output) =>
            new(source, Guid.NewGuid().ToString(), day.AddHours(hours), session, model, 0, 0, 0, 0, output);
        var events = new[]
        {
            E("claude-code:a", "claude-opus-5-5", "s1", 0, 1_000_000),
            E("claude-code:a", "claude-opus-5-5", "s1", 1, 1_000_000),
            E("codex:a", "gpt-6-astra", "s2", 30, 500),
        };

        var r = TokenReport.Build(events, new PriceTable(), day.AddDays(-1), day.AddDays(3), TimeSpan.FromDays(1), TimeZoneInfo.Utc);
        Assert.Equal(2, r.Sessions);
        Assert.Equal(40, r.Totals.Cost.Total, 6);
        Assert.Equal(["claude-code", "codex"], r.ByProvider.Select(p => p.Key));
        Assert.Equal(["gpt-6-astra"], r.UnpricedModels);
        Assert.Equal(5, r.Buckets.Count); // 19–23 Nov: the range ends at 03:00 on the 23rd
        Assert.Equal(2_000_000, r.Buckets[1].ByProvider["claude-code"].Output);
        Assert.Equal(500, r.Buckets[2].ByProvider["codex"].Output);
    }
}
