using System.Net.Http.Headers;
using System.Text.Json;
using AvaCodeStats.Core.Providers.Claude;
using AvaCodeStats.Core.Tokens;

namespace AvaCodeStats.Core.Providers.Codex;

/// <summary>
/// Codex (ChatGPT plan) usage:
/// - limits live from Codex's own usage endpoint, with the ChatGPT sign-in Codex keeps in auth.json
///   (read-only), falling back to the rate-limit windows recorded in its session logs;
/// - tokens from token_count events in the session logs (sessions/ and archived_sessions/).
/// </summary>
public sealed class CodexProvider(string account, string codexHome, TokenStore tokens, HttpClient? http = null, bool live = true) : IUsageProvider
{
    public const string Id = "codex";

    public string ProviderId => Id;
    public string AccountId => account;
    public string DisplayName => "Codex";
    public string Source => $"{Id}:{account}";
    private string LimitsKey => $"codex-rate-limits:{account}";

    public static string DefaultHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private static readonly Uri UsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");

    public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        await Task.Run(ScanLogs, ct);
        if (live && http is not null && await TryLiveAsync(ct) is { Count: > 0 } current) return current;
        return tokens.GetValue(LimitsKey) is { } json
            ? ParseLimits(json, account, DateTimeOffset.UtcNow)
            : [];
    }

    private async Task<IReadOnlyList<UsageSnapshot>?> TryLiveAsync(CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(codexHome, "auth.json");
            if (!File.Exists(path)) return null;
            string? token, accountId;
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct)))
            {
                if (!doc.RootElement.TryGetProperty("tokens", out var t) || t.ValueKind != JsonValueKind.Object) return null;
                token = ClaudeCodeProvider.Str(t, "access_token");
                accountId = ClaudeCodeProvider.Str(t, "account_id");
            }
            if (token is null) return null;

            using var req = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.UserAgent.ParseAdd("AvaCodeStats/0.2");
            if (accountId is not null) req.Headers.Add("ChatGPT-Account-Id", accountId);
            using var res = await http!.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null; // expired sign-in etc.: the logs still work
            return ParseLive(await res.Content.ReadAsStringAsync(ct), account, DateTimeOffset.UtcNow);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Parses the wham/usage response (same metric ids as <see cref="ParseLimits"/>, so history joins up).</summary>
    public static IReadOnlyList<UsageSnapshot> ParseLive(string json, string account, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("rate_limit", out var rl) || rl.ValueKind != JsonValueKind.Object) return [];
        var plan = ClaudeCodeProvider.Str(root, "plan_type");
        var result = new List<UsageSnapshot>();
        foreach (var (slot, name) in new[] { ("primary", "primary_window"), ("secondary", "secondary_window") })
        {
            if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            var seconds = ClaudeCodeProvider.Num(w, "limit_window_seconds");
            if (seconds <= 0) continue;
            var minutes = seconds / 60;
            var window = TimeSpan.FromSeconds(seconds);
            var reset = DateTimeOffset.FromUnixTimeSeconds(ClaudeCodeProvider.Long(w, "reset_at"));
            result.Add(new UsageSnapshot(Id, account, $"{slot}_{minutes:0}", WindowName(minutes),
                ClaudeCodeProvider.Num(w, "used_percent"), 100, false, reset - window, reset, now,
                plan is null ? null : $"ChatGPT {plan}"));
        }
        return result;
    }

    private static string WindowName(double minutes) => minutes switch
    {
        10080 => "Weekly",
        300 => "5-hour",
        _ => $"{TimeSpan.FromMinutes(minutes).TotalHours:0}-hour",
    };

    private void ScanLogs()
    {
        var latestLimits = tokens.GetValue(LimitsKey);
        foreach (var dir in new[] { "sessions", "archived_sessions" }.Select(d => Path.Combine(codexHome, d)).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    tokens.ScanAppended(file, (lines, context) =>
                    {
                        var state = ParseState.From(context, Path.GetFileNameWithoutExtension(file));
                        var events = ParseLogLines(lines, Source, state, ref latestLimits).ToList();
                        tokens.Upsert(events);
                        return state.ToString();
                    });
                }
                catch (IOException) { }
            }
        }
        if (latestLimits is not null) tokens.SetValue(LimitsKey, latestLimits);
    }

    /// <summary>Session id and current model carried from earlier lines of the same file.</summary>
    public sealed class ParseState
    {
        public string Session { get; set; } = "";
        public string Model { get; set; } = "unknown";

        public static ParseState From(string? context, string fallbackSession)
        {
            var parts = (context ?? "").Split('|');
            return parts.Length == 2
                ? new ParseState { Session = parts[0], Model = parts[1] }
                : new ParseState { Session = fallbackSession };
        }

        public override string ToString() => $"{Session}|{Model}";
    }

    /// <param name="latestLimits">Newest rate-limit record seen so far ({"ts":…, "rate_limits":…}); updated in place.</param>
    public static List<TokenEvent> ParseLogLines(IEnumerable<string> lines, string source, ParseState state, ref string? latestLimits)
    {
        var events = new List<TokenEvent>();
        var latestTs = LimitsTimestamp(latestLimits);
        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (!r.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                switch (ClaudeCodeProvider.Str(r, "type"))
                {
                    case "session_meta":
                        state.Session = ClaudeCodeProvider.Str(p, "id") ?? state.Session;
                        break;
                    case "turn_context":
                        state.Model = ClaudeCodeProvider.Str(p, "model") ?? state.Model;
                        break;
                    case "event_msg" when ClaudeCodeProvider.Str(p, "type") == "token_count":
                        if (!ClaudeCodeProvider.TryDate(r, "timestamp", out var ts)) break;
                        if (p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                            && info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object
                            && info.TryGetProperty("total_token_usage", out var total))
                        {
                            var input = ClaudeCodeProvider.Long(last, "input_tokens");
                            var cached = ClaudeCodeProvider.Long(last, "cached_input_tokens");
                            events.Add(new TokenEvent(source,
                                $"{state.Session}:{ClaudeCodeProvider.Long(total, "total_tokens")}",
                                ts, state.Session, state.Model,
                                Math.Max(0, input - cached), cached,
                                ClaudeCodeProvider.Long(last, "cache_write_input_tokens"), 0,
                                ClaudeCodeProvider.Long(last, "output_tokens")));
                        }
                        if (p.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object && ts > latestTs)
                        {
                            latestTs = ts;
                            latestLimits = JsonSerializer.Serialize(new { ts, rate_limits = rl });
                        }
                        break;
                }
            }
            catch (JsonException) { }
        }
        return events;
    }

    private static DateTimeOffset LimitsTimestamp(string? json)
    {
        if (json is null) return DateTimeOffset.MinValue;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("ts").GetDateTimeOffset();
    }

    public static IReadOnlyList<UsageSnapshot> ParseLimits(string json, string account, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var observed = doc.RootElement.GetProperty("ts").GetDateTimeOffset();
        var rl = doc.RootElement.GetProperty("rate_limits");
        var plan = ClaudeCodeProvider.Str(rl, "plan_type");
        var result = new List<UsageSnapshot>();
        foreach (var slot in new[] { "primary", "secondary" })
        {
            if (!rl.TryGetProperty(slot, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            var minutes = ClaudeCodeProvider.Num(w, "window_minutes");
            if (minutes <= 0) continue;
            var window = TimeSpan.FromMinutes(minutes);
            var reset = DateTimeOffset.FromUnixTimeSeconds(ClaudeCodeProvider.Long(w, "resets_at"));
            var used = ClaudeCodeProvider.Num(w, "used_percent");

            // The log only knows the window it last saw; once that window ends, usage is back to 0.
            while (reset <= now) { reset += window; used = 0; }

            var name = WindowName(minutes);
            result.Add(new UsageSnapshot(Id, account, $"{slot}_{minutes:0}", name, used, 100, false,
                reset - window, reset, used == 0 ? now : observed, plan is null ? null : $"ChatGPT {plan}"));
        }
        return result;
    }
}
