using AvaCodeStats.Core.Providers;
using AvaCodeStats.Core.Storage;
using AvaCodeStats.Core.Tokens;

namespace AvaCodeStats.Core;

/// <summary>Polls every provider, writes history, and keeps the latest snapshot per quota.</summary>
public sealed class UsageCollector(UsageStore store, TokenStore tokens)
{
    private readonly Dictionary<SubscriptionKey, UsageSnapshot> _latest = [];
    private readonly Dictionary<string, string> _errors = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UsageStore Store => store;
    public TokenStore Tokens => tokens;

    public IReadOnlyList<IUsageProvider> Providers { get; set; } = [];

    public IReadOnlyDictionary<SubscriptionKey, UsageSnapshot> Latest => _latest;

    /// <summary>Last error per provider account ("github-copilot:login"), cleared on success.</summary>
    public IReadOnlyDictionary<string, string> Errors => _errors;

    public DateTimeOffset? LastCollectedUtc { get; private set; }

    public event EventHandler? Updated;

    /// <summary>Seeds <see cref="Latest"/> from history so the UI has data before the first poll.</summary>
    public void LoadFromHistory()
    {
        foreach (var s in store.GetLatestPerKey())
            _latest[s.Key] = s;
    }

    public async Task CollectAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return; // a poll is already running
        try
        {
            var results = await Task.WhenAll(Providers.Select(async p =>
            {
                try { return (p, snapshots: await p.FetchAsync(ct), error: (string?)null); }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return (p, snapshots: (IReadOnlyList<UsageSnapshot>)[], error: e.Message);
                }
            }));

            foreach (var (p, snapshots, error) in results)
            {
                var id = $"{p.ProviderId}:{p.AccountId}";
                if (error is not null) { _errors[id] = error; continue; }
                _errors.Remove(id);
                foreach (var s in snapshots)
                {
                    store.Add(s);
                    _latest[s.Key] = s;
                }
            }
            LastCollectedUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The quota for the tray: the configured one, else the most used-up limited quota (premium requests first).</summary>
    public UsageSnapshot? PickTray(string? configuredKey, Func<UsageSnapshot, bool>? include = null)
    {
        include ??= _ => true;
        if (SubscriptionKey.TryParse(configuredKey) is { } k && _latest.TryGetValue(k, out var chosen) && include(chosen))
            return chosen;

        return _latest.Values
            .Where(s => !s.Unlimited && s.Entitlement > 0 && include(s))
            .OrderByDescending(s => s.MetricId == "premium_interactions")
            .ThenByDescending(s => s.UsedFraction)
            .FirstOrDefault();
    }
}
