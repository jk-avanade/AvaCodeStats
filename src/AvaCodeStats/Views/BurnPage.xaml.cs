using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvaCodeStats.Controls;
using AvaCodeStats.Core;
using AvaCodeStats.Core.Providers.GitHubCopilot;
using AvaCodeStats.Theming;

namespace AvaCodeStats.Views;

/// <summary>
/// Burn of every quota in the profile, of one subscription's quotas (both in shares of each quota, over a
/// chosen range), or of one quota in detail (its current period). As the Pace page it shows the same scopes as
/// how far each quota is ahead of (+) or behind (−) an even pace over time.
/// </summary>
public partial class BurnPage : UserControl, IPage
{
    private const string AllScope = "all";

    private readonly AppHost _host;
    private readonly bool _pace;
    /// <summary>"all", "sub:provider:account" or "quota:provider:account:metric".</summary>
    private string _scope = AllScope;
    private string _range = "30d";
    private UsageSnapshot? _quota;
    private bool _filling;
    private int _version;

    internal BurnPage(AppHost host, bool pace = false)
    {
        _host = host;
        _pace = pace;
        InitializeComponent();
        Range30.IsChecked = true;
    }

    public string Title => _pace ? "Pace" : "Burn";

    internal void Select(SubscriptionKey key)
    {
        _scope = QuotaScope(key);
        Refresh();
    }

    private static string QuotaScope(SubscriptionKey key) => $"quota:{key}";

    private sealed record Scope(string Id, string Label, bool IsGroup, int Level)
    {
        public FontWeight Weight => IsGroup ? FontWeights.SemiBold : FontWeights.Normal;
        public Thickness Indent => new(Level * 18, 0, 0, 0);
        public override string ToString() => Label;
    }

    private string QuotaLabel(LimitItem i) =>
        $"{i.Provider} · {i.Title}" + (i.Snapshot.AccountId == "default" ? "" : $"  ({Privacy.Account(_host.Settings, i.Snapshot.ProviderId, i.Snapshot.AccountId)})");

    private sealed record Kpi(string Label, string Value, string Note, Brush Brush);

    public void Refresh()
    {
        var s = _host.Settings;
        var items = LimitItem.For(_host, s.ActiveProfile);
        var groups = LimitGroup.From(items, s, false, Array.Empty<string>());

        // All quotas, then each subscription with its quotas under it (a single-quota subscription is just the quota).
        var scopes = new List<Scope>();
        if (items.Count > 1) scopes.Add(new(AllScope, $"All quotas ({items.Count})", true, 0));
        foreach (var g in groups)
        {
            if (g.Items.Count > 1)
            {
                scopes.Add(new($"sub:{g.Key}", $"{g.Name} · all {g.Items.Count}", true, 0));
                scopes.AddRange(g.Items.Select(i => new Scope(QuotaScope(i.Key), QuotaLabel(i), false, 1)));
            }
            else
            {
                scopes.Add(new(QuotaScope(g.Items[0].Key), QuotaLabel(g.Items[0]), false, 0));
            }
        }

        _filling = true;
        Picker.ItemsSource = scopes;
        var index = scopes.FindIndex(x => x.Id == _scope);
        Picker.SelectedIndex = scopes.Count == 0 ? -1 : Math.Max(0, index);
        _filling = false;

        if (Picker.SelectedItem is Scope scope) Show(scope.Id, items);
        else
        {
            _version++;
            Body.Visibility = Overview.Visibility = RangeHost.Visibility = Visibility.Collapsed;
            Empty.Visibility = Visibility.Visible;
            Empty.Text = $"No quotas in the {Profiles.Label(s.ActiveProfile)} profile yet.";
            Plan.Text = "";
        }
    }

    private void Show(string scope, IReadOnlyList<LimitItem> items)
    {
        _scope = scope;
        Empty.Visibility = Visibility.Collapsed;
        if (!_pace && scope.StartsWith("quota:") && items.FirstOrDefault(i => QuotaScope(i.Key) == scope) is { } one)
        {
            _version++; // drop an overview still loading
            _quota = one.Snapshot;
            Overview.Visibility = RangeHost.Visibility = Visibility.Collapsed;
            Plan.Visibility = Visibility.Visible;
            Draw(one.Snapshot);
            return;
        }

        _quota = null;
        var selected = items.Where(i => scope == AllScope || $"sub:{i.Snapshot.ProviderId}:{i.Snapshot.AccountId}" == scope || QuotaScope(i.Key) == scope).ToList();
        if (selected.Count == 0) selected = items.ToList();
        Body.Visibility = Plan.Visibility = Visibility.Collapsed;
        Overview.Visibility = RangeHost.Visibility = Visibility.Visible;
        DrawOverview(selected, single: scope != AllScope);
    }

    private void OnPick(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Picker.SelectedItem is not Scope scope) return;
        Show(scope.Id, LimitItem.For(_host, _host.Settings.ActiveProfile));
    }

    private void OnRange(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string r }) return;
        _range = r;
        if (IsLoaded && _quota is null) Refresh();
    }

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && _quota is { } q) Draw(q);
    }

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuotaRow row }) Select(row.Key);
    }

    // ---------- several quotas ----------

    private sealed record GroupRow(string Name, string Count, string Value, string Detail, Brush Dot, Brush DetailBrush);

    /// <summary>A quota in the table. Diverging (Pace): the bar shows <paramref name="Value"/> from -1 to +1 around a centre line.</summary>
    private sealed record QuotaRow(int Rank, string Name, string Sub, Brush Dot, string Left, string PaceGlyph, string Pace, Brush PaceBrush,
        string Burned, string Resets, string ResetTip, Brush Bar, double Value, bool Diverging, SubscriptionKey Key)
    {
        private static GridLength Star(double v) => new(Math.Max(0.0001, v), GridUnitType.Star);
        public GridLength UsedLength => Star(Math.Max(0.002, Value));
        public GridLength RestLength => Star(1 - Value);
        public GridLength UnderRest => Star(1 - Math.Clamp(-Value, 0, 1));
        public GridLength Under => Star(Math.Clamp(-Value, 0, 1));
        public GridLength Over => Star(Math.Clamp(Value, 0, 1));
        public GridLength OverRest => Star(1 - Math.Clamp(Value, 0, 1));
        public Visibility UsedBar => Diverging ? Visibility.Collapsed : Visibility.Visible;
        public Visibility PaceBar => Diverging ? Visibility.Visible : Visibility.Collapsed;
        public string Tip => Diverging ? "Show only this quota" : "Open this quota's burn";
    }

    private static (DateTimeOffset from, DateTimeOffset to, bool hourly, string label) Window(string range)
    {
        var now = DateTimeOffset.Now;
        if (range == "24h") return (now.AddHours(-24), now, true, "24 hours");
        var days = range switch { "7d" => 7, "90d" => 90, _ => 30 };
        return (new DateTimeOffset(now.Date.AddDays(-(days - 1)), now.Offset), now, false, $"{days} days");
    }

    private static bool IsOver(PaceStatus s) => s is PaceStatus.SlightlyOver or PaceStatus.FarOver or PaceStatus.Exhausted;

    private async void DrawOverview(List<LimitItem> items, bool single)
    {
        var version = ++_version;
        var (from, to, hourly, rangeLabel) = Window(_range);
        var store = _host.Collector.Store;
        // A little history before the range gives each quota a baseline for "burned".
        var samples = await Task.Run(() => items.Select(i => store.GetSamples(i.Key, from.AddHours(-2), to.AddMinutes(1))).ToList());
        if (version != _version) return;

        var p = ThemeService.Current;
        var s = _host.Settings;
        var text = (Brush)FindResource("TextFillColorPrimaryBrush");
        // Pace: share used minus share of the period gone, now and over the range. A quota whose period ended
        // without a newer reading has reset; where it stands in the new period is unknown until the next reading.
        var nowUtc = DateTimeOffset.UtcNow;
        bool Ended(int i) => items[i].Snapshot.PeriodEndUtc <= nowUtc;
        double Ahead(int i) => Ended(i) ? 0 : Math.Clamp(items[i].Snapshot.UsedFraction, 0, 1) - items[i].Pace.ElapsedFraction;
        string AheadText(int i) => Ended(i) ? "reset" : Fmt.Signed(Ahead(i));
        List<IReadOnlyList<(DateTimeOffset At, double Delta)>> paceLines = _pace
            ? Enumerable.Range(0, items.Count).Select(i => UsageAnalytics.PaceOverTime(samples[i], items[i].Snapshot, nowUtc)).ToList()
            : [];
        var order = (_pace
            ? Enumerable.Range(0, items.Count).OrderBy(Ended).ThenByDescending(Ahead)
            : Enumerable.Range(0, items.Count).OrderByDescending(i => AlertRules.Severity(items[i].Status)).ThenByDescending(i => items[i].Snapshot.UsedFraction)).ToList();
        var color = order.Select((item, n) => (item, c: p.Series[n % p.Series.Length])).ToDictionary(x => x.item, x => x.c);
        double Burned(int i) => UsageAnalytics.BurnedFraction(
            samples[i].Append(items[i].Snapshot).Where(x => x.TimestampUtc <= to.AddMinutes(1)).DistinctBy(x => x.TimestampUtc), from, to.AddMinutes(1));
        var burned = Enumerable.Range(0, items.Count).Select(Burned).ToList();
        string Name(LimitItem i) => single ? i.Title : $"{i.Provider} · {i.Title}" + Privacy.Suffix(s, i.Snapshot.ProviderId, i.Snapshot.AccountId, " (") + (i.Snapshot.AccountId == "default" ? "" : ")");

        // Hero: how many are over pace.
        var over = items.Count(i => IsOver(i.Status));
        var worst = LimitGroup.Worst(items);
        OvHero.Text = over == 0 ? "On pace" : $"{over} over pace";
        OvHero.Foreground = worst.StatusBrush;
        var groups = LimitGroup.From(order.Select(i => items[i]), s, false, Array.Empty<string>());
        OvSub.Text = $"{items.Count} quota{(items.Count == 1 ? "" : "s")}" + (single ? $" · {groups[0].Meta}".TrimEnd(' ', '·') : $" · {groups.Count} subscriptions");

        OvGroups.ItemsSource = groups.Select(g =>
        {
            // Pace: the quota furthest ahead of pace; Burn: the most pressing one.
            var w = _pace ? g.Items.MaxBy(x => Ended(Index(x)) ? double.MinValue : Ahead(Index(x)))! : LimitGroup.Worst(g.Items);
            return new GroupRow(g.Name, $"{g.Items.Count} quota{(g.Items.Count == 1 ? "" : "s")}",
                _pace ? AheadText(Index(w)) : $"{w.LeftPercent} left",
                (g.Items.Count > 1 ? $"{w.Title} · " : "") + w.PaceText, g.Dot, w.StatusBrush);
        }).ToList();
        int Index(LimitItem x) => items.IndexOf(x);

        // Chart: each quota's share used over time (a sawtooth across resets). Starts at the first reading when the
        // history is shorter than the range; with many quotas the table's coloured dots are the legend.
        var series = order.Select(i => (i, points: _pace
                ? paceLines[i].Where(x => x.At >= from).Select(x => (x.At, Value: x.Delta)).ToList()
                : UsageAnalytics.UsedOverTime(samples[i].Where(x => x.TimestampUtc >= from),
                    items[i].Snapshot.TimestampUtc >= from ? items[i].Snapshot : null).Select(x => (x.At, Value: x.Fraction)).ToList()))
            .Where(x => x.points.Count > 0).ToList();
        var firstReading = series.Select(x => x.points[0].At).DefaultIfEmpty(from).Min();
        var start = firstReading > from ? firstReading : from;
        var chartTitle = _pace ? "Over or under pace" : "Used over time";
        OvChartTitle.Text = start > from ? $"{chartTitle} · history from {start.ToLocalTime():ddd d MMM}" : chartTitle;
        var m = new ChartModel
        {
            XKind = hourly || to - start < TimeSpan.FromDays(2) ? XAxisKind.Hours : XAxisKind.Days,
            XMin = start.LocalDateTime.ToOADate(), XMax = to.LocalDateTime.ToOADate(),
            YMin = _pace ? -100 : null, YMax = _pace ? 100 : 105,
            FormatY = _pace ? v => Fmt.Signed(v / 100) : v => $"{v:0}%",
            FormatValue = _pace ? v => v >= 0 ? $"{v:0.#}% over pace" : $"{-v:0.#}% under pace" : v => $"{v:0.#}% used",
            EmptyText = "No readings in this range yet.",
        };
        foreach (var (i, points) in series)
        {
            // Over days, a 5-hour window swings between 0 and −100% every few hours: draw those lighter so the
            // longer quotas' trend stays readable (the 24-hour view shows them in full).
            var faint = _pace && !hourly && items[i].Snapshot.PeriodEndUtc - items[i].Snapshot.PeriodStartUtc < TimeSpan.FromDays(1.5);
            m.Series.Add(new ChartSeries
            {
                Name = Name(items[i]), InLegend = series.Count <= 4,
                Color = faint ? Color.FromArgb(110, color[i].R, color[i].G, color[i].B) : color[i], Thickness = faint ? 1.25 : 2,
                X = points.Select(x => x.At.LocalDateTime.ToOADate()).ToArray(),
                Y = points.Select(x => x.Value * 100).ToArray(),
            });
        }
        m.Lines.Add(_pace ? new ReferenceLine(0, "Even pace", p.TextSecondary) : new ReferenceLine(100, "Whole quota", p.Bad));
        OvChart.Model = m;

        // At a glance.
        string Short(LimitItem i) => single ? i.Title : $"{i.Provider} · {i.Title}";
        var runningOut = items.Where(i => i.Pace.ProjectedExhaustionUtc is not null).OrderBy(i => i.Pace.ProjectedExhaustionUtc).ToList();
        var lowest = items.MinBy(i => i.LeftFraction)!;
        var soonest = items.MinBy(i => i.Snapshot.PeriodEndUtc)!;
        var most = Enumerable.Range(0, items.Count).MaxBy(i => burned[i]);
        List<double?> average = _pace ? Enumerable.Range(0, items.Count).Select(i => UsageAnalytics.TimeAverage(paceLines[i], from, to)).ToList() : [];
        if (_pace)
        {
            var current = order.Where(i => !Ended(i)).DefaultIfEmpty(order[0]).ToList();
            var ahead = current[0];
            var behind = current[^1];
            var known = average.Where(a => a is not null).Select(a => a!.Value).ToList();
            OvKpis.ItemsSource = new[]
            {
                new Kpi("Over pace", $"{over}", $"of {items.Count} quotas", over > 0 ? worst.StatusBrush : ThemeService.Brush(p.Good)),
                new Kpi("Furthest over", AheadText(ahead), Short(items[ahead]), items[ahead].StatusBrush),
                new Kpi("Furthest under", AheadText(behind), Short(items[behind]), items[behind].StatusBrush),
                new Kpi("Average now", Fmt.Signed(current.Average(Ahead)), "share used minus time gone", text),
                new Kpi($"Average, {rangeLabel}", known.Count > 0 ? Fmt.Signed(known.Average()) : "–", "over time, these quotas", text),
            };
        }
        else OvKpis.ItemsSource = new[]
        {
            new Kpi("Over pace", $"{over}", $"of {items.Count} quotas", over > 0 ? worst.StatusBrush : ThemeService.Brush(p.Good)),
            new Kpi("Run out before reset", $"{runningOut.Count}",
                runningOut.FirstOrDefault() is { } first ? $"{Short(first)} · {first.Pace.ProjectedExhaustionUtc!.Value.ToLocalTime():ddd d MMM HH:mm}" : "none at the current pace",
                runningOut.Count > 0 ? (Brush)FindResource("StatusBadBrush") : text),
            new Kpi("Lowest left", lowest.LeftPercent, Short(lowest), lowest.StatusBrush),
            new Kpi("Soonest reset", Fmt.Countdown(soonest.Snapshot.PeriodEndUtc - DateTimeOffset.UtcNow), Short(soonest), text),
            new Kpi($"Most burned, {rangeLabel}", $"{burned[most] * 100:0.#}%", Short(items[most]), text),
        };

        // Every quota, most pressing first.
        LeftHeader.Text = _pace ? "Now" : "Left";
        BurnedHeader.Text = _pace ? $"Average, {rangeLabel}" : $"Burned, {rangeLabel}";
        OvRows.ItemsSource = order.Select((i, n) =>
        {
            var it = items[i];
            return new QuotaRow(n + 1, it.Title, single ? "" : groups.First(g => g.Items.Contains(it)).Name, ThemeService.Brush(color[i]),
                _pace ? AheadText(i) : it.LeftPercent,
                _pace && Ended(i) ? Glyphs.Info : it.StatusGlyph, _pace && Ended(i) ? "Waiting for a reading" : it.PaceText,
                _pace && Ended(i) ? (Brush)FindResource("TextFillColorSecondaryBrush") : it.StatusBrush,
                _pace ? (average[i] is { } a ? Fmt.Signed(a) : "–") : $"{burned[i] * 100:0.#}%",
                Fmt.Countdown(it.Snapshot.PeriodEndUtc - nowUtc), it.ResetTooltip,
                _pace ? it.StatusBrush : ThemeService.Brush(color[i], 200),
                _pace ? Ahead(i) : Math.Clamp(it.Snapshot.UsedFraction, 0, 1), _pace, it.Key);
        }).ToList();
    }

    // ---------- one quota ----------

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
            BarColors = periods.Select(x => UsageSnapshot.SamePeriod(x.PeriodStartUtc, snap.PeriodStartUtc) ? p.Series[0]
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