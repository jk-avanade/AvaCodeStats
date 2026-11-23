using AvaCodeStats.Core.Providers;
using AvaCodeStats.Core.Providers.Claude;
using AvaCodeStats.Core.Providers.Codex;
using AvaCodeStats.Core.Providers.GitHubCopilot;
using AvaCodeStats.Core.Tokens;

namespace AvaCodeStats.Core;

/// <summary>Turns settings into provider instances. Add new subscription types here.</summary>
public static class ProviderFactory
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static IReadOnlyDictionary<string, string> DisplayNames { get; } = new Dictionary<string, string>
    {
        [CopilotQuotaParser.ProviderId] = "GitHub Copilot",
        [ClaudeCodeProvider.Id] = "Claude",
        [CodexProvider.Id] = "Codex",
    };

    public static string DisplayName(string providerId) => DisplayNames.GetValueOrDefault(providerId, providerId);

    public static IUsageProvider Create(SubscriptionConfig config, TokenStore tokens, bool codexLive = true) => config.Provider switch
    {
        CopilotQuotaParser.ProviderId => new GitHubCopilotProvider(config.Account, TokenSource(config), Http)
        {
            IncludeUnlimited = config.IncludeUnlimited,
        },
        ClaudeCodeProvider.Id => new ClaudeCodeProvider(config.Account, Home(config, ClaudeCodeProvider.DefaultHome), tokens, Http),
        CodexProvider.Id => new CodexProvider(config.Account, Home(config, CodexProvider.DefaultHome), tokens, Http, codexLive),
        _ => throw new NotSupportedException($"Unknown provider '{config.Provider}'."),
    };

    /// <summary>
    /// First-run discovery: every GitHub CLI account, plus Claude Code and Codex when installed.
    /// Everything starts in the personal profile; move work accounts in settings.json.
    /// </summary>
    public static async Task<List<SubscriptionConfig>> DiscoverAsync(CancellationToken ct)
    {
        var found = new List<SubscriptionConfig>();
        try
        {
            foreach (var login in await GhCli.ListAccountsAsync(ct))
                found.Add(new SubscriptionConfig { Provider = CopilotQuotaParser.ProviderId, Account = login });
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }

        if (Directory.Exists(ClaudeCodeProvider.DefaultHome))
            found.Add(new SubscriptionConfig { Provider = ClaudeCodeProvider.Id, Account = "default" });
        if (Directory.Exists(CodexProvider.DefaultHome))
            found.Add(new SubscriptionConfig { Provider = CodexProvider.Id, Account = "default" });
        return found;
    }

    private static string Home(SubscriptionConfig c, string fallback) =>
        string.IsNullOrWhiteSpace(c.Path) ? fallback : Environment.ExpandEnvironmentVariables(c.Path);

    private static Func<CancellationToken, Task<string>> TokenSource(SubscriptionConfig c)
    {
        if (c.TokenSource.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var name = c.TokenSource[4..];
            return _ => Task.FromResult(Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidOperationException($"Environment variable {name} is not set."));
        }
        return ct => GhCli.GetTokenAsync(c.Account, ct);
    }
}
