using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AvaCodeStats.Core;
using AvaCodeStats.Theming;
using AvaCodeStats.Tray;

namespace AvaCodeStats.Views;

/// <summary>A page hosted in the main window.</summary>
internal interface IPage
{
    string Title { get; }
    /// <summary>Whether the Work / Personal / All switch applies to this page.</summary>
    bool UsesProfile => true;
    void Refresh();
}

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly Dictionary<string, IPage> _pages = [];
    private IPage? _current;
    private bool _syncing;

    internal MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        FitToScreen();
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        _host.Changed += OnChanged;
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => { _host.Changed -= OnChanged; ThemeService.Changed -= OnThemeChanged; };
        SyncProfile();
        UpdateIcon();
        NavLimits.IsChecked = true;
    }

    /// <summary>
    /// Opens at the last size, or 1240×880, but never bigger than the work area (at 150% scaling a
    /// 1080p screen is only 1280×~800 DIPs).
    /// </summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        var s = _host.Settings;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Clamp(s.WindowWidth ?? 1240, MinWidth, area.Width * 0.95);
        Height = Math.Clamp(s.WindowHeight ?? 880, MinHeight, area.Height * 0.95);
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        Closing += (_, _) =>
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            s.WindowWidth = Math.Round(bounds.Width);
            s.WindowHeight = Math.Round(bounds.Height);
            s.WindowMaximized = WindowState == WindowState.Maximized;
            s.Save();
        };
    }

    internal void Navigate(string page)
    {
        RadioButton nav = page switch
        {
            "burn" => NavBurn,
            "cost" => NavCost,
            "tokens" => NavTokens,
            "settings" => NavSettings,
            _ => NavLimits,
        };
        if (nav.IsChecked == true) Show(page); else nav.IsChecked = true;
    }

    internal void ShowBurn(SubscriptionKey key)
    {
        Navigate("burn");
        (_current as BurnPage)?.Select(key);
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        var page = sender == NavBurn ? "burn" : sender == NavCost ? "cost" : sender == NavTokens ? "tokens" : sender == NavSettings ? "settings" : "limits";
        Show(page);
    }

    private void Show(string name)
    {
        if (!_pages.TryGetValue(name, out var page))
        {
            page = name switch
            {
                "burn" => new BurnPage(_host),
                "cost" => new AnalyticsPage(_host, AnalyticsPage.CostMode),
                "tokens" => new AnalyticsPage(_host, AnalyticsPage.TokensMode),
                "settings" => new SettingsPage(_host),
                _ => new LimitsPage(_host, this),
            };
            _pages[name] = page;
        }
        _current = page;
        Host.Content = page;
        PageTitle.Text = page.Title;
        ProfileSwitch.Visibility = page.UsesProfile ? Visibility.Visible : Visibility.Hidden;
        page.Refresh();
        UpdateStatus();
    }

    private void OnChanged()
    {
        SyncProfile();
        _current?.Refresh();
        UpdateStatus();
        UpdateIcon();
    }

    private void OnThemeChanged()
    {
        foreach (var p in _pages.Values) p.Refresh();
        UpdateIcon();
    }

    private void SyncProfile()
    {
        _syncing = true;
        (_host.Settings.ActiveProfile switch
        {
            Profiles.Personal => ProfilePersonal,
            Profiles.Unified => ProfileUnified,
            _ => ProfileWork,
        }).IsChecked = true;
        _syncing = false;
    }

    private void OnProfile(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string profile } || _host.Settings.ActiveProfile == profile) return;
        _host.Settings.ActiveProfile = profile;
        _host.SaveSettings();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        await _host.RefreshAsync();
        RefreshButton.IsEnabled = true;
    }

    private void UpdateStatus()
    {
        var s = _host.Settings;
        var errors = _host.Collector.Errors
            .Where(e => e.Key.Split(':') is [var p, var a] && (_current?.UsesProfile != true || s.InActiveProfile(p, a)))
            .Select(e => $"{ProviderFactory.DisplayName(e.Key.Split(':')[0])}: {e.Value}")
            .ToList();
        var updated = _host.Collector.LastCollectedUtc is { } t ? $"Updated {t.ToLocalTime():HH:mm}" : "Refreshing…";
        Status.Text = errors.Count > 0 ? $"{updated}  ·  {string.Join("  ·  ", errors)}" : updated;
        Status.Foreground = errors.Count > 0
            ? (System.Windows.Media.Brush)FindResource("StatusWarnBrush")
            : (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
    }

    /// <summary>Window and taskbar icon: the ring of the tray profile's quota.</summary>
    private void UpdateIcon()
    {
        var s = _host.Settings;
        var snap = _host.Collector.PickTray(s.TrayKeys.GetValueOrDefault(s.TrayProfile),
            x => s.InProfile(s.TrayProfile, x.ProviderId, x.AccountId), s.Thresholds);
        var pace = snap is null ? null : PaceCalculator.Calculate(snap, DateTimeOffset.UtcNow, s.Thresholds);
        var icon = RingRenderer.CreateIcon(64, snap?.UsedFraction ?? 0, pace?.PaceFraction ?? 0, pace?.Status ?? PaceStatus.Unknown, ThemeService.Current.IsDark);
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        RingRenderer.DestroyIcon(icon);
        Icon = source;
        AppIcon.Source = source;
    }
}
