using System.Diagnostics;
using AvaCodeStats.Core;
using AvaCodeStats.Core.Storage;
using AvaCodeStats.Core.Tokens;
using Microsoft.Win32;

namespace AvaCodeStats;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly UsageCollector _collector;
    private readonly System.Windows.Forms.Timer _pollTimer = new();
    private readonly System.Windows.Forms.Timer _repaintTimer = new() { Interval = 60_000 };
    private readonly ToolStripMenuItem _profileMenu = new("Profile");
    private readonly ToolStripMenuItem _trayChoice = new("Show in tray");
    private readonly ToolStripMenuItem _startup = new("Start with Windows");
    private AppSettings _settings;
    private DashboardForm? _dashboard;
    private Icon? _icon;
    private PaceStatus? _lastNotifiedStatus;

    /// <param name="openView">Open the dashboard on start at this view ("limits", "burn", "cost", "tokens").</param>
    public TrayContext(string? openView = null)
    {
        _settings = AppSettings.Load();
        _collector = new UsageCollector(new UsageStore(AppSettings.DatabasePath), new TokenStore(AppSettings.DatabasePath));
        _collector.LoadFromHistory();
        _collector.Updated += (_, _) => Render();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add("Refresh now", null, async (_, _) => await _collector.CollectAsync());
        menu.Items.Add(_profileMenu);
        menu.Items.Add(_trayChoice);
        menu.Items.Add(new ToolStripSeparator());
        _startup.Click += (_, _) => { StartupRegistration.Set(!StartupRegistration.IsEnabled); _startup.Checked = StartupRegistration.IsEnabled; };
        menu.Items.Add(_startup);
        menu.Items.Add("Open settings file", null, (_, _) => OpenSettings());
        menu.Items.Add("Reload settings", null, async (_, _) => await ApplySettingsAsync(AppSettings.Load()));
        menu.Items.Add("Open data folder", null, (_, _) => Process.Start("explorer.exe", AppSettings.DataDirectory));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => { _startup.Checked = StartupRegistration.IsEnabled; BuildProfileMenu(); BuildTrayChoices(); };

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "AvaCodeStats: loading…" };
        _tray.DoubleClick += (_, _) => ShowDashboard();

        _pollTimer.Tick += async (_, _) => await _collector.CollectAsync();
        _repaintTimer.Tick += (_, _) => Render(); // pace moves even when usage does not
        _repaintTimer.Start();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Render();
        _ = ApplySettingsAsync(_settings);
        if (openView is not null) ShowDashboard(openView);
    }

    private async Task ApplySettingsAsync(AppSettings settings)
    {
        _settings = settings;
        if (_settings.Subscriptions.Count == 0)
        {
            _settings.Subscriptions = await ProviderFactory.DiscoverAsync(CancellationToken.None);
            if (_settings.Subscriptions.Count > 0) _settings.Save();
        }

        var providers = new List<Core.Providers.IUsageProvider>();
        foreach (var s in _settings.Subscriptions.Where(s => s.Enabled))
        {
            try { providers.Add(ProviderFactory.Create(s, _collector.Tokens)); }
            catch (NotSupportedException) { /* unknown provider in settings: skip */ }
        }
        _collector.Providers = providers;

        _pollTimer.Interval = Math.Max(1, _settings.PollMinutes) * 60_000;
        _pollTimer.Start();
        Render();
        await _collector.CollectAsync();
    }

    private bool InProfile(UsageSnapshot s) => _settings.InActiveProfile(s.ProviderId, s.AccountId);

    private UsageSnapshot? TraySnapshot() =>
        _collector.PickTray(_settings.TrayKeys.GetValueOrDefault(_settings.ActiveProfile), InProfile);

    private void Render()
    {
        var snap = TraySnapshot();
        var size = SystemInformation.SmallIconSize.Width;
        var profile = Profiles.Label(_settings.ActiveProfile);
        Icon next;
        string tip;

        if (snap is null)
        {
            next = RingRenderer.CreateIcon(size, 0, 0, PaceStatus.Unknown);
            tip = $"AvaCodeStats ({profile}): " + (_collector.Errors.Count > 0 ? _collector.Errors.Values.First() : "no quota data yet");
        }
        else
        {
            var pace = PaceCalculator.Calculate(snap, DateTimeOffset.UtcNow, _settings.Thresholds);
            next = RingRenderer.CreateIcon(size, snap.UsedFraction, pace.PaceFraction, pace.Status);
            tip = $"[{profile}] {ProviderFactory.DisplayName(snap.ProviderId)} {snap.MetricName}\n" +
                  $"{snap.UsedFraction:P1} used · pace {pace.PaceFraction:P1}\n" +
                  $"{PaceCalculator.Describe(pace)}\n" +
                  $"Resets in {Fmt.Countdown(snap.PeriodEndUtc - DateTimeOffset.UtcNow)}";
            MaybeNotify(snap, pace);
        }

        _tray.Icon = next;
        RingRenderer.DestroyIcon(_icon);
        _icon = next;
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private void MaybeNotify(UsageSnapshot snap, PaceInfo pace)
    {
        var alarming = pace.Status is PaceStatus.FarOver or PaceStatus.Exhausted;
        if (_settings.NotifyWhenFarOverPace && alarming && _lastNotifiedStatus != pace.Status)
        {
            _tray.ShowBalloonTip(8000, $"{snap.MetricName}: {PaceCalculator.Describe(pace)}",
                $"{snap.UsedFraction:P0} used, resets in {Fmt.Countdown(snap.PeriodEndUtc - DateTimeOffset.UtcNow)}.",
                ToolTipIcon.Warning);
        }
        _lastNotifiedStatus = pace.Status;
    }

    private void BuildProfileMenu()
    {
        _profileMenu.DropDownItems.Clear();
        foreach (var p in Profiles.All)
        {
            var item = new ToolStripMenuItem(Profiles.Label(p)) { Checked = _settings.ActiveProfile == p };
            item.Click += (_, _) => SetProfile(p);
            _profileMenu.DropDownItems.Add(item);
        }
    }

    private void SetProfile(string profile)
    {
        _settings.ActiveProfile = profile;
        _settings.Save();
        _lastNotifiedStatus = null;
        Render();
        if (_dashboard is { IsDisposed: false }) { _dashboard.Close(); ShowDashboard(); }
    }

    private void BuildTrayChoices()
    {
        _trayChoice.DropDownItems.Clear();
        var current = TraySnapshot()?.Key;
        foreach (var s in _collector.Latest.Values.Where(s => InProfile(s) && !s.Unlimited && s.Entitlement > 0)
                     .OrderBy(s => s.ProviderId).ThenBy(s => s.AccountId).ThenBy(s => s.MetricName))
        {
            var account = s.AccountId == "default" ? "" : $" ({s.AccountId})";
            var item = new ToolStripMenuItem($"{ProviderFactory.DisplayName(s.ProviderId)}: {s.MetricName}{account}") { Checked = s.Key == current };
            var key = s.Key.ToString();
            item.Click += (_, _) => { _settings.TrayKeys[_settings.ActiveProfile] = key; _settings.Save(); Render(); };
            _trayChoice.DropDownItems.Add(item);
        }
        _trayChoice.Enabled = _trayChoice.DropDownItems.Count > 0;
    }

    private void ShowDashboard(string? view = null)
    {
        if (_dashboard is null || _dashboard.IsDisposed)
            _dashboard = new DashboardForm(_collector, () => _settings, Render);
        if (view is not null) _dashboard.ShowView(view);
        _dashboard.Show();
        if (_dashboard.WindowState == FormWindowState.Minimized) _dashboard.WindowState = FormWindowState.Normal;
        _dashboard.Activate();
    }

    private static void OpenSettings()
    {
        if (!File.Exists(AppSettings.SettingsPath)) new AppSettings().Save();
        Process.Start(new ProcessStartInfo(AppSettings.SettingsPath) { UseShellExecute = true });
    }

    private async void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) await _collector.CollectAsync();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _pollTimer.Dispose();
        _repaintTimer.Dispose();
        _dashboard?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        RingRenderer.DestroyIcon(_icon);
        base.ExitThreadCore();
    }
}
