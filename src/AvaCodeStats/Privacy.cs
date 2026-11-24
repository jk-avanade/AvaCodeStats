using AvaCodeStats.Core;

namespace AvaCodeStats;

/// <summary>
/// How account logins are shown. With <see cref="Redact"/> on (the --screenshots mode), a login such as a
/// GitHub user name becomes "work-account" or "personal-account", numbered when a profile holds several.
/// </summary>
internal static class Privacy
{
    public static bool Redact { get; set; }

    public static string Account(AppSettings settings, string providerId, string account)
    {
        if (!Redact || account == "default") return account;
        var profile = settings.ProfileOf(providerId, account);
        var peers = settings.Subscriptions
            .Where(c => c.Provider == providerId && c.Account != "default" && settings.ProfileOf(c.Provider, c.Account) == profile)
            .Select(c => c.Account).Distinct().ToList();
        var n = peers.IndexOf(account);
        return $"{profile}-account" + (n > 0 ? $"-{n + 1}" : "");
    }

    /// <summary>" · login" after a provider name, or nothing for the provider's only (default) account.</summary>
    public static string Suffix(AppSettings settings, string providerId, string account, string separator = " · ") =>
        account == "default" ? "" : separator + Account(settings, providerId, account);
}
