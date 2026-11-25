using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AvaCodeStats.Core;
using Microsoft.Win32;

namespace AvaCodeStats.Theming;

/// <summary>
/// Applies light / dark / system mode (WPF Fluent theme) and the brand palette, and publishes the
/// palette as resources ("StatusGoodBrush", "HeroBrush", …) for XAML and as <see cref="Current"/> for code.
/// </summary>
internal static class ThemeService
{
    private static AppSettings? _settings;
    private static bool _subscribed;

    public static Palette Current { get; private set; } = Palette.For(true, "default");

    /// <summary>Raised after the palette changes; views redraw custom visuals and charts.</summary>
    public static event Action? Changed;

    public static void Initialise(AppSettings settings)
    {
        _settings = settings;
        if (!_subscribed)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _subscribed = true;
        }
        Apply(settings);
    }

    public static void Shutdown()
    {
        if (!_subscribed) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _subscribed = false;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _settings is { ThemeMode: "system" } s
            && Application.Current is { Dispatcher: { } d })
        {
            d.BeginInvoke(() => Apply(s));
        }
    }

    public static void Apply(AppSettings settings)
    {
        _settings = settings;
        var app = Application.Current;
        if (app is null) return;
        app.ThemeMode = settings.ThemeMode switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        var dark = settings.ThemeMode switch { "light" => false, "dark" => true, _ => SystemUsesDarkTheme() };
        var p = Current = Palette.For(dark, settings.Brand);

        var r = app.Resources;
        r["StatusGoodBrush"] = Brush(p.Good);
        r["StatusWarnBrush"] = Brush(p.Warn);
        r["StatusBadBrush"] = Brush(p.Bad);
        r["StatusNeutralBrush"] = Brush(p.Neutral);
        r["HeroBrush"] = Brush(p.Hero);
        r["TrackBrush"] = Brush(p.Track);
        r["ClaudeBrush"] = Brush(p.Claude);
        r["CodexBrush"] = Brush(p.Codex);
        r["CopilotBrush"] = Brush(p.Copilot);

        var aurora = p.Aurora is { Length: > 1 } a
            ? new LinearGradientBrush(a[0], a[^1], 0)
            : new LinearGradientBrush(Colors.Transparent, Colors.Transparent, 0);
        aurora.Freeze();
        r["AuroraBrush"] = aurora;
        r["AuroraVisibility"] = p.Aurora is null ? Visibility.Collapsed : Visibility.Visible;

        // Brand accent: override the Fluent accent brushes (controls use them directly).
        string[] accentFill = ["AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush"];
        string[] accentText = ["AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush"];
        string[] onAccent = ["TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush"];
        if (p.AccentFill is { } fill)
        {
            r["SystemAccentColor"] = fill;
            for (var i = 0; i < accentFill.Length; i++) r[accentFill[i]] = Brush(fill, (byte)(255 - i * 25));
            foreach (var k in accentText) r[k] = Brush(p.AccentText ?? fill);
            foreach (var k in onAccent) r[k] = Brush(p.TextOnAccent ?? Colors.White);
        }
        else
        {
            foreach (var k in accentFill.Concat(accentText).Concat(onAccent).Append("SystemAccentColor")) r.Remove(k);
        }

        foreach (Window w in app.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    /// <summary>Dark or light caption bar to match the theme (Windows 10 20H1+ / 11).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var on = Current.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    /// <summary>Rounded corners for borderless windows (Windows 11).</summary>
    public static void RoundCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
    }

    public static SolidColorBrush Brush(Color c, byte? alpha = null)
    {
        var b = new SolidColorBrush(alpha is { } a ? Color.FromArgb(a, c.R, c.G, c.B) : c);
        b.Freeze();
        return b;
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
