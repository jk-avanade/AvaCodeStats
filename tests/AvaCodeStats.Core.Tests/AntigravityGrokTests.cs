using System.Text;
using AvaCodeStats.Core.Providers.Antigravity;
using AvaCodeStats.Core.Providers.GrokBot;

namespace AvaCodeStats.Core.Tests;

public class AntigravityGrokTests
{
    [Fact]
    public void Antigravity_quota_groups_become_limits()
    {
        // Shape of `agy -p "/quota" --output-format json` (trimmed).
        const string json = """
            {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
              {"name":"Gemini Models","buckets":[
                {"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.8987900018692017,"reset_time":"2026-11-28T14:27:01Z"},
                {"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":1,"reset_time":"2026-11-23T06:07:43Z"}]},
              {"name":"Claude and GPT models","buckets":[
                {"id":"3p-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":1,"reset_time":"2026-11-30T01:07:43Z"}]}]}}}
            """;
        var limits = AntigravityProvider.Parse(json, "default", DateTimeOffset.UtcNow);

        Assert.Equal(["Gemini · Weekly", "Gemini · 5-hour", "Claude & GPT · Weekly"], limits.Select(l => l.MetricName));
        Assert.Equal("gemini-weekly", limits[0].MetricId);
        Assert.Equal(10.12, limits[0].Used, 2);
        Assert.Equal(TimeSpan.FromDays(7), limits[0].PeriodEndUtc - limits[0].PeriodStartUtc);
        Assert.Equal(TimeSpan.FromHours(5), limits[1].PeriodEndUtc - limits[1].PeriodStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 28, 14, 27, 1, TimeSpan.Zero), limits[0].PeriodEndUtc);
    }

    [Fact]
    public void Antigravity_without_groups_is_an_error()
    {
        Assert.Throws<InvalidOperationException>(() => AntigravityProvider.Parse("""{"status":"SUCCESS"}""", "default", DateTimeOffset.UtcNow));
    }

    private const string GrokCache = """
        {"schemaVersion":3,"value":{"kind":"present","selectedTeamId":null,
         "reading":{"usage":{"percentUsed":30.62,"nextResetMs":1795575886908,"grokPlanLabel":"Grok Bot Plan","unifiedPlanName":"Super"},
          "readAtMs":1795394661796},"expiresAtMs":1795481064022}}
        """;

    [Fact]
    public void Grok_cache_reading_parses()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1795394661796).AddHours(1);
        var s = GrokBotProvider.Parse(GrokCache, "default", now)!;

        Assert.Equal(30.62, s.Used);
        Assert.Equal(100, s.Entitlement);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795575886908), s.PeriodEndUtc);
        Assert.Equal(TimeSpan.FromDays(7), s.PeriodEndUtc - s.PeriodStartUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795394661796), s.TimestampUtc);
        Assert.Equal("Grok Bot Super", s.PlanName);
    }

    [Fact]
    public void Grok_cache_after_its_reset_counts_as_a_fresh_week()
    {
        var after = DateTimeOffset.FromUnixTimeMilliseconds(1795575886908).AddDays(1);
        var s = GrokBotProvider.Parse(GrokCache, "default", after)!;
        Assert.Equal(0, s.Used);
        Assert.True(s.PeriodEndUtc > after);
    }

    [Fact]
    public void Blob_names_decode_from_lowercase_base32()
    {
        var key = "sand.client.slice.account.0.weekly-usage.cache";
        var encoded = ToBase32(Encoding.UTF8.GetBytes(key));
        Assert.Equal(key, GrokBotProvider.DecodeKey(encoded));
        Assert.Null(GrokBotProvider.DecodeKey("not base32!"));
    }

    private static string ToBase32(byte[] data)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { bits -= 5; sb.Append(alphabet[(buffer >> bits) & 31]); }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
