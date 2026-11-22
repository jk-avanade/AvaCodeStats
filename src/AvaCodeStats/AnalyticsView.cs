using AvaCodeStats.Core;
using AvaCodeStats.Core.Tokens;
using ScottPlot.WinForms;
using SP = ScottPlot;

namespace AvaCodeStats;

/// <summary>Cost and token usage of local AI tools (Claude Code, Codex), read from their session logs.</summary>
internal sealed class AnalyticsView : UserControl
{
    public const string CostMode = "cost";
    public const string TokensMode = "tokens";

    private readonly UsageCollector _collector;
    private readonly Func<AppSettings> _settings;

    private readonly FlowLayoutPanel _content = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        BackColor = Theme.Background, Padding = new Padding(40, 16, 40, 24),
    };
    private readonly TableLayoutPanel _top = new() { ColumnCount = 2, Height = 340, BackColor = Theme.Background, Margin = new Padding(0) };
    private readonly PaintBox _summary = new() { Dock = DockStyle.Fill };
    private readonly Label _chartTitle = new() { AutoSize = true, Font = Theme.Strong, ForeColor = Theme.Text, Dock = DockStyle.Top, Padding = new Padding(4, 0, 0, 6) };
    private readonly FormsPlot _chart = Ui.NewPlot(Theme.Background);
    private readonly PaintBox _totals = new() { Height = 104, Margin = new Padding(0, 0, 0, 16) };
    private readonly PaintBox _types = new() { Height = 124, Margin = new Padding(0, 0, 0, 8) };
    private readonly Panel _breakdownHeader = new() { Height = 48, BackColor = Theme.Background, Margin = new Padding(0) };
    private readonly Segmented _breakdownMode = new(("model", "Model"), ("day", "Day"));
    private readonly DataGridView _grid = new();

    private TokenReport? _report;
    private string _range = "30d";
    private int _version;

    public string Mode { get; private set; } = CostMode;

    public AnalyticsView(UsageCollector collector, Func<AppSettings> settings)
    {
        _collector = collector;
        _settings = settings;
        BackColor = Theme.Background;

        _top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        _top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        _top.Controls.Add(_summary, 0, 0);
        var chartHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        _chart.Dock = DockStyle.Fill;
        chartHost.Controls.Add(_chart);
        chartHost.Controls.Add(_chartTitle);
        _top.Controls.Add(chartHost, 1, 0);

        _summary.Painter = PaintSummary;
        _totals.Painter = PaintTotals;
        _types.Painter = PaintTypes;

        var title = new Label { Text = "Breakdown", Font = Theme.Strong, ForeColor = Theme.Text, AutoSize = true, Location = new Point(0, 14) };
        _breakdownMode.Dock = DockStyle.Right;
        _breakdownMode.Changed += (_, _) => FillGrid();
        _breakdownHeader.Controls.Add(title);
        _breakdownHeader.Controls.Add(_breakdownMode);

        StyleGrid();

        _content.Controls.AddRange([_top, _totals, _types, _breakdownHeader, _grid]);
        _content.Resize += (_, _) => FitWidths();
        Controls.Add(_content);
    }

    public void SetMode(string mode, string range)
    {
        Mode = mode;
        _range = range;
        RefreshView();
    }

    /// <summary>Re-queries the token history (off the UI thread) and redraws.</summary>
    public async void RefreshView()
    {
        var version = ++_version;
        var settings = _settings();
        var sources = settings.ActiveSources();
        var prices = new PriceTable(settings.Pricing);
        var (from, to, bucket) = Window(_range);
        var report = await Task.Run(() =>
        {
            var events = _collector.Tokens.GetEvents(from, to, sources);
            return TokenReport.Build(events, prices, from, to, bucket, TimeZoneInfo.Local);
        });
        if (version != _version || IsDisposed) return;
        _report = report;
        DrawChart(bucket);
        _summary.Invalidate();
        _totals.Invalidate();
        _types.Invalidate();
        FillGrid();
        FitWidths();
    }

    private static (DateTimeOffset from, DateTimeOffset to, TimeSpan bucket) Window(string range)
    {
        var now = DateTimeOffset.Now;
        if (range == "24h") return (now.AddHours(-24), now.AddMinutes(1), TimeSpan.FromHours(1));
        var days = range switch { "7d" => 7, "90d" => 90, _ => 30 };
        var from = new DateTimeOffset(now.Date.AddDays(-(days - 1)), now.Offset);
        return (from, now.AddMinutes(1), TimeSpan.FromDays(1));
    }

    private void FitWidths()
    {
        var w = Math.Max(600, _content.ClientSize.Width - _content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        foreach (Control c in _content.Controls) c.Width = w;
    }

    private bool IsCost => Mode == CostMode;
    private double Value(TokenTotals t) => IsCost ? t.Cost.Total : t.Total;
    private string Show(double v) => IsCost ? Fmt.Money(v) : Fmt.Tokens(v);

    // ---------- painting ----------

    private void PaintSummary(Graphics g, Rectangle r)
    {
        if (_report is not { } rep) { Ui.DrawText(g, "Loading…", Theme.Body, Theme.Muted, 0, 0); return; }
        using var big = new Font("Segoe UI Semibold", 34f);
        var headline = Show(Value(rep.Totals));
        Ui.DrawText(g, headline, big, Theme.Text, -4, 0);
        var top = g.MeasureString(headline, big).Height * 0.92f;
        var sub = $"{rep.Sessions} sessions" + (IsCost ? " · API estimate" : "");
        Ui.DrawText(g, sub, Theme.Body, Theme.Muted, 0, top);
        if (IsCost && rep.UnpricedModels.Count > 0)
            Ui.DrawText(g, $"No price for: {string.Join(", ", rep.UnpricedModels.Take(3))}{(rep.UnpricedModels.Count > 3 ? "…" : "")} (add in settings)",
                Theme.Small, Theme.Amber, 0, top + 22);

        var total = Math.Max(1e-9, Value(rep.Totals));
        var y = top + 56;
        foreach (var p in rep.ByProvider)
        {
            using (var dot = new SolidBrush(Theme.ForProvider(p.Key))) g.FillEllipse(dot, 0, y + 7, 10, 10);
            var name = ProviderFactory.DisplayName(p.Key);
            Ui.DrawText(g, name, Theme.Strong, Theme.Text, 18, y);
            Ui.DrawText(g, $"{p.Sessions} sessions", Theme.Small, Theme.Muted, 22 + Ui.Width(g, name, Theme.Strong), y + 3);
            var value = Show(Value(p.Totals));
            Ui.DrawText(g, value, Theme.Strong, Theme.Text, r.Width - 24 - Ui.Width(g, value, Theme.Strong), y);
            var share = Value(p.Totals) / total;
            var second = IsCost
                ? $"{share:P1} of cost · {Fmt.Tokens(p.Totals.Total)} tokens"
                : $"{share:P1} of tokens · {Fmt.Money(p.Totals.Cost.Total)}";
            Ui.DrawText(g, second, Theme.Small, Theme.Muted, 0, y + 26);
            y += 66;
        }
        if (rep.ByProvider.Count == 0)
            Ui.DrawText(g, "No Claude Code or Codex activity in this profile and range.", Theme.Body, Theme.Muted, 0, y);
    }

    private void PaintTotals(Graphics g, Rectangle r)
    {
        if (_report is not { } rep) return;
        var t = rep.Totals;
        Ui.DrawText(g, "Totals", Theme.Strong, Theme.Text, 0, 8);
        var items = new (string, string)[]
        {
            ("Processed tokens", Fmt.Tokens(t.Total)), ("Cached input", Fmt.Tokens(t.CacheRead)),
            ("Cache writes", Fmt.Tokens(t.CacheWrite)), ("Uncached input", Fmt.Tokens(t.Input)),
            ("Output", Fmt.Tokens(t.Output)), ("Cache savings", Fmt.Money(t.Cost.CacheSavings)),
        };
        var col = r.Width / (float)items.Length;
        for (var i = 0; i < items.Length; i++)
        {
            Ui.DrawText(g, items[i].Item1, Theme.Body, Theme.Muted, i * col, 44);
            Ui.DrawText(g, items[i].Item2, Theme.CardValue, Theme.Text, i * col - 2, 64);
        }
    }

    private static readonly Color[] TypeColors =
    [
        ColorTranslator.FromHtml("#6B7079"), ColorTranslator.FromHtml("#3A3E45"),
        ColorTranslator.FromHtml("#A4AAB2"), ColorTranslator.FromHtml("#F2F3F5"),
    ];

    private void PaintTypes(Graphics g, Rectangle r)
    {
        if (_report is not { } rep) return;
        var t = rep.Totals;
        var half = (r.Width - 60) / 2f;
        var parts = IsCost
            ? new[] { ("Input", t.Cost.Input), ("Cache read", t.Cost.CacheRead), ("Cache write", t.Cost.CacheWrite), ("Output", t.Cost.Output) }
            : new[] { ("Input", (double)t.Input), ("Cache read", (double)t.CacheRead), ("Cache write", (double)t.CacheWrite), ("Output", (double)t.Output) };
        StackedBar(g, IsCost ? "Cost by type" : "Tokens by type", parts, TypeColors, 0, half);

        if (IsCost)
        {
            var speed = new[] { ("Standard", rep.StandardSpeed.Total), ("Fast", rep.FastSpeed.Total) };
            StackedBar(g, "Cost by speed", speed, [ColorTranslator.FromHtml("#3A3E45"), ColorTranslator.FromHtml("#F2F3F5")], half + 60, half);
            var premium = $"Fast premium {Fmt.Money(rep.FastSpeed.Total / 2)}";
            Ui.DrawText(g, premium, Theme.Small, Theme.Muted, r.Width - Ui.Width(g, premium, Theme.Small), 10);
        }
    }

    private void StackedBar(Graphics g, string title, (string label, double value)[] parts, Color[] colors, float x, float width)
    {
        Ui.DrawText(g, title, Theme.Strong, Theme.Text, x, 8);
        var total = parts.Sum(p => p.value);
        var bx = x;
        var by = 44f;
        if (total <= 0)
        {
            using var empty = new SolidBrush(Theme.Track);
            using var path = Theme.RoundedRect(new RectangleF(x, by, width, 10), 5);
            g.FillPath(empty, path);
        }
        for (var i = 0; i < parts.Length && total > 0; i++)
        {
            var w = (float)(parts[i].value / total * width);
            if (w < 0.5f) continue;
            using var b = new SolidBrush(colors[i]);
            using var path = Theme.RoundedRect(new RectangleF(bx, by, Math.Max(w - 2, 1), 10), 5);
            g.FillPath(b, path);
            bx += w;
        }
        var lx = x;
        var ly = 66f;
        for (var i = 0; i < parts.Length; i++)
        {
            var text = parts[i].label;
            var value = IsCost ? Fmt.Money(parts[i].value) : Fmt.Tokens(parts[i].value);
            var w = 14 + Ui.Width(g, text, Theme.Body) + 4 + Ui.Width(g, value, Theme.Strong) + 18;
            if (lx + w > x + width) { lx = x; ly += 24; }
            using (var b = new SolidBrush(colors[i])) g.FillRectangle(b, lx, ly + 5, 9, 9);
            Ui.DrawText(g, text, Theme.Body, Theme.Muted, lx + 14, ly);
            Ui.DrawText(g, value, Theme.Strong, Theme.Text, lx + 14 + Ui.Width(g, text, Theme.Body) + 2, ly - 1);
            lx += w;
        }
    }

    // ---------- chart ----------

    private void DrawChart(TimeSpan bucket)
    {
        if (_report is not { } rep) return;
        var hourly = bucket < TimeSpan.FromDays(1);
        _chartTitle.Text = (hourly ? "Hourly " : "Daily ") + (IsCost ? "cost" : "processed tokens");

        var plot = _chart.Plot;
        plot.Clear();
        var xs = rep.Buckets.Select(b => b.StartUtc.LocalDateTime).ToArray();
        foreach (var p in rep.ByProvider.Select(p => p.Key).Reverse())
        {
            var ys = rep.Buckets.Select(b => b.ByProvider.TryGetValue(p, out var t) ? Value(t) : 0).ToArray();
            if (xs.Length == 0) continue;
            var color = Theme.ForProvider(p);
            var s = plot.Add.Scatter(xs, ys);
            s.Color = Ui.Sp(color);
            s.LineWidth = 2f;
            s.MarkerSize = 0;
            s.Smooth = true;
            s.FillY = true;
            s.FillYColor = Ui.Sp(color, 40);
            s.LegendText = ProviderFactory.DisplayName(p);
        }

        plot.Axes.DateTimeTicksBottom();
        plot.Axes.Left.TickGenerator = new SP.TickGenerators.NumericAutomatic
        {
            LabelFormatter = v => IsCost ? (v == 0 ? "0" : $"${v:0.##}") : Fmt.Tokens(v),
        };
        var max = rep.Buckets.Count == 0 ? 1 : rep.Buckets.Max(b => b.ByProvider.Values.Select(Value).DefaultIfEmpty(0).Max());
        if (xs.Length > 1)
            plot.Axes.SetLimits(xs[0].ToOADate(), xs[^1].ToOADate(), 0, Math.Max(max * 1.15, IsCost ? 1 : 1000));
        plot.Grid.XAxisStyle.MajorLineStyle.Width = 0;
        plot.ShowLegend(SP.Alignment.UpperLeft);
        _chart.Refresh();
    }

    // ---------- breakdown ----------

    private void StyleGrid()
    {
        var g = _grid;
        g.BackgroundColor = Theme.Background;
        g.BorderStyle = BorderStyle.None;
        g.GridColor = Theme.PanelBorder;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        g.RowHeadersVisible = false;
        g.AllowUserToAddRows = false;
        g.AllowUserToResizeRows = false;
        g.ReadOnly = true;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.ScrollBars = ScrollBars.None;
        g.Margin = new Padding(0);
        g.RowTemplate.Height = 46;
        g.ColumnHeadersHeight = 40;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Background, ForeColor = Theme.Text, SelectionBackColor = Theme.Panel,
            SelectionForeColor = Theme.Text, Font = Theme.Body, Padding = new Padding(6, 0, 6, 0),
        };
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Background, ForeColor = Theme.Muted, SelectionBackColor = Theme.Background,
            SelectionForeColor = Theme.Muted, Font = Theme.Body, Padding = new Padding(6, 0, 6, 0),
        };
        g.Columns.Add("n", "#");
        g.Columns.Add("name", "Model");
        g.Columns.Add("cost", "Cost");
        g.Columns.Add("share", "Share");
        g.Columns.Add("tokens", "Tokens");
        g.Columns[0].Width = 44;
        g.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach (var i in new[] { 2, 3, 4 })
        {
            g.Columns[i].Width = 110;
            g.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            g.Columns[i].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        g.Columns[0].DefaultCellStyle.ForeColor = Theme.Muted;
        g.Columns[3].DefaultCellStyle.ForeColor = Theme.Muted;
        foreach (DataGridViewColumn c in g.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
        g.CellPainting += PaintNameCell;
    }

    private void FillGrid()
    {
        _grid.Rows.Clear();
        if (_report is not { } rep) return;
        var byDay = _breakdownMode.Value == "day";
        _grid.Columns[1].HeaderText = byDay ? "Day" : "Model";
        var total = Math.Max(1e-9, Value(rep.Totals));

        IEnumerable<(string name, string provider, TokenTotals t)> rows = byDay
            ? rep.Buckets
                .GroupBy(b => b.StartUtc.LocalDateTime.Date)
                .Select(d => (d.Key.ToString("ddd d MMM"), "",
                    d.SelectMany(b => b.ByProvider.Values).Aggregate(TokenTotals.Zero, Sum)))
                .Where(r => r.Item3.Total > 0)
                .Reverse()
            : rep.ByModel.Select(m => (m.Key, m.ProviderId, m.Totals));

        var i = 0;
        foreach (var (name, provider, t) in rows)
        {
            var idx = _grid.Rows.Add(++i, name, t.Cost.Priced || t.Cost.Total > 0 ? Fmt.Money(t.Cost.Total) : "–",
                $"{Value(t) / total:P1}", Fmt.Tokens(t.Total));
            _grid.Rows[idx].Tag = (provider, Value(t) / total);
        }
        _grid.Height = _grid.ColumnHeadersHeight + Math.Max(1, _grid.Rows.Count) * _grid.RowTemplate.Height + 2;
        _grid.ClearSelection();
    }

    private static TokenTotals Sum(TokenTotals a, TokenTotals b) =>
        new(a.Input + b.Input, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite, a.Output + b.Output, a.Cost + b.Cost);

    /// <summary>Model name with the provider's dot and a bar under it sized by share.</summary>
    private void PaintNameCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.ColumnIndex != 1 || e.RowIndex < 0 || e.Graphics is null) return;
        if (_grid.Rows[e.RowIndex].Tag is not (string provider, double share)) return;
        e.PaintBackground(e.CellBounds, true);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var x = e.CellBounds.X + 8f;
        var color = provider.Length > 0 ? Theme.ForProvider(provider) : Theme.Text;
        if (provider.Length > 0)
        {
            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, x, e.CellBounds.Y + 13, 10, 10);
            x += 18;
        }
        Ui.DrawText(g, e.Value?.ToString() ?? "", Theme.Body, Theme.Text, x, e.CellBounds.Y + 8);
        var barWidth = Math.Max(4f, (float)(share * (e.CellBounds.Width - 30)));
        using (var bar = new SolidBrush(color))
            g.FillRectangle(bar, x, e.CellBounds.Y + 32, barWidth, 3);
        e.Handled = true;
    }
}
