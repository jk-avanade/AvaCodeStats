using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AvaCodeStats.Controls;
using AvaCodeStats.Core;
using AvaCodeStats.Core.Providers.GitHubCopilot;
using AvaCodeStats.Theming;
using SP = ScottPlot;

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

    private sealed record Choice(UsageSnapshot Snapshot)
    {
        public override string ToString() =>
            $"{ProviderFactory.DisplayName(Snapshot.ProviderId)} · {Snapshot.MetricName}" + (Snapshot.AccountId == "default" ? "" : $"  ({Snapshot.AccountId})");
    }

    private sealed record Kpi(string Label, string Value, string Note, Brush Brush);

    public void Refresh()
    {
        var s = _host.Settings;
        var items = LimitItem.For(_host, s.ActiveProfile);
        _selected ??= _host.Collector.PickTray(s.TrayKeys.GetValueOrDefault(s.ActiveProfile),
            x => s.InActiveProfile(x.ProviderId, x.AccountId), s.Thresholds)?.Key;

        _filling = true;
        Picker.ItemsSource = items.Select(i => new Choice(i.Snapshot)).ToList();
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
        Plan.Text = string.Join("  ·  ", new[] { snap.AccountId == "default" ? null : snap.AccountId, snap.PlanName }.Where(x => x is not null));

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

        // A fresh plot each time: the tabs use different axis types (dates vs. periods), and a
        // date axis cannot be switched back to numeric ticks in place.
        Chart.Reset();
        var plot = Chart.Plot;
        ChartStyle.Apply(plot);
        if (TabDaily.IsChecked == true) DrawDaily(plot, snap, samples, pace);
        else if (TabHistory.IsChecked == true) DrawHistory(plot, snap);
        else DrawPeriod(plot, snap, samples, pace, now);
        Chart.Refresh();
    }

    private static SP.Color Sp(Color c, byte? a = null) => ChartStyle.Sp(c, a);

    private static void DrawPeriod(SP.Plot plot, UsageSnapshot snap, IReadOnlyList<UsageSnapshot> samples, PaceInfo pace, DateTimeOffset now)
    {
        var p = ThemeService.Current;
        var start = snap.PeriodStartUtc.LocalDateTime;
        var end = snap.PeriodEndUtc.LocalDateTime;
        var nowLocal = now.LocalDateTime;

        var quota = plot.Add.HorizontalLine(snap.Entitlement);
        quota.Color = Sp(p.Bad, 140);
        quota.LinePattern = SP.LinePattern.Dashed;
        quota.LineWidth = 1;
        quota.LegendText = "Quota";

        var even = plot.Add.Scatter(new[] { start, end }, new[] { 0, snap.Entitlement });
        even.LegendText = "Even pace";
        even.Color = Sp(p.TextSecondary);
        even.LinePattern = SP.LinePattern.Dashed;
        even.MarkerSize = 0;

        var xs = new List<DateTime> { start };
        var ys = new List<double> { 0 };
        foreach (var s in samples) { xs.Add(s.TimestampUtc.LocalDateTime); ys.Add(s.Used); }
        xs.Add(nowLocal); ys.Add(snap.Used);
        var used = plot.Add.Scatter(xs.ToArray(), ys.ToArray());
        used.LegendText = "Used";
        used.Color = Sp(p.ForStatus(pace.Status));
        used.LineWidth = 3;
        used.MarkerSize = 0;
        used.FillY = true;
        used.FillYColor = Sp(p.ForStatus(pace.Status), 40);

        var projection = plot.Add.Scatter(new[] { nowLocal, end }, new[] { snap.Used, pace.ProjectedUsedAtReset });
        projection.LegendText = "Projection";
        projection.Color = Sp(p.ForStatus(pace.Status), 170);
        projection.LinePattern = SP.LinePattern.Dotted;
        projection.LineWidth = 2;
        projection.MarkerSize = 0;

        var nowLine = plot.Add.VerticalLine(nowLocal.ToOADate());
        nowLine.Color = Sp(p.TextSecondary, 120);
        nowLine.LineWidth = 1;
        nowLine.Text = "Now";
        nowLine.LabelFontColor = Sp(p.Text);
        nowLine.LabelBackgroundColor = SP.Colors.Transparent;

        ChartStyle.DateAxis(plot);
        plot.Axes.SetLimits(start.ToOADate(), end.ToOADate(), 0, Math.Max(snap.Entitlement, pace.ProjectedUsedAtReset) * 1.08);
        plot.ShowLegend(SP.Alignment.UpperLeft);
    }

    private static void DrawDaily(SP.Plot plot, UsageSnapshot snap, IReadOnlyList<UsageSnapshot> samples, PaceInfo pace)
    {
        var p = ThemeService.Current;
        var days = UsageAnalytics.DailyBurns(samples.Append(snap).ToList(), snap.PeriodStartUtc, snap.PeriodEndUtc, TimeZoneInfo.Local);
        var bars = days.Select(d => new SP.Bar
        {
            Position = d.Day.ToDateTime(new TimeOnly(12, 0)).ToOADate(),
            Value = d.Burned,
            Size = 0.7,
            FillColor = d.Estimated ? Sp(p.Neutral, 90)
                : Sp(d.Burned > pace.DailyAllowance * 1.5 ? p.Bad : d.Burned > pace.DailyAllowance ? p.Warn : p.Good, 220),
            LineWidth = 0,
        }).ToList();
        if (bars.Count > 0) plot.Add.Bars(bars);
        if (days.Any(d => d.Estimated))
            plot.Legend.ManualItems.Add(new SP.LegendItem { LabelText = "Estimated (no readings those days)", FillColor = Sp(p.Neutral, 90) });

        var even = plot.Add.HorizontalLine(pace.DailyAllowance);
        even.Color = Sp(p.TextSecondary);
        even.LinePattern = SP.LinePattern.Dashed;
        even.LineWidth = 1;
        even.LegendText = $"Even pace {Amount(snap, pace.DailyAllowance)}/day";

        var budget = plot.Add.HorizontalLine(pace.DailyBudgetLeft);
        budget.Color = Sp(p.Series[0]);
        budget.LinePattern = SP.LinePattern.Dotted;
        budget.LineWidth = 2;
        budget.LegendText = $"Budget left {Amount(snap, pace.DailyBudgetLeft)}/day";

        ChartStyle.DateAxis(plot);
        var max = Math.Max(pace.DailyAllowance * 2, days.Count > 0 ? days.Max(d => d.Burned) : 0);
        plot.Axes.SetLimits(snap.PeriodStartUtc.LocalDateTime.Date.ToOADate(), snap.PeriodEndUtc.LocalDateTime.Date.AddDays(1).ToOADate(), 0, Math.Max(1, max) * 1.12);
        plot.ShowLegend(SP.Alignment.UpperRight);
    }

    private void DrawHistory(SP.Plot plot, UsageSnapshot snap)
    {
        var p = ThemeService.Current;
        var monthly = (snap.PeriodEndUtc - snap.PeriodStartUtc).TotalDays >= 27;
        var periods = _host.Collector.Store.GetPeriodSummaries(snap.Key);
        var bars = new List<SP.Bar>();
        var ticks = new List<SP.Tick>();
        for (var i = 0; i < periods.Count; i++)
        {
            var period = periods[i];
            var current = period.PeriodStartUtc == snap.PeriodStartUtc;
            var pct = period.UsedFraction * 100;
            bars.Add(new SP.Bar
            {
                Position = i, Value = pct, Size = 0.55, LineWidth = 0,
                FillColor = current ? Sp(p.Series[0]) : Sp(pct >= 100 ? p.Bad : pct >= 85 ? p.Warn : p.Good, 220),
                Label = IsPercent(snap) ? $"{pct:0}%" : $"{period.Used:N0}",
            });
            ticks.Add(new SP.Tick(i, period.PeriodStartUtc.ToLocalTime().ToString(monthly ? "MMM yyyy" : "d MMM") + (current ? " (now)" : ""), true));
        }
        if (bars.Count > 0) plot.Add.Bars(bars).ValueLabelStyle.ForeColor = Sp(p.Text);

        var full = plot.Add.HorizontalLine(100);
        full.Color = Sp(p.Bad, 140);
        full.LinePattern = SP.LinePattern.Dashed;
        full.LineWidth = 1;
        full.LegendText = "Whole quota";

        plot.Axes.Bottom.TickGenerator = new SP.TickGenerators.NumericManual(ticks.ToArray());
        plot.Axes.Left.Label.Text = "% of quota used";
        plot.Axes.Left.Label.ForeColor = Sp(p.TextSecondary);
        plot.Axes.SetLimits(-0.75, Math.Max(periods.Count, 6) - 0.25, 0, 118);
        plot.ShowLegend(SP.Alignment.UpperRight);
    }
}
