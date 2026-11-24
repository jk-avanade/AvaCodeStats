using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvaCodeStats.Core;

namespace AvaCodeStats.Views;

public partial class LimitsPage : UserControl, IPage
{
    private readonly AppHost _host;
    private readonly MainWindow _window;

    internal LimitsPage(AppHost host, MainWindow window)
    {
        _host = host;
        _window = window;
        InitializeComponent();
    }

    public string Title => "Limits";

    public void Refresh()
    {
        var s = _host.Settings;
        var items = LimitItem.For(_host, s.ActiveProfile);
        Groups.ItemsSource = LimitGroup.From(items, s, s.ActiveProfile == Profiles.Unified, s.CollapsedGroups);

        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = _host.Collector.Errors.Count > 0
            ? "No limits in this profile yet. " + string.Join("  ", _host.Collector.Errors.Values)
            : $"No limits in the {Profiles.Label(s.ActiveProfile)} profile yet. Assign subscriptions to it in Settings.";
    }

    private void OnGroupClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LimitGroup { CanCollapse: true } group }) return;
        group.Toggle(_host.Settings.CollapsedGroups);
        _host.SaveSettings(); // raises Changed, which rebuilds the list
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LimitItem item }) _window.ShowBurn(item.Key);
    }
}
