using System.Net.Http.Headers;
using System.Text.Json;
using AvaCodeStats.Core.Tokens;
using static AvaCodeStats.Core.Providers.JsonHelpers;

namespace AvaCodeStats.Core.Providers.Claude;

/// <summary>
/// Claude (Pro/Max) usage:
/// - limits from the endpoint Claude Code's /usage uses, with the sign-in Claude Code keeps in .credentials.json
///   (read-only: this app never refreshes the token, so it cannot disturb Claude Code's own login);
/// - tokens from Claude Code's session logs (projects/**/*.jsonl).
/// </summary>
public sealed class ClaudeCodeProvider(string account, string claudeHome, TokenStore tokens, HttpClient http) : IUsageProvider
{
    public const string Id = "claude-code";
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");

    public string ProviderId => Id;
    public string AccountId => account;
    public string DisplayName => "Claude";
    public string Source => $"{Id}:{account}";

    public static string DefaultHome =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        await Task.Run(ScanLogs, ct);

        var (token, plan) = ReadCredentials();
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.UserAgent.ParseAdd("AvaCodeStats/0.1");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Claude sign-in expired: use Claude Code once to refresh it.");
        res.EnsureSuccessStatusCode();
        return ParseLimits(await res.Content.ReadAsStringAsync(ct), account, plan, DateTimeOffset.UtcNow);
    }

    private (string token, string? plan) ReadCredentials()
    {
        var path = Path.Combine(claudeHome, ".credentials.json");
        if (!File.Exists(path)) throw new InvalidOperationException("Claude Code is not signed in on this machine.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o) || o.GetProperty("accessToken").GetString() is not { } token)
            throw new InvalidOperationException("Claude Code is not signed in with a Claude subscription.");
        if (o.TryGetProperty("expiresAt", out var exp) && exp.TryGetInt64(out var ms)
            && DateTimeOffset.FromUnixTimeMilliseconds(ms) < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Claude sign-in expired: use Claude Code once to refresh it.");
        var plan = o.TryGetProperty("subscriptionType", out var s) ? s.GetString() : null;
        return (token, plan);
    }

    public static IReadOnlyList<UsageSnapshot> ParseLimits(string json, string account, string? plan, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new List<UsageSnapshot>();
        var planName = plan is null ? null : $"Claude {plan}";

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in limits.EnumerateArray())
            {
                if (!TryDate(l, "resets_at", out var reset)) continue;
                var kind = Str(l, "kind") ?? "limit";
                var group = Str(l, "group") ?? "";
                var model = l.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                    && scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object
                    ? Str(m, "display_name") : null;
                var window = group is "weekly" ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5);
                var name = (group is "weekly" ? "Weekly" : group is "" ? kind : "5-hour") + (model is null ? "" : $" · {model}");
                var id = kind + (model is null ? "" : "_" + model.ToLowerInvariant());
                result.Add(new UsageSnapshot(Id, account, id, name, Num(l, "percent"), 100, false,
                    reset - window, reset, now, planName));
            }
        }

        // Rolling 5-hour session limit, when the plan has one.
        if (root.TryGetProperty("five_hour", out var five) && five.ValueKind == JsonValueKind.Object
            && TryDate(five, "resets_at", out var fiveReset) && result.All(r => r.MetricName != "5-hour"))
        {
            result.Add(new UsageSnapshot(Id, account, "five_hour", "5-hour", Num(five, "utilization"), 100, false,
                fiveReset - TimeSpan.FromHours(5), fiveReset, now, planName));
        }
        return result;
    }

    // ---------- session logs ----------

    private void ScanLogs()
    {
        var projects = Path.Combine(claudeHome, "projects");
        if (!Directory.Exists(projects)) return;
        foreach (var file in Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories))
        {
            try { tokens.ScanAppended(file, (lines, _) => { tokens.Upsert(ParseLogLines(lines, Source).ToList()); return null; }); }
            catch (IOException) { /* file busy or removed mid-scan: next poll picks it up */ }
        }
    }

    public static IEnumerable<TokenEvent> ParseLogLines(IEnumerable<string> lines, string source)
    {
        foreach (var line in lines)
        {
            if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
            TokenEvent? e = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (Str(r, "type") != "assistant" || !r.TryGetProperty("message", out var msg)) continue;
                if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) continue;
                var model = Str(msg, "model");
                if (model is null || model.StartsWith('<')) continue; // "<synthetic>" placeholder messages
                if (!TryDate(r, "timestamp", out var ts)) continue;

                long w5 = 0, w1 = 0;
                if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
                {
                    w5 = Long(cc, "ephemeral_5m_input_tokens");
                    w1 = Long(cc, "ephemeral_1h_input_tokens");
                }
                else w5 = Long(u, "cache_creation_input_tokens");

                var id = Str(msg, "id") ?? Str(r, "uuid") ?? Guid.NewGuid().ToString("N");
                e = new TokenEvent(source, id, ts, Str(r, "sessionId") ?? "", model,
                    Long(u, "input_tokens"), Long(u, "cache_read_input_tokens"), w5, w1, Long(u, "output_tokens"),
                    Str(u, "speed") ?? "standard");
            }
            catch (JsonException) { }
            if (e is not null) yield return e;
        }
    }
}
