namespace AvaCodeStats;

internal static class Fmt
{
    public static string Tokens(double v) => v switch
    {
        >= 1e9 => $"{v / 1e9:0.##}B",
        >= 1e6 => $"{v / 1e6:0.##}M",
        >= 1e3 => $"{v / 1e3:0.#}K",
        _ => $"{v:0}",
    };

    public static string Money(double v) => v >= 1000 ? $"${v:N0}" : $"${v:N2}";

    public static string Countdown(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "now";
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(1, t.Minutes)}m";
    }
}

/// <summary>Segoe Fluent Icons code points used in the UI.</summary>
internal static class Glyphs
{
    public const string Gauge = "";      // Speedometer
    public const string Chart = "";      // AreaChart
    public const string Money = "";      // PaymentCard
    public const string Tokens = "";     // Code
    public const string Settings = "";
    public const string Refresh = "";
    public const string OpenWindow = ""; // OpenInNewWindow
    public const string Check = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Info = "";
}
