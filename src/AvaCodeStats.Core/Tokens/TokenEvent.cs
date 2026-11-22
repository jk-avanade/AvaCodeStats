namespace AvaCodeStats.Core.Tokens;

/// <summary>
/// One model call read from a local tool log (Claude Code, Codex).
/// <see cref="Input"/> is uncached input only; cached input is in <see cref="CacheRead"/>.
/// </summary>
public sealed record TokenEvent(
    string Source,
    string EventId,
    DateTimeOffset TimestampUtc,
    string SessionId,
    string Model,
    long Input,
    long CacheRead,
    long CacheWrite5m,
    long CacheWrite1h,
    long Output,
    string Speed = "standard")
{
    public long CacheWrite => CacheWrite5m + CacheWrite1h;
    public long Total => Input + CacheRead + CacheWrite + Output;

    /// <summary>Provider part of <see cref="Source"/> ("claude-code:me" → "claude-code").</summary>
    public string ProviderId => Source.Split(':')[0];
}
