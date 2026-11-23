using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AvaCodeStats.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AvaCodeStats.Tray;

/// <summary>
/// Tray icon: left click = summary popup, double click = dashboard, right click = menu.
/// The ring follows the tray profile (Work by default) and its chosen quota, else the one most over pace.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppHost _host;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.Timer _clickTimer = new() { Interval = Forms.SystemInformation.DoubleClickTime };
    private System.Drawing.Icon? _current;

    public TrayIcon(AppHost host)
    {
        _host = host;
        _icon = new Forms.NotifyIcon { Visible = true, Text = "AvaCodeStats" };
        // Wait out the double-click time so a double click opens only the dashboard.
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); _host.ToggleSummary(); };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _clickTimer.Start();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            _clickTimer.Stop();
            _host.ShowDashboard();
        };
        _host.Changed += Render;
        Theming.ThemeService.Changed += Render;
        Render();
    }

    private bool InTrayProfile(UsageSnapshot s) => _host.Settings.InProfile(_host.Settings.TrayProfile, s.ProviderId, s.AccountId);

    public UsageSnapshot? TraySnapshot() =>
        _host.Collector.PickTray(_host.Settings.TrayKeys.GetValueOrDefault(_host.Settings.TrayProfile), InTrayProfile, _host.Settings.Thresholds);

    private void Render()
    {
        var snap = TraySnapshot();
        var size = Forms.SystemInformation.SmallIconSize.Width;
        var profile = Profiles.Label(_host.Settings.TrayProfile);
        var dark = TaskbarIsDark();
        string tip;
        System.Drawing.Icon next;

        if (snap is null)
        {
            next = RingRenderer.CreateIcon(size, 0, 0, PaceStatus.Unknown, dark);
            tip = $"AvaCodeStats · {profile}\nNo quota data yet";
        }
        else
        {
            var pace = PaceCalculator.Calculate(snap, DateTimeOffset.UtcNow, _host.Settings.Thresholds);
            next = RingRenderer.CreateIcon(size, snap.UsedFraction, pace.PaceFraction, pace.Status, dark);
            tip = $"{ProviderFactory.DisplayName(snap.ProviderId)} {snap.MetricName} · {profile}\n" +
                  $"{1 - snap.UsedFraction:P0} left · {PaceCalculator.Describe(pace)}\n" +
                  $"Resets in {Fmt.Countdown(snap.PeriodEndUtc - DateTimeOffset.UtcNow)}";
        }

        _icon.Icon = next;
        RingRenderer.DestroyIcon(_current);
        _current = next;
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public void Notify(Alert alert) =>
        _icon.ShowBalloonTip(8000, alert.Title, alert.Message,
            alert.Level == AlertLevel.Warning ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);

    // ---------- menu ----------

    private Window? _menuOwner;

    /// <summary>
    /// A WPF (Fluent-styled) context menu. WPF menus only close on outside clicks when their owner
    /// window is active, so it is hosted by an invisible, activated helper window.
    /// </summary>
    private void ShowMenu()
    {
        _menuOwner ??= new Window
        {
            Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, Left = -10000, Top = -10000,
        };
        _menuOwner.Show();
        _menuOwner.Activate();

        var s = _host.Settings;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = _menuOwner };
        menu.Items.Add(Item("Open dashboard", (_, _) => _host.ShowDashboard(), bold: true));
        menu.Items.Add(Item("Summary", (_, _) => _host.ShowSummary()));
        menu.Items.Add(Item("Refresh now", async (_, _) => await _host.RefreshAsync()));
        menu.Items.Add(new Separator());

        var profile = new MenuItem { Header = "Tray profile" };
        foreach (var p in Profiles.All)
        {
            var key = p;
            profile.Items.Add(new MenuItem
            {
                Header = Profiles.Label(p), IsCheckable = true, IsChecked = s.TrayProfile == p,
            }.With(m => m.Click += (_, _) => { s.TrayProfile = key; _host.SaveSettings(); }));
        }
        menu.Items.Add(profile);

        var quota = new MenuItem { Header = "Tray shows" };
        var chosen = s.TrayKeys.GetValueOrDefault(s.TrayProfile);
        quota.Items.Add(new MenuItem { Header = "Most over pace (auto)", IsCheckable = true, IsChecked = chosen is null }
            .With(m => m.Click += (_, _) => { s.TrayKeys.Remove(s.TrayProfile); _host.SaveSettings(); }));
        foreach (var snap in _host.Collector.Latest.Values.Where(x => InTrayProfile(x) && !x.Unlimited && x.Entitlement > 0)
                     .OrderBy(x => x.ProviderId).ThenBy(x => x.AccountId).ThenBy(x => x.MetricName))
        {
            var key = snap.Key.ToString();
            var account = snap.AccountId == "default" ? "" : $" ({snap.AccountId})";
            quota.Items.Add(new MenuItem
            {
                Header = $"{ProviderFactory.DisplayName(snap.ProviderId)} · {snap.MetricName}{account}",
                IsCheckable = true, IsChecked = chosen == key,
            }.With(m => m.Click += (_, _) => { s.TrayKeys[s.TrayProfile] = key; _host.SaveSettings(); }));
        }
        menu.Items.Add(quota);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings", (_, _) => _host.ShowDashboard("settings")));
        menu.Items.Add(Item("Exit", (_, _) => _host.Exit()));

        menu.Closed += (_, _) => _menuOwner?.Hide();
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, RoutedEventHandler click, bool bold = false)
    {
        var item = new MenuItem { Header = header, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        item.Click += click;
        return item;
    }

    private static bool TaskbarIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is not int v || v == 0;
    }

    public void Dispose()
    {
        _clickTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        RingRenderer.DestroyIcon(_current);
        _menuOwner?.Close();
    }
}

internal static class FluentExtensions
{
    public static T With<T>(this T item, Action<T> configure)
    {
        configure(item);
        return item;
    }
}
