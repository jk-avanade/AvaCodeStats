# AvaCodeStats

A Windows tray app that tracks AI coding subscriptions, shows whether you are burning them faster than an even pace, and keeps a history of usage, tokens and cost.

- **Tray ring**: the arc is how much of the quota is used. The white notch is where an even pace would be today. The arc past the notch is overburn (amber or red). A faint stretch before the notch is budget banked. The tooltip gives the numbers, and a balloon warns you when you go far over pace.
- **Double-click** the ring to open the dashboard:
  - **Limits**: every quota as a bar. The fill shows what's left, the hatching shows what's used, and the white marker shows how much time is left in the window. Click a bar to open its burn view.
  - **Burn**: one quota in detail: pace cards, the cumulative burn against the even-pace line with a projection, the daily burn against the daily allowance, and a history of earlier periods.
  - **Cost / Tokens**: Claude Code and Codex usage from their local session logs. It covers the past 24 hours, 7, 30 or 90 days, split by tool and model, plus cache savings and a breakdown by day. Cost is an API list-price estimate.
- **Profiles**: Work, Personal, or Unified (everything). The tray and the dashboard follow the active profile, which you can switch from the tray menu or at the top of the dashboard.

## What it reads

| Provider | Limits | Tokens / cost |
|---|---|---|
| GitHub Copilot | `api.github.com/copilot_internal/user`, the endpoint the Copilot extensions use for their own quota display. The token comes from the GitHub CLI (`gh auth token --user <login>`), so every `gh` account can be tracked. | – |
| Claude (Claude Code) | `api.anthropic.com/api/oauth/usage`, the endpoint behind Claude Code's `/usage`, using the sign-in Claude Code keeps in `~/.claude/.credentials.json`. It's read-only: the app never refreshes the token, so Claude Code's own login is untouched. | `~/.claude/projects/**/*.jsonl` |
| Codex | Rate-limit windows recorded in Codex's session logs | `~/.codex/sessions`, `~/.codex/archived_sessions` |

Snapshots are stored in SQLite every 5 minutes: changes always, unchanged readings at most hourly. Log files are read incrementally and their token events are copied into the database, so history survives the tools cleaning up their own logs.

Data lives in `%LOCALAPPDATA%\AvaCodeStats` (`settings.json`, `usage.db`). Set `AVACODESTATS_DATA` to use another folder.

## Settings

Open them from the tray: **Open settings file**, then **Reload settings**.

```jsonc
{
  "pollMinutes": 5,
  "activeProfile": "work",              // work | personal | unified
  "subscriptions": [
    { "provider": "github-copilot", "account": "jk-avanade", "profile": "work" },
    { "provider": "github-copilot", "account": "jernejk",    "profile": "personal" },
    { "provider": "claude-code",    "account": "default",    "profile": "personal" },   // "path": other Claude home
    { "provider": "codex",          "account": "default",    "profile": "personal" }    // "path": other CODEX_HOME
  ],
  "trayKeys": { "work": "github-copilot:jk-avanade:chat" },   // or pick from the tray menu
  "pricing": [                                              // USD per million tokens; longest prefix wins
    { "model": "gpt-6", "input": 0, "output": 0, "cacheRead": 0 }
  ],
  "onPaceDays": 0.5,     // within ±0.5 days of allowance = on pace
  "farOverDays": 2.0     // more than 2 days of allowance over = red + notification
}
```

Claude model prices are built in. Other vendors' models have no default price, so add them under `pricing` to include Codex in the cost view.

Pace is measured in **days of allowance**: one day of allowance is the quota divided by the days in the period. "4.6 days over pace" means you have used what an even pace would only reach 4.6 days from now.

## Adding a subscription type

Implement `IUsageProvider` in `src/AvaCodeStats.Core/Providers/<Name>/`. It returns `UsageSnapshot`s (used, entitlement, period) and can write `TokenEvent`s to the `TokenStore`. Then register it in `ProviderFactory`. Everything else (pace, history, tray, dashboard, profiles) is provider-agnostic.

## Build, run, install

```powershell
dotnet test
dotnet run --project src/AvaCodeStats -- --open limits   # also: burn | cost | tokens
./scripts/install.ps1                                    # publishes to %LOCALAPPDATA%\Programs\AvaCodeStats and starts it
```

Then use **Start with Windows** in the tray menu.

Requires the .NET 10 desktop runtime, and the GitHub CLI for Copilot.
