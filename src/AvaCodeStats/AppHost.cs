using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AvaCodeStats.Core;
using AvaCodeStats.Core.Storage;
using AvaCodeStats.Core.Tokens;
using AvaCodeStats.Theming;
using AvaCodeStats.Tray;
using AvaCodeStats.Views;
using Microsoft.Win32;

namespace AvaCodeStats;

/// <summary>Owns settings, polling, alerts, the tray icon and the windows.</summary>
internal sealed class AppHost : IDisposable
{
    private readonly DispatcherTimer _poll = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMinutes(1) };
    private TrayIcon? _tray;
    private MainWindow? _main;
    private SummaryPopup? _summary;
    private DateTime _summaryClosedAt;

    public AppSettings Settings { get; private set; } = AppSettings.Load();
    public UsageCollector Collector { get; }

    /// <summary>New data, a settings change, or the minute tick (countdowns and pace move with time).</summary>
    public event Action? Changed;

    public AppHost()
    {
        Collector = new UsageCollector(new UsageStore(AppSettings.DatabasePath), new TokenStore(AppSettings.DatabasePath));
        Collector.LoadFromHistory();
        Collector.Updated += (_, _) => Application.Current.Dispatcher.BeginInvoke(OnCollected);
    }

    public void Start()
    {
        ThemeService.Initialise(Settings);
        _tray = new TrayIcon(this);
        _poll.Tick += async (_, _) => await RefreshAsync();
        _tick.Tick += (_, _) => RaiseChanged();
        _tick.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _ = ApplySettingsAsync();
    }

    public Task RefreshAsync() => Collector.CollectAsync();

    /// <summary>Saves settings and updates everything that depends on them.</summary>
    public void SaveSettings(bool providersChanged = false, bool themeChanged = false)
    {
        Settings.Save();
        if (themeChanged) ThemeService.Apply(Settings);
        if (providersChanged) _ = ApplySettingsAsync();
        else RaiseChanged();
    }

    public void ReloadSettings()
    {
        Settings = AppSettings.Load();
        ThemeService.Apply(Settings);
        _ = ApplySettingsAsync();
    }

    private async Task ApplySettingsAsync()
    {
        if (Settings.Subscriptions.Count == 0)
        {
            Settings.Subscriptions = await ProviderFactory.DiscoverAsync(CancellationToken.None);
            if (Settings.Subscriptions.Count > 0) Settings.Save();
        }
        else if (ProviderFactory.AddNewLocalTools(Settings.Subscriptions))
        {
            Settings.Save();
        }

        var providers = new List<Core.Providers.IUsageProvider>();
        foreach (var s in Settings.Subscriptions.Where(s => s.Enabled))
        {
            try { providers.Add(ProviderFactory.Create(s, Collector.Tokens, Settings.CodexLiveUsage)); }
            catch (NotSupportedException) { /* unknown provider in settings: skip */ }
        }
        Collector.Providers = providers;

        _poll.Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.PollMinutes));
        _poll.Start();
        RaiseChanged();
        await RefreshAsync();
    }

    private void OnCollected()
    {
        var enabled = Settings.Subscriptions.Where(s => s.Enabled).Select(s => s.Source).ToHashSet();
        var alerts = AlertRules.Evaluate(
            Collector.Latest.Values.Where(s => enabled.Contains($"{s.ProviderId}:{s.AccountId}")),
            Settings, DateTimeOffset.UtcNow,
            key => Collector.Tokens.GetValue(key) is not null,
            key => Collector.Tokens.SetValue(key, DateTimeOffset.UtcNow.ToString("O")));
        foreach (var a in alerts.Take(3)) _tray?.Notify(a);
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    // ---------- windows ----------

    public void ShowDashboard(string? page = null)
    {
        _summary?.Close();
        if (_main is null)
        {
            _main = new MainWindow(this);
            _main.Closed += (_, _) => _main = null;
        }
        if (page is not null) _main.Navigate(page);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ShowDashboard(SubscriptionKey burnKey)
    {
        ShowDashboard("burn");
        _main?.ShowBurn(burnKey);
    }

    public void ToggleSummary()
    {
        if (_summary is not null) { _summary.Close(); return; }
        // A tray click that dismissed the popup (by deactivating it) should not reopen it.
        // (The tray waits out the double-click time before a single click lands here.)
        if ((DateTime.UtcNow - _summaryClosedAt).TotalMilliseconds < System.Windows.Forms.SystemInformation.DoubleClickTime + 400) return;
        ShowSummary();
    }

    public void ShowSummary()
    {
        if (_summary is not null) { _summary.Activate(); return; }
        _summary = new SummaryPopup(this);
        _summary.Closed += (_, _) => { _summary = null; _summaryClosedAt = DateTime.UtcNow; };
        _summary.ShowNearTray();
    }

    public static void OpenSettingsFile()
    {
        if (!System.IO.File.Exists(AppSettings.SettingsPath)) new AppSettings().Save();
        Process.Start(new ProcessStartInfo(AppSettings.SettingsPath) { UseShellExecute = true });
    }

    public static void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", AppSettings.DataDirectory) { UseShellExecute = true });

    public void Exit()
    {
        _summary?.Close();
        _main?.Close();
        Application.Current.Shutdown();
    }

    private async void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) await RefreshAsync();
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _poll.Stop();
        _tick.Stop();
        _tray?.Dispose();
    }
}
