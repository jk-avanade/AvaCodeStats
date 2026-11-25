using AvaCodeStats.Core.Providers;
using AvaCodeStats.Core.Providers.Antigravity;
using AvaCodeStats.Core.Providers.Claude;
using AvaCodeStats.Core.Providers.Codex;
using AvaCodeStats.Core.Providers.GitHubCopilot;
using AvaCodeStats.Core.Providers.GrokBot;
using AvaCodeStats.Core.Tokens;

namespace AvaCodeStats.Core;

/// <summary>Turns settings into provider instances. Add new subscription types here.</summary>
public static class ProviderFactory
{
    // Long-lived client; recycling pooled connections picks up DNS changes.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static IReadOnlyDictionary<string, string> DisplayNames { get; } = new Dictionary<string, string>
    {
        [CopilotQuotaParser.ProviderId] = "GitHub Copilot",
        [ClaudeCodeProvider.Id] = "Claude",
        [CodexProvider.Id] = "Codex",
        [AntigravityProvider.Id] = "Antigravity",
        [GrokBotProvider.Id] = "Grok Bot",
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
        AntigravityProvider.Id => new AntigravityProvider(config.Account, config.Path),
        GrokBotProvider.Id => new GrokBotProvider(config.Account, config.Path),
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

        found.AddRange(LocalTools());
        return found;
    }

    /// <summary>Locally installed tools that need no account setup.</summary>
    private static IEnumerable<SubscriptionConfig> LocalTools()
    {
        if (Directory.Exists(ClaudeCodeProvider.DefaultHome))
            yield return new SubscriptionConfig { Provider = ClaudeCodeProvider.Id, Account = "default" };
        if (Directory.Exists(CodexProvider.DefaultHome))
            yield return new SubscriptionConfig { Provider = CodexProvider.Id, Account = "default" };
        if (AntigravityProvider.FindCli() is not null)
            yield return new SubscriptionConfig { Provider = AntigravityProvider.Id, Account = "default" };
        if (Directory.Exists(GrokBotProvider.DefaultDataDir))
            yield return new SubscriptionConfig { Provider = GrokBotProvider.Id, Account = "default" };
    }

    /// <summary>
    /// Adds local tools installed since the settings were written (as personal). A tool the user
    /// switched off stays in the list disabled, so it is never re-added.
    /// </summary>
    public static bool AddNewLocalTools(List<SubscriptionConfig> subscriptions)
    {
        var added = false;
        foreach (var tool in LocalTools())
        {
            if (subscriptions.Any(s => s.Provider == tool.Provider)) continue;
            subscriptions.Add(tool);
            added = true;
        }
        return added;
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
