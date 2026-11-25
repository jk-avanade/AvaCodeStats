using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AvaCodeStats.Core.Providers.Antigravity;

/// <summary>
/// Google Antigravity quotas via its CLI: <c>agy -p "/quota" --output-format json</c>. The CLI answers
/// slash commands without starting an agent turn or spending quota, using its own sign-in.
/// Groups of models share a weekly and a 5-hour limit (e.g. "Gemini Models", "Claude and GPT models").
/// </summary>
public sealed class AntigravityProvider(string account, string? agyPath) : IUsageProvider
{
    public const string Id = "antigravity";

    /// <summary>The CLI call takes a few seconds and the numbers move slowly, so reuse a recent reading.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    private IReadOnlyList<UsageSnapshot>? _last;
    private DateTimeOffset _lastAt;

    public string ProviderId => Id;
    public string AccountId => account;
    public string DisplayName => "Antigravity";

    /// <summary>agy.exe on PATH (winget adds a link) or in the winget package folder.</summary>
    public static string? FindCli()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim(), "agy.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet");
        var link = Path.Combine(winget, "Links", "agy.exe");
        if (File.Exists(link)) return link;
        var packages = Path.Combine(winget, "Packages");
        return Directory.Exists(packages)
            ? Directory.EnumerateDirectories(packages, "Google.AntigravityCLI*").Select(d => Path.Combine(d, "agy.exe")).FirstOrDefault(File.Exists)
            : null;
    }

    public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        if (_last is not null && DateTimeOffset.UtcNow - _lastAt < MinInterval) return _last;

        var exe = agyPath is { Length: > 0 } ? Environment.ExpandEnvironmentVariables(agyPath) : FindCli()
                  ?? throw new InvalidOperationException("Antigravity CLI (agy) not found.");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in new[] { "-p", "/quota", "--output-format", "json" }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start agy.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }

        var json = await stdout;
        if (p.ExitCode != 0)
            throw new InvalidOperationException(p.ExitCode == 2
                ? "Antigravity quota needs a Google sign-in (agy is using an API key)."
                : $"agy /quota failed: {(await stderr).Trim()}");

        _last = Parse(json, account, DateTimeOffset.UtcNow);
        _lastAt = DateTimeOffset.UtcNow;
        return _last;
    }

    public static IReadOnlyList<UsageSnapshot> Parse(string json, string account, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("command", out var cmd) || !cmd.TryGetProperty("data", out var data)
            || !data.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("agy /quota returned no quota groups (is the CLI signed in?).");

        var result = new List<UsageSnapshot>();
        foreach (var g in groups.EnumerateArray())
        {
            var group = ShortGroup(JsonHelpers.Str(g, "name") ?? "Models");
            if (!g.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (var b in buckets.EnumerateArray())
            {
                if (!b.TryGetProperty("remaining_fraction", out var rf) || rf.ValueKind != JsonValueKind.Number) continue;
                if (JsonHelpers.Str(b, "reset_time") is not { } resetText
                    || !DateTimeOffset.TryParse(resetText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reset)) continue;
                var window = JsonHelpers.Str(b, "window") ?? "weekly";
                var span = window == "5h" ? TimeSpan.FromHours(5) : window == "daily" ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
                var label = window switch { "5h" => "5-hour", "daily" => "Daily", _ => "Weekly" };
                var id = JsonHelpers.Str(b, "id") ?? $"{group}-{window}";
                result.Add(new UsageSnapshot(Id, account, id, $"{group} · {label}",
                    Math.Round((1 - Math.Clamp(rf.GetDouble(), 0, 1)) * 100, 2), 100, false,
                    reset - span, reset, now));
            }
        }
        return result;
    }

    private static string ShortGroup(string name) => name switch
    {
        "Gemini Models" => "Gemini",
        "Claude and GPT models" => "Claude & GPT",
        _ => name,
    };
}
