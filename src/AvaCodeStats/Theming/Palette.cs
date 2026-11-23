using System.Windows.Media;
using AvaCodeStats.Core;
using AvaCodeStats.Core.Providers.Claude;
using AvaCodeStats.Core.Providers.Codex;
using AvaCodeStats.Core.Providers.GitHubCopilot;

namespace AvaCodeStats.Theming;

/// <summary>
/// The colours the app draws itself (status, bars, rings, charts). Everything else comes from the
/// WPF Fluent theme. One palette per brand × light/dark; adding a brand means adding a factory here.
/// </summary>
internal sealed record Palette(
    bool IsDark,
    string Brand,
    Color Good,
    Color Warn,
    Color Bad,
    Color Neutral,
    Color Track,
    Color Hatch,
    Color Marker,
    Color Text,
    Color TextSecondary,
    Color Grid,
    Color Hero,
    Color Claude,
    Color Codex,
    Color Copilot,
    Color[] Series,
    Color[] TypeShades,
    Color? AccentFill = null,
    Color? AccentText = null,
    Color? TextOnAccent = null,
    Color[]? Aurora = null)
{
    public bool IsAvanade => Brand == "avanade";

    public Color ForStatus(PaceStatus s) => s switch
    {
        PaceStatus.UnderPace or PaceStatus.OnPace => Good,
        PaceStatus.SlightlyOver => Warn,
        PaceStatus.FarOver or PaceStatus.Exhausted => Bad,
        _ => Neutral,
    };

    public Color ForProvider(string providerId) => providerId switch
    {
        ClaudeCodeProvider.Id => Claude,
        CodexProvider.Id => Codex,
        CopilotQuotaParser.ProviderId => Copilot,
        _ => Series[1],
    };

    public static Palette For(bool dark, string brand) => (brand, dark) switch
    {
        ("avanade", false) => AvanadeLight,
        ("avanade", true) => AvanadeDark,
        (_, false) => DefaultLight,
        _ => DefaultDark,
    };

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color[] Cs(params string[] hex) => hex.Select(C).ToArray();

    // Status colours follow Fluent's SystemFillColorSuccess / Caution / Critical.
    private static readonly Palette DefaultLight = new(
        false, "default",
        Good: C("#0F7B0F"), Warn: C("#9D5D00"), Bad: C("#C42B1C"), Neutral: C("#8A8A8A"),
        Track: C("#14000000"), Hatch: C("#2E000000"), Marker: C("#E4000000"),
        Text: C("#E4000000"), TextSecondary: C("#9E000000"), Grid: C("#14000000"),
        Hero: C("#E4000000"),
        Claude: C("#C15F3C"), Codex: C("#3C4048"), Copilot: C("#6E5BD9"),
        Series: Cs("#2A6DC8", "#C15F3C", "#6E5BD9", "#14A085", "#C98A00", "#0B8BB8", "#CE0569", "#5C5C5C"),
        TypeShades: Cs("#8A8F98", "#C9CDD3", "#5B6069", "#1F2329"));

    private static readonly Palette DefaultDark = new(
        true, "default",
        Good: C("#6CCB5F"), Warn: C("#FCE100"), Bad: C("#FF99A4"), Neutral: C("#9A9A9A"),
        Track: C("#17FFFFFF"), Hatch: C("#30FFFFFF"), Marker: C("#FFFFFFFF"),
        Text: C("#FFFFFFFF"), TextSecondary: C("#C5FFFFFF"), Grid: C("#14FFFFFF"),
        Hero: C("#FFFFFFFF"),
        Claude: C("#E08A6A"), Codex: C("#D5D8DE"), Copilot: C("#A99CF5"),
        Series: Cs("#60A5FA", "#E08A6A", "#A99CF5", "#4CC777", "#F2B33D", "#38BDF8", "#F472B6", "#9A9A9A"),
        TypeShades: Cs("#6B7079", "#3A3E45", "#A4AAB2", "#F2F3F5"));

    // Avanade: tokens from the Avanade design system (clients/avanade/avanade-design).
    // Accent fills/text meet contrast in each mode; raw #FF5800 is decoration only.
    private static readonly Palette AvanadeLight = new(
        false, "avanade",
        Good: C("#12A244"), Warn: C("#C98A00"), Bad: C("#C42B1C"), Neutral: C("#8A8A8A"),
        Track: C("#14000000"), Hatch: C("#2E000000"), Marker: C("#E41C1C1C"),
        Text: C("#FF1C1C1C"), TextSecondary: C("#FF666666"), Grid: C("#FFE5E5E5"),
        Hero: C("#C24400"),
        Claude: C("#FF5800"), Codex: C("#2A6DC8"), Copilot: C("#6250D6"),
        Series: Cs("#FF5800", "#2A6DC8", "#CE0569", "#C98A00", "#14A085", "#6250D6", "#C80000", "#0B8BB8"),
        TypeShades: Cs("#FF9A63", "#FFE4D6", "#C24400", "#7A2B00"),
        AccentFill: C("#C24400"), AccentText: C("#A83B00"), TextOnAccent: C("#FFFFFF"),
        Aurora: Cs("#CE0569", "#FF5800"));

    private static readonly Palette AvanadeDark = new(
        true, "avanade",
        Good: C("#4CC777"), Warn: C("#E0A526"), Bad: C("#FF99A4"), Neutral: C("#9A9A9A"),
        Track: C("#17FFFFFF"), Hatch: C("#30FFFFFF"), Marker: C("#FFFFFFFF"),
        Text: C("#FFFFFFFF"), TextSecondary: C("#C5FFFFFF"), Grid: C("#14FFFFFF"),
        Hero: C("#FF7A33"),
        Claude: C("#F05000"), Codex: C("#3D84E0"), Copilot: C("#8F84EA"),
        Series: Cs("#F05000", "#3D84E0", "#D9348A", "#B88000", "#14A085", "#8F84EA", "#E34948", "#1E92BF"),
        TypeShades: Cs("#FF9A63", "#4C2A1A", "#FFC2A1", "#FF5800"),
        AccentFill: C("#FF9A63"), AccentText: C("#FFC2A1"), TextOnAccent: C("#000000"),
        Aurora: Cs("#CE0569", "#FF5800"));
}
