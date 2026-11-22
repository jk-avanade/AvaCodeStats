using System.Text.Json;
using AvaCodeStats.Core.Providers.Claude;
using AvaCodeStats.Core.Tokens;

namespace AvaCodeStats.Core.Providers.Codex;

/// <summary>
/// Codex (ChatGPT plan) usage, all from Codex's own session logs (sessions/ and archived_sessions/):
/// token_count events carry per-call tokens and the latest rate-limit windows.
/// </summary>
public sealed class CodexProvider(string account, string codexHome, TokenStore tokens) : IUsageProvider
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

    public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        await Task.Run(ScanLogs, ct);
        return tokens.GetValue(LimitsKey) is { } json
            ? ParseLimits(json, account, DateTimeOffset.UtcNow)
            : [];
    }

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

            var name = minutes switch { 10080 => "Weekly", 300 => "5-hour", _ => $"{window.TotalHours:0}-hour" };
            result.Add(new UsageSnapshot(Id, account, $"{slot}_{minutes:0}", name, used, 100, false,
                reset - window, reset, used == 0 ? now : observed, plan is null ? null : $"ChatGPT {plan}"));
        }
        return result;
    }
}
