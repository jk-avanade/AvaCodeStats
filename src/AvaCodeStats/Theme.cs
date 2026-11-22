using System.Drawing.Drawing2D;
using AvaCodeStats.Core;
using AvaCodeStats.Core.Providers.Claude;
using AvaCodeStats.Core.Providers.Codex;
using AvaCodeStats.Core.Providers.GitHubCopilot;

namespace AvaCodeStats;

internal static class Theme
{
    public static readonly Color Background = ColorTranslator.FromHtml("#0C0D10");
    public static readonly Color Panel = ColorTranslator.FromHtml("#15171B");
    public static readonly Color PanelBorder = ColorTranslator.FromHtml("#26292F");
    public static readonly Color Selected = ColorTranslator.FromHtml("#2B2E35");
    public static readonly Color Text = ColorTranslator.FromHtml("#E8EAED");
    public static readonly Color Muted = ColorTranslator.FromHtml("#9AA1AC");
    public static readonly Color Track = ColorTranslator.FromHtml("#30343C");
    public static readonly Color Grid = ColorTranslator.FromHtml("#23262C");

    public static readonly Color Green = ColorTranslator.FromHtml("#3FB950");
    public static readonly Color Teal = ColorTranslator.FromHtml("#2EA8A0");
    public static readonly Color Amber = ColorTranslator.FromHtml("#D29922");
    public static readonly Color Red = ColorTranslator.FromHtml("#F85149");
    public static readonly Color Blue = ColorTranslator.FromHtml("#58A6FF");
    public static readonly Color Purple = ColorTranslator.FromHtml("#A371F7");
    public static readonly Color Grey = ColorTranslator.FromHtml("#8B949E");

    public static Color ForStatus(PaceStatus s) => s switch
    {
        PaceStatus.UnderPace => Green,
        PaceStatus.OnPace => Teal,
        PaceStatus.SlightlyOver => Amber,
        PaceStatus.FarOver or PaceStatus.Exhausted => Red,
        _ => Grey,
    };

    public static Color ForProvider(string providerId) => providerId switch
    {
        ClaudeCodeProvider.Id => ColorTranslator.FromHtml("#D97757"),
        CodexProvider.Id => ColorTranslator.FromHtml("#D5D8DE"),
        CopilotQuotaParser.ProviderId => ColorTranslator.FromHtml("#8C7CF0"),
        _ => Blue,
    };

    /// <summary>Mixes <paramref name="c"/> into the panel colour; amount 0 = panel, 1 = c.</summary>
    public static Color Dim(Color c, double amount) => Color.FromArgb(
        (int)(Panel.R + (c.R - Panel.R) * amount),
        (int)(Panel.G + (c.G - Panel.G) * amount),
        (int)(Panel.B + (c.B - Panel.B) * amount));

    public static readonly Font Body = new("Segoe UI", 9.5f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font Strong = new("Segoe UI Semibold", 10.5f);
    public static readonly Font CardValue = new("Segoe UI Semibold", 15f);
    public static readonly Font Title = new("Segoe UI Semibold", 12f);
    public static readonly Font Big = new("Segoe UI Semibold", 26f);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

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
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
    }
}
