using System.Text;
using System.Text.Json;
using AvaCodeStats.Core.Providers.Claude;

namespace AvaCodeStats.Core.Providers.GrokBot;

/// <summary>
/// Grok Bot weekly usage, from the cache the desktop app keeps in
/// <c>%APPDATA%\Grok Bot\sand-client-persistence</c>: each <c>.blob</c> file is named by its key in
/// lowercase unpadded base32, and the <c>…weekly-usage.cache</c> key holds plain JSON with
/// <c>percentUsed</c> and <c>nextResetMs</c>. The reading is as fresh as the app's last refresh
/// (it refreshes while running); no credentials are touched.
/// </summary>
public sealed class GrokBotProvider(string account, string? dataDir) : IUsageProvider
{
    public const string Id = "grok-bot";

    public string ProviderId => Id;
    public string AccountId => account;
    public string DisplayName => "Grok Bot";

    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Grok Bot");

    public Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        var dir = Path.Combine(string.IsNullOrWhiteSpace(dataDir) ? DefaultDataDir : Environment.ExpandEnvironmentVariables(dataDir),
            "sand-client-persistence");
        if (!Directory.Exists(dir)) throw new InvalidOperationException("Grok Bot has no usage cache yet (open the app once).");

        // Several account slots can exist; use the most recent reading.
        var readings = Directory.EnumerateFiles(dir, "*.blob")
            .Where(f => DecodeKey(Path.GetFileNameWithoutExtension(f))?.EndsWith(".weekly-usage.cache", StringComparison.Ordinal) == true)
            .Select(f => { try { return Parse(File.ReadAllText(f), account, DateTimeOffset.UtcNow); } catch (Exception e) when (e is JsonException or IOException) { return null; } })
            .Where(s => s is not null)
            .OrderByDescending(s => s!.TimestampUtc)
            .ToList();
        IReadOnlyList<UsageSnapshot> result = readings.Count > 0 ? [readings[0]!] : [];
        return Task.FromResult(result);
    }

    /// <summary>Parses the cached weekly-usage JSON. A reading whose reset has passed counts as 0% of the new week.</summary>
    public static UsageSnapshot? Parse(string json, string account, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var v) || ClaudeCodeProvider.Str(v, "kind") != "present"
            || !v.TryGetProperty("reading", out var reading) || !reading.TryGetProperty("usage", out var usage))
            return null;

        var used = ClaudeCodeProvider.Num(usage, "percentUsed");
        var reset = DateTimeOffset.FromUnixTimeMilliseconds(ClaudeCodeProvider.Long(usage, "nextResetMs"));
        var readAt = DateTimeOffset.FromUnixTimeMilliseconds(ClaudeCodeProvider.Long(reading, "readAtMs"));
        if (reset.ToUnixTimeMilliseconds() <= 0) return null;

        var week = TimeSpan.FromDays(7);
        while (reset <= now) { reset += week; used = 0; readAt = now; }

        var plan = ClaudeCodeProvider.Str(usage, "unifiedPlanName") ?? ClaudeCodeProvider.Str(usage, "grokPlanLabel");
        return new UsageSnapshot(Id, account, "weekly", "Weekly", Math.Round(used, 2), 100, false,
            reset - week, reset, readAt, plan is null ? null : $"Grok Bot {plan}");
    }

    /// <summary>RFC 4648 base32, lowercase, no padding.</summary>
    public static string? DecodeKey(string name)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var bytes = new List<byte>(name.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var ch in name)
        {
            var i = alphabet.IndexOf(char.ToLowerInvariant(ch));
            if (i < 0) return null;
            buffer = (buffer << 5) | i;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        try { return Encoding.UTF8.GetString(bytes.ToArray()); }
        catch (ArgumentException) { return null; }
    }
}
