using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AvaCodeStats.Controls;
using AvaCodeStats.Core;
using AvaCodeStats.Core.Tokens;
using AvaCodeStats.Theming;
using SP = ScottPlot;

namespace AvaCodeStats.Views;

/// <summary>Cost and token usage of local AI tools (Claude Code, Codex), read from their session logs.</summary>
public partial class AnalyticsPage : UserControl, IPage
{
    public const string CostMode = "cost";
    public const string TokensMode = "tokens";

    private readonly AppHost _host;
    private readonly string _mode;
    private string _range = "30d";
    private TokenReport? _report;
    private int _version;

    internal AnalyticsPage(AppHost host, string mode)
    {
        _host = host;
        _mode = mode;
        InitializeComponent();
        Range30.IsChecked = true;
        Intro.Text = IsCost
            ? "Claude Code and Codex, read from their local session logs. Cost is an API list-price estimate of the same usage."
            : "Claude Code and Codex, read from their local session logs.";
        if (!IsCost)
        {
            SpeedCard.Visibility = Visibility.Collapsed;
            SpeedColumn.Width = new GridLength(0);
        }
    }

    public string Title => IsCost ? "Cost" : "Tokens";
    private bool IsCost => _mode == CostMode;
    private double Value(TokenTotals t) => IsCost ? t.Cost.Total : t.Total;
    private string Show(double v) => IsCost ? Fmt.Money(v) : Fmt.Tokens(v);

    private void OnRange(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string r }) { _range = r; if (IsLoaded) Refresh(); }
    }

    private void OnBreakdown(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) FillRows();
    }

    public async void Refresh()
    {
        var version = ++_version;
        var settings = _host.Settings;
        var sources = settings.ActiveSources();
        var prices = new PriceTable(settings.Pricing);
        var (from, to, bucket) = Window(_range);
        var report = await Task.Run(() =>
            TokenReport.Build(_host.Collector.Tokens.GetEvents(from, to, sources), prices, from, to, bucket, TimeZoneInfo.Local));
        if (version != _version) return;
        _report = report;
        Render(bucket);
    }

    private static (DateTimeOffset from, DateTimeOffset to, TimeSpan bucket) Window(string range)
    {
        var now = DateTimeOffset.Now;
        if (range == "24h") return (now.AddHours(-24), now.AddMinutes(1), TimeSpan.FromHours(1));
        var days = range switch { "7d" => 7, "90d" => 90, _ => 30 };
        return (new DateTimeOffset(now.Date.AddDays(-(days - 1)), now.Offset), now.AddMinutes(1), TimeSpan.FromDays(1));
    }

    private sealed record ProviderRow(string Name, string Sessions, string Value, string Detail, Brush Dot);
    private sealed record Stat(string Label, string Value);
    private sealed record LegendEntry(string Label, string Value, Brush Swatch);

    private sealed record Row(int Rank, string Name, string Cost, string Share, string Tokens, Brush Dot, Visibility DotVisibility, Brush Bar, double ShareValue)
    {
        public GridLength ShareLength => new(Math.Max(0.002, ShareValue), GridUnitType.Star);
        public GridLength RestLength => new(Math.Max(0, 1 - ShareValue), GridUnitType.Star);
    }

    private void Render(TimeSpan bucket)
    {
        if (_report is not { } rep) return;
        var p = ThemeService.Current;
        var total = Math.Max(1e-9, Value(rep.Totals));

        Total.Text = Show(Value(rep.Totals));
        Sub.Text = $"{rep.Sessions} sessions" + (IsCost ? " · API estimate" : $" · {Fmt.Money(rep.Totals.Cost.Total)} at API prices");
        Unpriced.Text = IsCost && rep.UnpricedModels.Count > 0
            ? $"{string.Join(", ", rep.UnpricedModels)} {(rep.UnpricedModels.Count == 1 ? "has" : "have")} no price yet, so {(rep.UnpricedModels.Count == 1 ? "it counts" : "they count")} as $0. Add prices under \"pricing\" in the settings file."
            : "";
        UnpricedNote.Visibility = Unpriced.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Providers.ItemsSource = rep.ByProvider.OrderByDescending(g => Value(g.Totals)).Select(g => new ProviderRow(
            ProviderFactory.DisplayName(g.Key), $"{g.Sessions} sessions", Show(Value(g.Totals)),
            IsCost ? $"{Value(g.Totals) / total:P1} of cost · {Fmt.Tokens(g.Totals.Total)} tokens"
                   : $"{Value(g.Totals) / total:P1} of tokens · {Fmt.Money(g.Totals.Cost.Total)}",
            ThemeService.Brush(p.ForProvider(g.Key)))).ToList();

        var t = rep.Totals;
        Totals.ItemsSource = new[]
        {
            new Stat("Processed tokens", Fmt.Tokens(t.Total)), new Stat("Cached input", Fmt.Tokens(t.CacheRead)),
            new Stat("Cache writes", Fmt.Tokens(t.CacheWrite)), new Stat("Uncached input", Fmt.Tokens(t.Input)),
            new Stat("Output", Fmt.Tokens(t.Output)), new Stat("Cache savings", Fmt.Money(t.Cost.CacheSavings)),
        };

        TypeTitle.Text = IsCost ? "Cost by type" : "Tokens by type";
        var shades = p.TypeShades;
        var types = IsCost
            ? new[] { ("Input", t.Cost.Input), ("Cache read", t.Cost.CacheRead), ("Cache write", t.Cost.CacheWrite), ("Output", t.Cost.Output) }
            : new[] { ("Input", (double)t.Input), ("Cache read", (double)t.CacheRead), ("Cache write", (double)t.CacheWrite), ("Output", (double)t.Output) };
        TypeBar.SetParts(types.Select((x, i) => (x.Item2, shades[i])).ToList());
        TypeLegend.ItemsSource = types.Select((x, i) => new LegendEntry(x.Item1, Show(x.Item2), ThemeService.Brush(shades[i]))).ToList();

        if (IsCost)
        {
            var speed = new[] { ("Standard", rep.StandardSpeed.Total, shades[1]), ("Fast", rep.FastSpeed.Total, shades[3]) };
            SpeedBar.SetParts(speed.Select(x => (x.Item2, x.Item3)).ToList());
            SpeedLegend.ItemsSource = speed.Select(x => new LegendEntry(x.Item1, Fmt.Money(x.Item2), ThemeService.Brush(x.Item3))).ToList();
            FastPremium.Text = $"Fast premium {Fmt.Money(rep.FastSpeed.Total / 2)}";
        }

        DrawChart(rep, bucket);
        FillRows();
    }

    private void DrawChart(TokenReport rep, TimeSpan bucket)
    {
        var p = ThemeService.Current;
        ChartTitle.Text = (bucket < TimeSpan.FromDays(1) ? "Hourly " : "Daily ") + (IsCost ? "cost" : "processed tokens");
        var plot = Chart.Plot;
        plot.Clear();
        ChartStyle.Apply(plot);
        var xs = rep.Buckets.Select(b => b.StartUtc.LocalDateTime).ToArray();
        foreach (var provider in rep.ByProvider.Select(g => g.Key).Reverse())
        {
            if (xs.Length == 0) break;
            var ys = rep.Buckets.Select(b => b.ByProvider.TryGetValue(provider, out var v) ? Value(v) : 0).ToArray();
            var color = p.ForProvider(provider);
            var s = plot.Add.Scatter(xs, ys);
            s.Color = ChartStyle.Sp(color);
            s.LineWidth = 2.5f;
            s.MarkerSize = 0;
            s.Smooth = true;
            s.FillY = true;
            s.FillYColor = ChartStyle.Sp(color, 45);
            s.LegendText = ProviderFactory.DisplayName(provider);
        }
        ChartStyle.DateAxis(plot, hours: bucket < TimeSpan.FromDays(1));
        plot.Axes.Left.TickGenerator = new SP.TickGenerators.NumericAutomatic
        {
            LabelFormatter = v => IsCost ? (v == 0 ? "0" : $"${v:0.##}") : Fmt.Tokens(v),
        };
        var max = rep.Buckets.Count == 0 ? 0 : rep.Buckets.Max(b => b.ByProvider.Values.Select(Value).DefaultIfEmpty(0).Max());
        if (xs.Length > 1) plot.Axes.SetLimits(xs[0].ToOADate(), xs[^1].ToOADate(), 0, Math.Max(max * 1.15, IsCost ? 1 : 1000));
        plot.ShowLegend(SP.Alignment.UpperLeft);
        Chart.Refresh();
    }

    private void FillRows()
    {
        if (_report is not { } rep) return;
        var p = ThemeService.Current;
        var total = Math.Max(1e-9, Value(rep.Totals));
        var byModel = ByModel.IsChecked == true;
        NameHeader.Text = byModel ? "Model" : "Day";

        IEnumerable<(string name, string provider, TokenTotals t)> source = byModel
            ? rep.ByModel.OrderByDescending(m => Value(m.Totals)).ThenByDescending(m => m.Totals.Total).Select(m => (m.Key, m.ProviderId, m.Totals))
            : rep.Buckets.GroupBy(b => b.StartUtc.LocalDateTime.Date)
                .Select(d => (d.Key.ToString("ddd d MMM"), "", d.SelectMany(b => b.ByProvider.Values)
                    .Aggregate(TokenTotals.Zero, (a, b) => new TokenTotals(a.Input + b.Input, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite, a.Output + b.Output, a.Cost + b.Cost))))
                .Where(x => x.Item3.Total > 0)
                .Reverse();

        Rows.ItemsSource = source.Select((x, i) =>
        {
            var color = x.provider.Length > 0 ? p.ForProvider(x.provider) : p.Series[0];
            var share = Value(x.t) / total;
            var priced = x.t.Cost.Priced || x.t.Cost.Total > 0;
            return new Row(i + 1, x.name, priced ? Fmt.Money(x.t.Cost.Total) : "–", $"{share:P1}", Fmt.Tokens(x.t.Total),
                ThemeService.Brush(color), x.provider.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
                ThemeService.Brush(color, 200), share);
        }).ToList();
    }
}
