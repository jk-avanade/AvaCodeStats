using System.Diagnostics;
using System.Text.Json;

namespace AvaCodeStats.Core.Providers.GitHubCopilot;

/// <summary>Reads accounts and tokens from the GitHub CLI, so the app never stores a token itself.</summary>
public static class GhCli
{
    public static async Task<IReadOnlyList<string>> ListAccountsAsync(CancellationToken ct)
    {
        var json = await RunAsync(["auth", "status", "--json", "hosts"], ct);
        var logins = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("hosts", out var hosts)
            && hosts.TryGetProperty("github.com", out var accounts)
            && accounts.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in accounts.EnumerateArray())
            {
                if (a.TryGetProperty("login", out var login) && login.GetString() is { Length: > 0 } l)
                    logins.Add(l);
            }
        }
        return logins;
    }

    public static async Task<string> GetTokenAsync(string login, CancellationToken ct) =>
        (await RunAsync(["auth", "token", "--hostname", "github.com", "--user", login], ct)).Trim();

    private static async Task<string> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the GitHub CLI (gh).");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"gh {args[0]} {args[1]} failed: {(await stderr).Trim()}");
        return await stdout;
    }
}
