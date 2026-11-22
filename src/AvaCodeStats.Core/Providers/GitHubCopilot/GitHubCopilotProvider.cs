using System.Net.Http.Headers;

namespace AvaCodeStats.Core.Providers.GitHubCopilot;

/// <summary>
/// Reads Copilot quotas from the endpoint the Copilot editor extensions use for their own usage display.
/// Token: from the GitHub CLI (default) or an environment variable holding a PAT/OAuth token.
/// </summary>
public sealed class GitHubCopilotProvider(string account, Func<CancellationToken, Task<string>> tokenSource, HttpClient http)
    : IUsageProvider
{
    private static readonly Uri Endpoint = new("https://api.github.com/copilot_internal/user");

    private string? _token;
    private DateTimeOffset _tokenFetched;

    public string ProviderId => CopilotQuotaParser.ProviderId;
    public string AccountId => account;
    public string DisplayName => "GitHub Copilot";

    public bool IncludeUnlimited { get; init; }

    public async Task<IReadOnlyList<UsageSnapshot>> FetchAsync(CancellationToken ct)
    {
        var json = await GetAsync(await TokenAsync(false, ct), ct)
                   ?? await GetAsync(await TokenAsync(true, ct), ct) // token may have rotated
                   ?? throw new InvalidOperationException($"GitHub rejected the token for {account}.");
        return CopilotQuotaParser.Parse(json, account, DateTimeOffset.UtcNow, IncludeUnlimited);
    }

    private async Task<string?> GetAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        req.Headers.UserAgent.ParseAdd("AvaCodeStats/0.1");
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> TokenAsync(bool refresh, CancellationToken ct)
    {
        if (refresh || _token is null || DateTimeOffset.UtcNow - _tokenFetched > TimeSpan.FromMinutes(30))
        {
            _token = await tokenSource(ct);
            _tokenFetched = DateTimeOffset.UtcNow;
        }
        return _token;
    }
}
