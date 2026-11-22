namespace AvaCodeStats.Core.Providers;

/// <summary>
/// A source of usage for one subscription account. Implement this to support a new kind of
/// subscription (e.g. Claude, Cursor, OpenAI); register it in <see cref="ProviderFactory"/>.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Stable provider id stored in history, e.g. "github-copilot".</summary>
    string ProviderId { get; }

    /// <summary>Account this instance reads, e.g. a GitHub login.</summary>
    string AccountId { get; }

    /// <summary>Label for the UI, e.g. "GitHub Copilot".</summary>
    string DisplayName { get; }

    /// <summary>Reads the current usage of every quota this account has.</summary>
    Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken cancellationToken);
}
