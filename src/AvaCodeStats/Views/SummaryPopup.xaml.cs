using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvaCodeStats.Core;
using AvaCodeStats.Theming;

namespace AvaCodeStats.Views;

/// <summary>Popup above the tray: the tray profile's quotas at a glance. Closes when it loses focus.</summary>
public partial class SummaryPopup : Window
{
    private readonly AppHost _host;
    private bool _syncing;
    private bool _closing;

    internal SummaryPopup(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SourceInitialized += (_, _) => { ThemeService.ApplyTitleBar(this); ThemeService.RoundCorners(this); };
        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };
        _host.Changed += Refresh;
        ThemeService.Changed += Refresh;
        Closed += (_, _) => { _host.Changed -= Refresh; ThemeService.Changed -= Refresh; };
        Refresh();
    }

    internal void ShowNearTray()
    {
        // A flyout, not a window: at most three quarters of the screen; the list scrolls beyond that.
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height * 0.75);
        Show();
        UpdateLayout();
        // Bottom-right of the work area (above the taskbar), like the Windows flyouts.
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
        Activate();
    }

    private void Refresh()
    {
        var s = _host.Settings;
        _syncing = true;
        (s.TrayProfile switch { Profiles.Personal => Personal, Profiles.Unified => All, _ => Work }).IsChecked = true;
        _syncing = false;

        var items = LimitItem.For(_host, s.TrayProfile)
            .OrderByDescending(i => AlertRules.Severity(i.Status))
            .ThenByDescending(i => i.Snapshot.UsedFraction)
            .ToList();
        Items.ItemsSource = items;
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = $"No limits in the {Profiles.Label(s.TrayProfile)} profile yet.";
        Updated.Text = _host.Collector.LastCollectedUtc is { } t ? $"Updated {t.ToLocalTime():HH:mm}" : "Refreshing…";

        // Keep the bottom edge anchored when the height changes.
        if (IsVisible)
        {
            UpdateLayout();
            Top = SystemParameters.WorkArea.Bottom - ActualHeight - 12;
        }
    }

    private void OnProfile(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string profile } || _host.Settings.TrayProfile == profile) return;
        _host.Settings.TrayProfile = profile;
        _host.SaveSettings();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        await _host.RefreshAsync();
        RefreshButton.IsEnabled = true;
    }

    private void OnItem(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LimitItem item })
        {
            SafeClose();
            _host.ShowDashboard(item.Key);
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        SafeClose();
        _host.ShowDashboard();
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(Close);
    }
}
