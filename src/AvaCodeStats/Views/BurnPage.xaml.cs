using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AvaCodeStats.Controls;
using AvaCodeStats.Core;
using AvaCodeStats.Core.Providers.GitHubCopilot;
using AvaCodeStats.Theming;

namespace AvaCodeStats.Views;

public partial class BurnPage : UserControl, IPage
{
    private readonly AppHost _host;
    private SubscriptionKey? _selected;
    private bool _filling;

    internal BurnPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
    }

    public string Title => "Burn";

    internal void Select(SubscriptionKey key)
    {
        _selected = key;
        Refresh();
    }

    private sealed record Choice(UsageSnapshot Snapshot, string Label)
    {
        public override string ToString() => Label;
    }

    private Choice ChoiceFor(UsageSnapshot x) => new(x,
        $"{ProviderFactory.DisplayName(x.ProviderId)} · {x.MetricName}" + Privacy.Suffix(_host.Settings, x.ProviderId, x.AccountId, "  (") + (x.AccountId == "default" ? "" : ")"));

    private sealed record Kpi(string Label, string Value, string Note, Brush Brush);

    public void Refresh()
    {
        var s = _host.Settings;
        var items = LimitItem.For(_host, s.ActiveProfile);
        _selected ??= _host.Collector.PickTray(s.TrayKeys.GetValueOrDefault(s.ActiveProfile),
            x => s.InActiveProfile(x.ProviderId, x.AccountId), s.Thresholds)?.Key;

        _filling = true;
        Picker.ItemsSource = items.Select(i => ChoiceFor(i.Snapshot)).ToList();
        var index = items.ToList().FindIndex(i => i.Key == _selected);
        Picker.SelectedIndex = items.Count == 0 ? -1 : Math.Max(0, index);
        _filling = false;

        if (Picker.SelectedItem is Choice c) { _selected = c.Snapshot.Key; Draw(c.Snapshot); }
        else
        {
            Body.Visibility = Visibility.Collapsed;
            Empty.Visibility = Visibility.Visible;
            Empty.Text = $"No quotas in the {Profiles.Label(s.ActiveProfile)} profile yet.";
            Plan.Text = "";
        }
    }

    private void OnPick(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Picker.SelectedItem is not Choice c) return;
        _selected = c.Snapshot.Key;
        Draw(c.Snapshot);
    }

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && Picker.SelectedItem is Choice c) Draw(c.Snapshot);
    }

    private static bool IsPercent(UsageSnapshot s) => s.Entitlement == 100 && s.ProviderId != CopilotQuotaParser.ProviderId;
    private static string Amount(UsageSnapshot s, double v) => IsPercent(s) ? $"{v:0.#}%" : $"{v:N0}";

    private void Draw(UsageSnapshot snap)
    {
        Body.Visibility = Visibility.Visible;
        Empty.Visibility = Visibility.Collapsed;

        var p = ThemeService.Current;
        var now = DateTimeOffset.UtcNow;
        var pace = PaceCalculator.Calculate(snap, now, _host.Settings.Thresholds);
        var samples = _host.Collector.Store.GetSamples(snap.Key, snap.PeriodStartUtc, snap.PeriodEndUtc);
        var last24 = UsageAnalytics.BurnedWithin(samples, snap, TimeSpan.FromHours(24));
        var status = ThemeService.Brush(p.ForStatus(pace.Status));
        var text = (Brush)FindResource("TextFillColorPrimaryBrush");
        var item = new LimitItem(snap, pace, now);

        Ring.Used = snap.UsedFraction;
        Ring.Pace = pace.PaceFraction;
        Ring.Status = pace.Status;
        RingValue.Text = $"{snap.UsedFraction * 100:0}%";
        RingCaption.Text = $"used · pace {pace.PaceFraction * 100:0}%";

        HeadlineGlyph.Text = item.StatusGlyph;
        HeadlineGlyph.Foreground = status;
        Headline.Text = PaceCalculator.Describe(pace);
        Headline.Foreground = status;
        var start = snap.PeriodStartUtc.ToLocalTime();
        var end = snap.PeriodEndUtc.ToLocalTime();
        Period.Text = $"Period {start:ddd d MMM HH:mm} – {end:ddd d MMM HH:mm}";
        Plan.Text = string.Join("  ·  ", new[] { snap.AccountId == "default" ? null : Privacy.Account(_host.Settings, snap.ProviderId, snap.AccountId), snap.PlanName }.Where(x => x is not null));

        var shortWindow = (snap.PeriodEndUtc - snap.PeriodStartUtc).TotalDays < 1.5;
        Brush? Warn(bool b) => b ? (Brush)FindResource("StatusWarnBrush") : null;
        Kpis.ItemsSource = new[]
        {
            new Kpi("Used", Amount(snap, snap.Used), IsPercent(snap) ? "of the limit" : $"of {snap.Entitlement:N0}", text),
            new Kpi("Left", Amount(snap, snap.Remaining), $"{1 - snap.UsedFraction:P0} of the quota", text),
            new Kpi("Even pace now", Amount(snap, pace.ExpectedUsed), $"{pace.PaceFraction:P0} of the period gone", text),
            new Kpi("Versus pace", $"{(pace.Delta >= 0 ? "+" : "−")}{Amount(snap, Math.Abs(pace.Delta))}",
                shortWindow ? (pace.Delta >= 0 ? "over" : "in hand")
                : pace.DaysOverPace >= 0 ? $"{pace.DaysOverPace:0.0} days ahead of pace" : $"{-pace.DaysOverPace:0.0} days in hand", status),
            new Kpi("Budget per day", Amount(snap, pace.DailyBudgetLeft), $"even pace is {Amount(snap, pace.DailyAllowance)}",
                Warn(pace.DailyBudgetLeft < pace.DailyAllowance) ?? text),
            new Kpi("At reset", $"{pace.ProjectedUsedAtReset / snap.Entitlement:P0}",
                pace.ProjectedExhaustionUtc is { } ex ? $"runs out {ex.ToLocalTime():ddd d MMM HH:mm}" : "at the current average",
                pace.ProjectedExhaustionUtc is null ? text : (Brush)FindResource("StatusBadBrush")),
            new Kpi("Last 24 hours", last24 is { } b ? Amount(snap, b) : "–",
                last24 is { } b2 && pace.DailyAllowance > 0 ? $"{b2 / pace.DailyAllowance:P0} of a day's pace" : "collecting history…",
                Warn(last24 is { } b3 && b3 > pace.DailyBudgetLeft) ?? text),
            new Kpi("Resets in", Fmt.Countdown(snap.PeriodEndUtc - now), $"{end:ddd d MMM HH:mm}", text),
        };

        Chart.Model = TabDaily.IsChecked == true ? DailyModel(snap, samples, pace)
            : TabHistory.IsChecked == true ? HistoryModel(snap)
            : PeriodModel(snap, samples, pace, now);
    }

    private static ChartModel PeriodModel(UsageSnapshot snap, IReadOnlyList<UsageSnapshot> samples, PaceInfo pace, DateTimeOffset now)
    {
        var p = ThemeService.Current;
        var start = snap.PeriodStartUtc.LocalDateTime.ToOADate();
        var end = snap.PeriodEndUtc.LocalDateTime.ToOADate();
        var nowX = now.LocalDateTime.ToOADate();
        var status = p.ForStatus(pace.Status);

        // Usage is 0 at the reset; then every reading; then now.
        var xs = new List<double> { start };
        var ys = new List<double> { 0 };
        foreach (var s in samples) { xs.Add(s.TimestampUtc.LocalDateTime.ToOADate()); ys.Add(s.Used); }
        xs.Add(nowX); ys.Add(snap.Used);

        var m = new ChartModel
        {
            XKind = XAxisKind.Days, XMin = start, XMax = end, NowX = nowX,
            YMax = Math.Max(snap.Entitlement, pace.ProjectedUsedAtReset) * 1.05,
            FormatY = v => Amount(snap, v), FormatValue = v => Amount(snap, v),
        };
        m.Series.Add(new ChartSeries { Name = "Used", Color = status, X = xs.ToArray(), Y = ys.ToArray(), Kind = SeriesKind.Area, Thickness = 2.5 });
        m.Series.Add(new ChartSeries { Name = "Even pace", Color = p.TextSecondary, X = [start, end], Y = [0, snap.Entitlement], Dashed = true, Thickness = 1.5 });
        m.Series.Add(new ChartSeries { Name = "Projection", Color = status, X = [nowX, end], Y = [snap.Used, pace.ProjectedUsedAtReset], Dashed = true, Thickness = 2, InTooltip = false });
        m.Lines.Add(new ReferenceLine(snap.Entitlement, $"Quota {Amount(snap, snap.Entitlement)}", p.Bad));
        return m;
    }

    private static ChartModel DailyModel(UsageSnapshot snap, IReadOnlyList<UsageSnapshot> samples, PaceInfo pace)
    {
        var p = ThemeService.Current;
        var days = UsageAnalytics.DailyBurns(samples.Append(snap).ToList(), snap.PeriodStartUtc, snap.PeriodEndUtc, TimeZoneInfo.Local);
        var first = snap.PeriodStartUtc.LocalDateTime.Date;
        var last = snap.PeriodEndUtc.LocalDateTime.Date;
        var m = new ChartModel
        {
            XKind = XAxisKind.Days, XMin = first.ToOADate(), XMax = last.AddDays(1).ToOADate(), BarWidth = 0.72,
            YMax = Math.Max(Math.Max(pace.DailyAllowance, pace.DailyBudgetLeft) * 1.3, days.Select(d => d.Burned).DefaultIfEmpty(0).Max() * 1.1),
            FormatY = v => Amount(snap, v), FormatValue = v => Amount(snap, v),
        };
        m.Series.Add(new ChartSeries
        {
            Name = "Burned", Color = p.Good, Kind = SeriesKind.Bars,
            X = days.Select(d => d.Day.ToDateTime(new TimeOnly(12, 0)).ToOADate()).ToArray(),
            Y = days.Select(d => d.Burned).ToArray(),
            BarColors = days.Select(d => d.Estimated ? Color.FromArgb(120, p.Neutral.R, p.Neutral.G, p.Neutral.B)
                : d.Burned > pace.DailyAllowance * 1.5 ? p.Bad : d.Burned > pace.DailyAllowance ? p.Warn : p.Good).ToArray(),
            InLegend = false,
        });
        m.LegendExtras.Add(("Under pace", p.Good, true));
        m.LegendExtras.Add(("Over pace", p.Warn, true));
        m.LegendExtras.Add(("Well over", p.Bad, true));
        if (days.Any(d => d.Estimated)) m.LegendExtras.Add(("Estimated (no readings)", Color.FromArgb(120, p.Neutral.R, p.Neutral.G, p.Neutral.B), true));
        m.Lines.Add(new ReferenceLine(pace.DailyAllowance, $"Even pace {Amount(snap, pace.DailyAllowance)}/day", p.TextSecondary));
        m.Lines.Add(new ReferenceLine(pace.DailyBudgetLeft, $"Budget left {Amount(snap, pace.DailyBudgetLeft)}/day", p.Series[0]));
        return m;
    }

    private ChartModel HistoryModel(UsageSnapshot snap)
    {
        var p = ThemeService.Current;
        var monthly = (snap.PeriodEndUtc - snap.PeriodStartUtc).TotalDays >= 27;
        var periods = _host.Collector.Store.GetPeriodSummaries(snap.Key).TakeLast(12).ToList();
        var labels = periods.Select(x => x.PeriodStartUtc.ToLocalTime().ToString(monthly ? "MMM yyyy" : "d MMM")).ToArray();
        var m = new ChartModel
        {
            XKind = XAxisKind.Categories, Categories = labels, XMin = -0.6, XMax = Math.Max(periods.Count, 4) - 0.4,
            YMax = 110, BarWidth = 0.55, FormatY = v => $"{v:0}%", FormatValue = v => $"{v:0.#}% used",
            EmptyText = "History builds up as periods complete.",
        };
        m.Series.Add(new ChartSeries
        {
            Name = "Used at end of period", Color = p.Series[0], Kind = SeriesKind.Bars,
            X = periods.Select((_, i) => (double)i).ToArray(),
            Y = periods.Select(x => x.UsedFraction * 100).ToArray(),
            BarColors = periods.Select(x => x.PeriodStartUtc == snap.PeriodStartUtc ? p.Series[0]
                : x.UsedFraction >= 1 ? p.Bad : x.UsedFraction >= 0.85 ? p.Warn : p.Good).ToArray(),
            BarLabels = periods.Select(x => IsPercent(snap) ? $"{x.UsedFraction * 100:0}%" : $"{x.Used:N0}").ToArray(),
            InLegend = false,
        });
        m.LegendExtras.Add(("This period", p.Series[0], true));
        m.LegendExtras.Add(("Earlier periods", p.Good, true));
        m.Lines.Add(new ReferenceLine(100, "Whole quota", p.Bad));
        return m;
    }
}