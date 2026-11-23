using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AvaCodeStats.Core;
using AvaCodeStats.Theming;

namespace AvaCodeStats.Views;

public partial class SettingsPage : UserControl, IPage
{
    private readonly AppHost _host;
    private bool _loading;

    internal SettingsPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        PollMinutes.ItemsSource = new[] { 1, 2, 5, 10, 15, 30, 60 }.Select(m => new Choice(m == 1 ? "1 minute" : $"{m} minutes", m)).ToList();
    }

    public string Title => "Settings";
    public bool UsesProfile => false;

    private sealed record Choice(string Label, object Value)
    {
        public override string ToString() => Label;
    }

    /// <summary>Editable view of one subscription; edits save immediately.</summary>
    private sealed class SubscriptionRow(SubscriptionConfig config, AppHost host)
    {
        public string Name => ProviderFactory.DisplayName(config.Provider) + (config.Account == "default" ? "" : $" · {config.Account}");
        public string Source => config.Provider + ":" + config.Account;
        public Brush Dot => ThemeService.Brush(ThemeService.Current.ForProvider(config.Provider));
        public string[] ProfileChoices { get; } = ["Work", "Personal"];

        public string Profile
        {
            get => Profiles.Label(config.Profile);
            set { config.Profile = value == "Work" ? Profiles.Work : Profiles.Personal; host.SaveSettings(); }
        }

        public bool Enabled
        {
            get => config.Enabled;
            set { config.Enabled = value; host.SaveSettings(providersChanged: true); }
        }
    }

    public void Refresh()
    {
        if (IsLoaded && IsKeyboardFocusWithin) return; // the user is editing; the controls already show their choice
        _loading = true;
        var s = _host.Settings;

        (s.ThemeMode switch { "light" => ThemeLight, "dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true;
        (s.Brand == "avanade" ? BrandAvanade : BrandDefault).IsChecked = true;
        (s.TrayProfile switch { Profiles.Personal => TrayPersonal, Profiles.Unified => TrayUnified, _ => TrayWork }).IsChecked = true;

        var quotas = new List<Choice> { new("Automatic (most over pace)", "") };
        quotas.AddRange(_host.Collector.Latest.Values
            .Where(x => !x.Unlimited && x.Entitlement > 0 && s.InProfile(s.TrayProfile, x.ProviderId, x.AccountId))
            .OrderBy(x => x.ProviderId).ThenBy(x => x.AccountId).ThenBy(x => x.MetricName)
            .Select(x => new Choice($"{ProviderFactory.DisplayName(x.ProviderId)} · {x.MetricName}" + (x.AccountId == "default" ? "" : $" ({x.AccountId})"), x.Key.ToString())));
        TrayQuota.ItemsSource = quotas;
        var chosen = s.TrayKeys.GetValueOrDefault(s.TrayProfile) ?? "";
        TrayQuota.SelectedItem = quotas.FirstOrDefault(q => (string)q.Value == chosen) ?? quotas[0];
        StartWithWindows.IsChecked = StartupRegistration.IsEnabled;

        WarnSlider.Value = s.WarnAtPercent;
        CriticalSlider.Value = s.CriticalAtPercent;
        WarnValue.Text = Percent(s.WarnAtPercent);
        CriticalValue.Text = Percent(s.CriticalAtPercent);
        FarOverPace.IsChecked = s.NotifyWhenFarOverPace;

        Subscriptions.ItemsSource = s.Subscriptions.Select(c => new SubscriptionRow(c, _host)).ToList();

        var polls = (List<Choice>)PollMinutes.ItemsSource;
        PollMinutes.SelectedItem = polls.FirstOrDefault(p => (int)p.Value == s.PollMinutes) ?? polls[2];
        CodexLive.IsChecked = s.CodexLiveUsage;
        _loading = false;
    }

    private static string Percent(int v) => v == 0 ? "Off" : $"{v}%";

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string mode }) return;
        _host.Settings.ThemeMode = mode;
        _host.SaveSettings(themeChanged: true);
    }

    private void OnBrand(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string brand }) return;
        _host.Settings.Brand = brand;
        _host.SaveSettings(themeChanged: true);
    }

    private void OnTrayProfile(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string profile }) return;
        _host.Settings.TrayProfile = profile;
        _host.SaveSettings();
    }

    private void OnTrayQuota(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TrayQuota.SelectedItem is not Choice { Value: string key }) return;
        var s = _host.Settings;
        if (key.Length == 0) s.TrayKeys.Remove(s.TrayProfile); else s.TrayKeys[s.TrayProfile] = key;
        _host.SaveSettings();
    }

    private void OnStartWithWindows(object sender, RoutedEventArgs e) => StartupRegistration.Set(StartWithWindows.IsChecked == true);

    private void OnThreshold(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        var s = _host.Settings;
        s.WarnAtPercent = (int)WarnSlider.Value;
        s.CriticalAtPercent = (int)CriticalSlider.Value;
        WarnValue.Text = Percent(s.WarnAtPercent);
        CriticalValue.Text = Percent(s.CriticalAtPercent);
        s.Save(); // no full refresh while dragging
    }

    private void OnFarOverPace(object sender, RoutedEventArgs e)
    {
        _host.Settings.NotifyWhenFarOverPace = FarOverPace.IsChecked == true;
        _host.Settings.Save();
    }

    private void OnPoll(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PollMinutes.SelectedItem is not Choice { Value: int minutes }) return;
        _host.Settings.PollMinutes = minutes;
        _host.SaveSettings(providersChanged: true);
    }

    private void OnCodexLive(object sender, RoutedEventArgs e)
    {
        _host.Settings.CodexLiveUsage = CodexLive.IsChecked == true;
        _host.SaveSettings(providersChanged: true);
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => AppHost.OpenSettingsFile();
    private void OnReload(object sender, RoutedEventArgs e) => _host.ReloadSettings();
    private void OnOpenData(object sender, RoutedEventArgs e) => AppHost.OpenDataFolder();
}
