using System.Text.Json;
using System.Text.Json.Serialization;
using AvaCodeStats.Core.Tokens;

namespace AvaCodeStats.Core;

public static class Profiles
{
    public const string Work = "work";
    public const string Personal = "personal";
    /// <summary>Everything, regardless of profile.</summary>
    public const string Unified = "unified";

    public static readonly string[] All = [Work, Personal, Unified];

    public static string Label(string profile) => profile switch
    {
        Work => "Work",
        Personal => "Personal",
        _ => "Unified",
    };
}

public sealed class SubscriptionConfig
{
    /// <summary>"github-copilot", "claude-code" or "codex".</summary>
    public string Provider { get; set; } = "github-copilot";

    /// <summary>Account at the provider (a GitHub login), or a label for local tools.</summary>
    public string Account { get; set; } = "";

    /// <summary>"work" or "personal".</summary>
    public string Profile { get; set; } = Profiles.Personal;

    /// <summary>GitHub Copilot: "gh" (GitHub CLI) or "env:VARIABLE_NAME".</summary>
    public string TokenSource { get; set; } = "gh";

    /// <summary>Claude Code / Codex: their home folder. Empty = the default (~/.claude, ~/.codex).</summary>
    public string? Path { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Also track unlimited quotas / quotas with no entitlement.</summary>
    public bool IncludeUnlimited { get; set; }

    [JsonIgnore]
    public string Source => $"{Provider}:{Account}";
}

public sealed class AppSettings
{
    public int PollMinutes { get; set; } = 5;

    /// <summary>Profile shown in the tray and dashboard: "work", "personal" or "unified".</summary>
    public string ActiveProfile { get; set; } = Profiles.Work;

    /// <summary>Subscriptions to track. Empty = discover GitHub CLI accounts, Claude Code and Codex on startup.</summary>
    public List<SubscriptionConfig> Subscriptions { get; set; } = [];

    /// <summary>Quota shown in the tray per profile, "provider:account:metric". Missing = pick automatically.</summary>
    public Dictionary<string, string> TrayKeys { get; set; } = [];

    /// <summary>Extra or replacement model prices (USD per million tokens) for the cost estimate.</summary>
    public List<ModelPrice> Pricing { get; set; } = [];

    public double OnPaceDays { get; set; } = 0.5;
    public double FarOverDays { get; set; } = 2.0;

    public bool NotifyWhenFarOverPace { get; set; } = true;

    [JsonIgnore]
    public PaceThresholds Thresholds => new(OnPaceDays, FarOverDays);

    /// <summary>Profile a provider account belongs to (accounts not in settings count as personal).</summary>
    public string ProfileOf(string providerId, string accountId) =>
        Subscriptions.FirstOrDefault(s => s.Provider == providerId && s.Account == accountId)?.Profile ?? Profiles.Personal;

    public bool InActiveProfile(string providerId, string accountId) =>
        ActiveProfile == Profiles.Unified || ProfileOf(providerId, accountId) == ActiveProfile;

    /// <summary>Token log sources ("provider:account") visible in the active profile.</summary>
    public IReadOnlySet<string> ActiveSources() =>
        Subscriptions.Where(s => s.Enabled && InActiveProfile(s.Provider, s.Account)).Select(s => s.Source).ToHashSet();

    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("AVACODESTATS_DATA")
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaCodeStats");

    public static string SettingsPath => System.IO.Path.Combine(DataDirectory, "settings.json");
    public static string DatabasePath => System.IO.Path.Combine(DataDirectory, "usage.db");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Keep running on a broken file; the user can fix it via "Open settings".
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
    }
}
