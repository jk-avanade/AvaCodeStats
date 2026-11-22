using AvaCodeStats.Core;
using AvaCodeStats.Core.Providers.GitHubCopilot;
using ScottPlot.WinForms;
using SP = ScottPlot;

namespace AvaCodeStats;

/// <summary>Burn of one quota over its period: ring, pace cards, and the period / daily / history charts.</summary>
internal sealed class BurnView : UserControl
{
    private readonly UsageCollector _collector;
    private readonly Func<AppSettings> _settings;

    private readonly ComboBox _picker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly RingControl _ring = new() { Dock = DockStyle.Fill };
    private readonly Label _headline = new() { AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text };
    private readonly Label _plan = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted };
    private readonly Dictionary<string, (Label value, Label note)> _cards = [];

    private readonly FormsPlot _periodPlot = Ui.NewPlot();
    private readonly FormsPlot _dailyPlot = Ui.NewPlot();
    private readonly FormsPlot _historyPlot = Ui.NewPlot();
    private readonly List<Button> _tabs = [];

    private List<UsageSnapshot> _choices = [];
    private SubscriptionKey? _wanted;
    private bool _rendered;

    /// <summary>Raised after drawing a quota, so the window icon can follow it.</summary>
    public event Action<UsageSnapshot, PaceInfo>? Drawn;

    public BurnView(UsageCollector collector, Func<AppSettings> settings)
    {
        _collector = collector;
        _settings = settings;
        BackColor = Theme.Background;
        BuildLayout();
        _picker.SelectedIndexChanged += (_, _) => Render();
    }

    /// <summary>Shows <paramref name="key"/> (from a click in the Limits view).</summary>
    public void Select(SubscriptionKey key)
    {
        _wanted = key;
        RefreshView();
    }

    /// <summary>Rebuilds the quota list for the active profile and redraws.</summary>
    public void RefreshView() => RebuildPicker();

    // ScottPlot auto-scales on a plot's first render, overriding limits set before it; redraw once visible.
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && !_rendered && IsHandleCreated) { _rendered = true; BeginInvoke(Render); }
    }

    // ---------- layout ----------

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16, 8, 16, 16), BackColor = Theme.Background };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 300));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, BackColor = Theme.Background };
        _picker.BackColor = Theme.Panel;
        _picker.ForeColor = Theme.Text;
        _picker.FlatStyle = FlatStyle.Flat;
        header.Controls.Add(_picker);
        root.Controls.Add(header, 0, 0);

        var summary = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Panel, Padding = new Padding(8) };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summary.Controls.Add(_ring, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Theme.Panel };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var titles = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, BackColor = Theme.Panel, Padding = new Padding(4, 6, 0, 0) };
        titles.Controls.AddRange([_headline, _plan]);
        right.Controls.Add(titles, 0, 0);

        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, BackColor = Theme.Panel };
        for (var i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (var i = 0; i < 2; i++) cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        foreach (var name in new[] { "Used", "Remaining", "Pace now", "Versus pace", "Budget per day", "Projected at reset", "Last 24 hours", "Resets in" })
            cards.Controls.Add(Card(name));
        right.Controls.Add(cards, 0, 1);
        summary.Controls.Add(right, 1, 0);
        root.Controls.Add(summary, 0, 1);

        var tabBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Theme.Background, Padding = new Padding(0, 8, 0, 0) };
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        foreach (var (label, plot) in new[] { ("Burn this period", _periodPlot), ("Daily burn", _dailyPlot), ("History", _historyPlot) })
        {
            plot.Dock = DockStyle.Fill;
            plot.Visible = false;
            host.Controls.Add(plot);
            var b = Ui.FlatButton(label);
            b.Click += (_, _) => SelectTab(b, plot);
            _tabs.Add(b);
            tabBar.Controls.Add(b);
        }
        root.Controls.Add(tabBar, 0, 2);
        root.Controls.Add(host, 0, 3);
        Controls.Add(root);

        SelectTab(_tabs[0], _periodPlot);
    }

    private void SelectTab(Button button, FormsPlot plot)
    {
        foreach (var b in _tabs) b.BackColor = b == button ? Theme.Selected : Theme.Panel;
        foreach (var p in new[] { _periodPlot, _dailyPlot, _historyPlot }) p.Visible = p == plot;
    }

    private Control Card(string title)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Panel, Padding = new Padding(4, 6, 4, 0) };
        var t = new Label { Text = title.ToUpperInvariant(), AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted };
        var v = new Label { Text = "–", AutoSize = true, Font = Theme.CardValue, ForeColor = Theme.Text };
        var n = new Label { Text = "", AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted };
        panel.Controls.AddRange([t, v, n]);
        _cards[title] = (v, n);
        return panel;
    }

    private static SP.Color Sp(Color c, byte? alpha = null) => Ui.Sp(c, alpha);

    // ---------- data ----------

    /// <summary>Quotas reported as a percentage (Claude, Codex) rather than a count (Copilot).</summary>
    private static bool IsPercent(UsageSnapshot s) => s.Entitlement == 100 && s.ProviderId != CopilotQuotaParser.ProviderId;

    private static string Amount(UsageSnapshot s, double v) => IsPercent(s) ? $"{v:0.#}%" : $"{v:N0}";

    private void RebuildPicker()
    {
        var settings = _settings();
        bool InProfile(UsageSnapshot s) => settings.InActiveProfile(s.ProviderId, s.AccountId);
        var selected = _wanted
                       ?? (_picker.SelectedItem as PickerItem)?.Snapshot.Key
                       ?? _collector.PickTray(settings.TrayKeys.GetValueOrDefault(settings.ActiveProfile), InProfile)?.Key;
        _wanted = null;

        _choices = _collector.Latest.Values
            .Where(s => !s.Unlimited && s.Entitlement > 0 && InProfile(s))
            .OrderBy(s => s.ProviderId).ThenBy(s => s.AccountId).ThenBy(s => s.PeriodEndUtc - s.PeriodStartUtc).ThenBy(s => s.MetricName)
            .ToList();

        _picker.BeginUpdate();
        _picker.Items.Clear();
        foreach (var s in _choices) _picker.Items.Add(new PickerItem(s));
        _picker.EndUpdate();

        var index = Math.Max(0, _choices.FindIndex(s => s.Key == selected));
        if (_picker.Items.Count > 0 && _picker.SelectedIndex != index) _picker.SelectedIndex = index; // triggers Render
        else Render();
    }

    private sealed record PickerItem(UsageSnapshot Snapshot)
    {
        public override string ToString() =>
            $"{ProviderFactory.DisplayName(Snapshot.ProviderId)} · {Snapshot.MetricName}" +
            (Snapshot.AccountId == "default" ? "" : $" ({Snapshot.AccountId})");
    }

    private void Render()
    {
        if ((_picker.SelectedItem as PickerItem)?.Snapshot.Key is not { } key || !_collector.Latest.TryGetValue(key, out var snap))
        {
            _headline.Text = "No quota data in this profile yet";
            _headline.ForeColor = Theme.Text;
            _plan.Text = "Refresh, or assign subscriptions to this profile in the settings file.";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var pace = PaceCalculator.Calculate(snap, now, _settings().Thresholds);
        var samples = _collector.Store.GetSamples(key, snap.PeriodStartUtc, snap.PeriodEndUtc);
        var last24 = UsageAnalytics.BurnedWithin(samples, snap, TimeSpan.FromHours(24));
        var statusColor = Theme.ForStatus(pace.Status);

        _ring.Used = snap.UsedFraction;
        _ring.Pace = pace.PaceFraction;
        _ring.Status = pace.Status;
        _ring.CenterText = $"{snap.UsedFraction:P0}";
        _ring.CenterSubText = $"pace {pace.PaceFraction:P0}";
        _ring.Invalidate();

        _headline.Text = $"{ProviderFactory.DisplayName(snap.ProviderId)} · {snap.MetricName} · {PaceCalculator.Describe(pace)}";
        _headline.ForeColor = statusColor;
        var start = snap.PeriodStartUtc.ToLocalTime();
        var end = snap.PeriodEndUtc.ToLocalTime();
        _plan.Text = $"{snap.AccountId} · {snap.PlanName ?? "plan unknown"} · period {start:d MMM HH:mm} – {end:d MMM HH:mm}";

        var shortWindow = (snap.PeriodEndUtc - snap.PeriodStartUtc).TotalDays < 1.5;
        SetCard("Used", Amount(snap, snap.Used), IsPercent(snap) ? "of the limit" : $"of {snap.Entitlement:N0} ({snap.UsedFraction:P1})");
        SetCard("Remaining", Amount(snap, snap.Remaining), $"{1 - snap.UsedFraction:P1} left");
        SetCard("Pace now", Amount(snap, pace.ExpectedUsed), $"{pace.PaceFraction:P1} of the period gone");
        SetCard("Versus pace", $"{(pace.Delta >= 0 ? "+" : "−")}{Amount(snap, Math.Abs(pace.Delta))}",
            shortWindow ? (pace.Delta >= 0 ? "over pace" : "banked")
            : pace.DaysOverPace >= 0 ? $"{pace.DaysOverPace:0.0} days over" : $"{-pace.DaysOverPace:0.0} days banked", statusColor);
        SetCard("Budget per day", Amount(snap, pace.DailyBudgetLeft), $"even pace is {Amount(snap, pace.DailyAllowance)}",
            pace.DailyBudgetLeft < pace.DailyAllowance ? Theme.Amber : null);
        SetCard("Projected at reset", $"{pace.ProjectedUsedAtReset / snap.Entitlement:P0}",
            pace.ProjectedExhaustionUtc is { } ex ? $"runs out {ex.ToLocalTime():ddd d MMM HH:mm}" : "at the current average",
            pace.ProjectedExhaustionUtc is null ? null : Theme.Red);
        SetCard("Last 24 hours", last24 is { } b ? Amount(snap, b) : "–",
            last24 is { } b2 && pace.DailyAllowance > 0 ? $"{b2 / pace.DailyAllowance:P0} of daily pace" : "collecting history…",
            last24 is { } b3 && b3 > pace.DailyBudgetLeft ? Theme.Amber : null);
        SetCard("Resets in", Fmt.Countdown(snap.PeriodEndUtc - now), $"{end:ddd d MMM HH:mm}");

        DrawPeriod(snap, samples, pace, now);
        DrawDaily(snap, samples, pace);
        DrawHistory(snap);
        Drawn?.Invoke(snap, pace);
    }

    private void SetCard(string name, string value, string note, Color? color = null)
    {
        var (v, n) = _cards[name];
        v.Text = value;
        v.ForeColor = color ?? Theme.Text;
        n.Text = note;
    }

    private void DrawPeriod(UsageSnapshot snap, IReadOnlyList<UsageSnapshot> samples, PaceInfo pace, DateTimeOffset now)
    {
        var plot = _periodPlot.Plot;
        plot.Clear();

        var start = snap.PeriodStartUtc.LocalDateTime;
        var end = snap.PeriodEndUtc.LocalDateTime;
        var nowLocal = now.LocalDateTime;

        var paceLine = plot.Add.Scatter(new[] { start, end }, new[] { 0, snap.Entitlement });
        paceLine.LegendText = "Even pace";
        paceLine.Color = Sp(Theme.Muted);
        paceLine.LinePattern = SP.LinePattern.Dashed;
        paceLine.MarkerSize = 0;

        // Actual cumulative burn. Usage is 0 at reset by definition.
        var xs = new List<DateTime> { start };
        var ys = new List<double> { 0 };
        foreach (var s in samples) { xs.Add(s.TimestampUtc.LocalDateTime); ys.Add(s.Used); }
        xs.Add(nowLocal); ys.Add(snap.Used);
        var actual = plot.Add.Scatter(xs.ToArray(), ys.ToArray());
        actual.LegendText = "Used";
        actual.Color = Sp(Theme.ForStatus(pace.Status));
        actual.LineWidth = 2.5f;
        actual.MarkerSize = 0;

        var projection = plot.Add.Scatter(new[] { nowLocal, end }, new[] { snap.Used, pace.ProjectedUsedAtReset });
        projection.LegendText = "Projection";
        projection.Color = Sp(Theme.Purple);
        projection.LinePattern = SP.LinePattern.Dotted;
        projection.MarkerSize = 0;

        var cap = plot.Add.HorizontalLine(snap.Entitlement);
        cap.Color = Sp(Theme.Red, 160);
        cap.LinePattern = SP.LinePattern.Dashed;
        cap.LegendText = "Quota";

        var nowLine = plot.Add.VerticalLine(nowLocal.ToOADate());
        nowLine.Color = Sp(Theme.Blue, 140);
        nowLine.LegendText = "Now";

        plot.Axes.DateTimeTicksBottom();
        plot.Axes.SetLimits(start.ToOADate(), end.ToOADate(), 0, Math.Max(snap.Entitlement, pace.ProjectedUsedAtReset) * 1.05);
        plot.ShowLegend(SP.Alignment.UpperLeft);
        _periodPlot.Refresh();
    }

    private void DrawDaily(UsageSnapshot snap, IReadOnlyList<UsageSnapshot> samples, PaceInfo pace)
    {
        var plot = _dailyPlot.Plot;
        plot.Clear();
        plot.Legend.ManualItems.Clear();

        var withCurrent = samples.Append(snap).ToList();
        var days = UsageAnalytics.DailyBurns(withCurrent, snap.PeriodStartUtc, snap.PeriodEndUtc, TimeZoneInfo.Local);
        var bars = days.Select(d => new SP.Bar
        {
            Position = d.Day.ToDateTime(new TimeOnly(12, 0)).ToOADate(),
            Value = d.Burned,
            Size = 0.8,
            FillColor = d.Estimated
                ? Sp(Theme.Grey, 110)
                : Sp(d.Burned > pace.DailyAllowance * 1.5 ? Theme.Red : d.Burned > pace.DailyAllowance ? Theme.Amber : Theme.Green),
            LineWidth = 0,
        }).ToList();
        if (bars.Count > 0) plot.Add.Bars(bars);
        if (days.Any(d => d.Estimated))
        {
            plot.Legend.ManualItems.Add(new SP.LegendItem
            {
                LabelText = "Estimated (no readings those days)",
                FillColor = Sp(Theme.Grey, 110),
            });
        }

        var allowance = plot.Add.HorizontalLine(pace.DailyAllowance);
        allowance.Color = Sp(Theme.Muted);
        allowance.LinePattern = SP.LinePattern.Dashed;
        allowance.LegendText = $"Even pace {Amount(snap, pace.DailyAllowance)}/day";

        var budget = plot.Add.HorizontalLine(pace.DailyBudgetLeft);
        budget.Color = Sp(Theme.Blue);
        budget.LinePattern = SP.LinePattern.Dotted;
        budget.LegendText = $"Budget left {Amount(snap, pace.DailyBudgetLeft)}/day";

        plot.Axes.DateTimeTicksBottom();
        var start = snap.PeriodStartUtc.LocalDateTime.Date;
        var end = snap.PeriodEndUtc.LocalDateTime.Date.AddDays(1);
        var max = Math.Max(pace.DailyAllowance * 2, days.Count > 0 ? days.Max(d => d.Burned) : 0);
        plot.Axes.SetLimits(start.ToOADate(), end.ToOADate(), 0, Math.Max(1, max) * 1.1);
        plot.ShowLegend(SP.Alignment.UpperRight);
        _dailyPlot.Refresh();
    }

    private void DrawHistory(UsageSnapshot snap)
    {
        var plot = _historyPlot.Plot;
        plot.Clear();

        var monthly = (snap.PeriodEndUtc - snap.PeriodStartUtc).TotalDays >= 27;
        var periods = _collector.Store.GetPeriodSummaries(snap.Key);
        var bars = new List<SP.Bar>();
        var positions = new List<double>();
        var labels = new List<string>();
        for (var i = 0; i < periods.Count; i++)
        {
            var p = periods[i];
            var current = p.PeriodStartUtc == snap.PeriodStartUtc;
            var pct = p.UsedFraction * 100;
            bars.Add(new SP.Bar
            {
                Position = i,
                Value = pct,
                Size = 0.6,
                FillColor = Sp(current ? Theme.Blue : pct >= 100 ? Theme.Red : pct >= 85 ? Theme.Amber : Theme.Green),
                Label = IsPercent(snap) ? $"{pct:0}%" : $"{p.Used:N0}",
                LineWidth = 0,
            });
            positions.Add(i);
            labels.Add(p.PeriodStartUtc.ToLocalTime().ToString(monthly ? "MMM yyyy" : "d MMM") + (current ? " (now)" : ""));
        }

        if (bars.Count > 0)
        {
            var series = plot.Add.Bars(bars);
            series.ValueLabelStyle.ForeColor = Sp(Theme.Text);
        }

        var full = plot.Add.HorizontalLine(100);
        full.Color = Sp(Theme.Red, 160);
        full.LinePattern = SP.LinePattern.Dashed;
        full.LegendText = "100% of quota";

        plot.Axes.Bottom.TickGenerator = new SP.TickGenerators.NumericManual(positions.ToArray(), labels.ToArray());
        plot.Axes.Left.Label.Text = "% of quota used";
        plot.Axes.SetLimits(-0.75, Math.Max(periods.Count, 6) - 0.25, 0, 115);
        plot.ShowLegend(SP.Alignment.UpperRight);
        _historyPlot.Refresh();
    }
}
