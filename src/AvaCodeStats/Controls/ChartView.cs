using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AvaCodeStats.Theming;

namespace AvaCodeStats.Controls;

internal enum SeriesKind { Line, Area, Bars }

internal sealed class ChartSeries
{
    public required string Name { get; init; }
    public required Color Color { get; init; }
    public required double[] X { get; init; }
    public required double[] Y { get; init; }
    public SeriesKind Kind { get; init; } = SeriesKind.Line;
    public bool Smooth { get; init; }
    public bool Dashed { get; init; }
    public double Thickness { get; init; } = 2;
    /// <summary>Per-bar colours (bars only); falls back to <see cref="Color"/>.</summary>
    public Color[]? BarColors { get; init; }
    /// <summary>Text drawn above each bar (bars only).</summary>
    public string[]? BarLabels { get; init; }
    public bool InLegend { get; init; } = true;
    /// <summary>Include in the hover tooltip.</summary>
    public bool InTooltip { get; init; } = true;
}

/// <summary>A labelled horizontal reference line (quota, even pace, budget).</summary>
internal sealed record ReferenceLine(double Y, string Label, Color Color, bool Dashed = true);

internal enum XAxisKind { Days, Hours, Categories }

internal sealed class ChartModel
{
    public List<ChartSeries> Series { get; } = [];
    public List<ReferenceLine> Lines { get; } = [];
    /// <summary>Extra legend entries (e.g. "Estimated" bars).</summary>
    public List<(string Name, Color Color, bool Box)> LegendExtras { get; } = [];
    public XAxisKind XKind { get; init; } = XAxisKind.Days;
    /// <summary>X values: OLE dates for Days/Hours, 0..n-1 for Categories.</summary>
    public double XMin { get; init; }
    public double XMax { get; init; }
    public string[]? Categories { get; init; }
    public double? NowX { get; init; }
    public double? YMax { get; init; }
    /// <summary>Bar width in X units (e.g. 0.7 of a day).</summary>
    public double BarWidth { get; init; } = 0.7;
    public Func<double, string> FormatY { get; init; } = v => v.ToString("N0", CultureInfo.CurrentCulture);
    public Func<double, string> FormatValue { get; init; } = v => v.ToString("N0", CultureInfo.CurrentCulture);
    public string? EmptyText { get; init; }
}

/// <summary>
/// Theme-aware chart drawn with WPF: horizontal gridlines only, readable axis labels that thin
/// out to fit, labelled reference lines, a legend, and a hover guide with a tooltip.
/// </summary>
internal sealed class ChartView : FrameworkElement
{
    private ChartModel? _model;
    private Point? _mouse;
    private Rect _plot;

    private const double LegendHeight = 30;
    private const double AxisFont = 12;

    public ChartView()
    {
        ClipToBounds = true;
        Loaded += (_, _) => ThemeService.Changed += InvalidateVisual;
        Unloaded += (_, _) => ThemeService.Changed -= InvalidateVisual;
    }

    public ChartModel? Model
    {
        get => _model;
        set { _model = value; InvalidateVisual(); }
    }

    protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _mouse = null; InvalidateVisual(); }

    // ---------- text ----------

    private static readonly Typeface Regular = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Semibold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private FormattedText Text(string s, double size, Color c, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Semibold : Regular, size,
            ThemeService.Brush(c), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ---------- scales ----------

    private static (double max, double step) NiceScale(double max, int targetTicks = 5)
    {
        if (max <= 0 || double.IsNaN(max)) max = 1;
        var raw = max / targetTicks;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(s => s >= raw);
        return (Math.Ceiling(max / step - 1e-9) * step, step);
    }

    private double Px(double x, ChartModel m) => _plot.Left + (x - m.XMin) / Math.Max(1e-9, m.XMax - m.XMin) * _plot.Width;

    private double Py(double y, double yMax) => _plot.Bottom - Math.Clamp(y / yMax, -0.05, 1.05) * _plot.Height;

    protected override void OnRender(DrawingContext dc)
    {
        var m = _model;
        var p = ThemeService.Current;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit-testable for hover
        if (m is null || ActualWidth < 80 || ActualHeight < 80) return;

        // Y scale
        var dataMax = m.Series.SelectMany(s => s.Y).DefaultIfEmpty(0).Max();
        var lineMax = m.Lines.Select(l => l.Y).DefaultIfEmpty(0).Max();
        var (yMax, yStep) = NiceScale(m.YMax ?? Math.Max(dataMax, lineMax) * 1.08);
        var yLabels = Enumerable.Range(0, (int)Math.Round(yMax / yStep) + 1).Select(i => i * yStep).ToList();
        var labelWidth = yLabels.Max(v => Text(m.FormatY(v), AxisFont, p.TextSecondary).Width);

        _plot = new Rect(labelWidth + 14, LegendHeight + 10, Math.Max(10, ActualWidth - labelWidth - 14 - 12), Math.Max(10, ActualHeight - LegendHeight - 10 - 30));

        DrawLegend(dc, m, p);

        // Gridlines and Y labels
        var grid = new Pen(ThemeService.Brush(p.Grid), 1);
        foreach (var v in yLabels)
        {
            var y = Math.Round(Py(v, yMax)) + 0.5;
            dc.DrawLine(v == 0 ? new Pen(ThemeService.Brush(p.TextSecondary, 90), 1) : grid, new Point(_plot.Left, y), new Point(_plot.Right, y));
            var t = Text(m.FormatY(v), AxisFont, p.TextSecondary);
            dc.DrawText(t, new Point(_plot.Left - 10 - t.Width, y - t.Height / 2));
        }

        DrawXAxis(dc, m, p);

        dc.PushClip(new RectangleGeometry(new Rect(_plot.Left, _plot.Top - 6, _plot.Width, _plot.Height + 6)));
        foreach (var s in m.Series.Where(s => s.Kind == SeriesKind.Bars)) DrawBars(dc, m, s, yMax);
        foreach (var s in m.Series.Where(s => s.Kind == SeriesKind.Area)) DrawLine(dc, m, s, yMax, fill: true);
        foreach (var s in m.Series.Where(s => s.Kind == SeriesKind.Line)) DrawLine(dc, m, s, yMax, fill: false);
        dc.Pop();

        DrawReferenceLines(dc, m, p, yMax);

        if (m.NowX is { } now && now >= m.XMin && now <= m.XMax)
        {
            var x = Math.Round(Px(now, m)) + 0.5;
            dc.DrawLine(new Pen(ThemeService.Brush(p.TextSecondary, 140), 1) { DashStyle = new DashStyle([2, 3], 0) },
                new Point(x, _plot.Top), new Point(x, _plot.Bottom));
            var t = Text("Now", 11.5, p.TextSecondary, bold: true);
            dc.DrawText(t, new Point(Math.Min(x + 4, _plot.Right - t.Width), _plot.Top));
        }

        if (m.EmptyText is { } empty && dataMax <= 0)
        {
            var t = Text(empty, 13, p.TextSecondary);
            dc.DrawText(t, new Point(_plot.Left + (_plot.Width - t.Width) / 2, _plot.Top + (_plot.Height - t.Height) / 2));
        }

        DrawHover(dc, m, p, yMax);
    }

    private void DrawLegend(DrawingContext dc, ChartModel m, Palette p)
    {
        var x = _plot.Left;
        var items = m.Series.Where(s => s.InLegend).Select(s => (s.Name, s.Color, Box: s.Kind == SeriesKind.Bars, s.Dashed))
            .Concat(m.LegendExtras.Select(e => (e.Name, e.Color, e.Box, Dashed: false)));
        foreach (var (name, color, box, dashed) in items)
        {
            var cy = 12.0;
            if (box)
                dc.DrawRoundedRectangle(ThemeService.Brush(color), null, new Rect(x, cy - 5, 10, 10), 2, 2);
            else
                dc.DrawLine(new Pen(ThemeService.Brush(color), 2.5) { DashStyle = dashed ? new DashStyle([1.5, 1.5], 0) : null, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    new Point(x, cy), new Point(x + 14, cy));
            var t = Text(name, 12.5, p.Text);
            dc.DrawText(t, new Point(x + 20, cy - t.Height / 2));
            x += 20 + t.Width + 20;
        }
    }

    private void DrawXAxis(DrawingContext dc, ChartModel m, Palette p)
    {
        var ticks = new List<(double x, string label)>();
        if (m.XKind == XAxisKind.Categories && m.Categories is { } cats)
        {
            var widest = cats.Select(c => Text(c, AxisFont, p.TextSecondary).Width).DefaultIfEmpty(40).Max() + 16;
            var every = Math.Max(1, (int)Math.Ceiling(widest / Math.Max(1, _plot.Width / Math.Max(1, cats.Length))));
            for (var i = 0; i < cats.Length; i += every) ticks.Add((i, cats[i]));
        }
        else
        {
            var start = DateTime.FromOADate(m.XMin);
            var end = DateTime.FromOADate(m.XMax);
            var hours = m.XKind == XAxisKind.Hours;
            var sample = Text(hours ? "00:00" : "30 Nov", AxisFont, p.TextSecondary).Width + 18;
            var fit = Math.Max(1, (int)(_plot.Width / sample));
            var span = (end - start).TotalHours;
            int[] steps = hours ? [1, 2, 3, 4, 6, 12, 24] : [24, 48, 72, 168, 336, 720];
            var stepHours = steps.FirstOrDefault(s => span / s <= fit, steps[^1]);
            var t = hours ? new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0) : start.Date;
            if (t < start) t = t.AddHours(hours ? 1 : 24);
            // Weekly steps start on a Monday; day steps on the period's first day.
            while (stepHours == 168 && t.DayOfWeek != DayOfWeek.Monday) t = t.AddDays(1);
            for (; t <= end; t = t.AddHours(stepHours))
                ticks.Add((t.ToOADate(), hours && t.TimeOfDay != TimeSpan.Zero ? t.ToString("HH:mm") : t.ToString("d MMM")));
        }

        var lastRight = double.MinValue;
        foreach (var (x, label) in ticks)
        {
            var px = Px(x, m);
            var t = Text(label, AxisFont, p.TextSecondary);
            var left = Math.Clamp(px - t.Width / 2, 0, ActualWidth - t.Width);
            if (left < lastRight + 6) continue;
            dc.DrawText(t, new Point(left, _plot.Bottom + 8));
            lastRight = left + t.Width;
        }
    }

    private void DrawBars(DrawingContext dc, ChartModel m, ChartSeries s, double yMax)
    {
        var p = ThemeService.Current;
        var unit = _plot.Width / Math.Max(1e-9, m.XMax - m.XMin);
        var w = Math.Max(2, Math.Min(m.BarWidth * unit, 64));
        for (var i = 0; i < s.X.Length; i++)
        {
            var x = Px(s.X[i], m);
            var top = Py(s.Y[i], yMax);
            var h = _plot.Bottom - top;
            var color = s.BarColors?[i] ?? s.Color;
            if (h >= 0.5)
            {
                var r = Math.Min(4, Math.Min(w / 2, h));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x - w / 2, _plot.Bottom), true, true);
                    c.LineTo(new Point(x - w / 2, top + r), false, false);
                    c.ArcTo(new Point(x - w / 2 + r, top), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                    c.LineTo(new Point(x + w / 2 - r, top), false, false);
                    c.ArcTo(new Point(x + w / 2, top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                    c.LineTo(new Point(x + w / 2, _plot.Bottom), false, false);
                }
                g.Freeze();
                dc.DrawGeometry(ThemeService.Brush(color), null, g);
            }
            if (s.BarLabels?[i] is { Length: > 0 } label)
            {
                var t = Text(label, 12, p.Text, bold: true);
                dc.DrawText(t, new Point(x - t.Width / 2, Math.Max(_plot.Top - 4, top - t.Height - 2)));
            }
        }
    }

    private void DrawLine(DrawingContext dc, ChartModel m, ChartSeries s, double yMax, bool fill)
    {
        if (s.X.Length < 2) return;
        var pts = s.X.Select((x, i) => new Point(Px(x, m), Py(s.Y[i], yMax))).ToArray();
        var line = new StreamGeometry();
        using (var c = line.Open())
        {
            c.BeginFigure(pts[0], false, false);
            AddPath(c, pts, s.Smooth);
        }
        line.Freeze();

        if (fill)
        {
            var area = new StreamGeometry();
            using (var c = area.Open())
            {
                c.BeginFigure(new Point(pts[0].X, _plot.Bottom), true, true);
                c.LineTo(pts[0], false, false);
                AddPath(c, pts, s.Smooth);
                c.LineTo(new Point(pts[^1].X, _plot.Bottom), false, false);
            }
            area.Freeze();
            var gradient = new LinearGradientBrush(
                Color.FromArgb(ThemeService.Current.IsDark ? (byte)110 : (byte)80, s.Color.R, s.Color.G, s.Color.B),
                Color.FromArgb(8, s.Color.R, s.Color.G, s.Color.B), 90);
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, area);
        }

        var pen = new Pen(ThemeService.Brush(s.Color), s.Thickness)
        {
            LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
            DashStyle = s.Dashed ? new DashStyle([2, 2.5], 0) : null,
        };
        dc.DrawGeometry(null, pen, line);
    }

    /// <summary>Straight segments, or a Catmull-Rom curve that never dips below the baseline.</summary>
    private void AddPath(StreamGeometryContext c, Point[] pts, bool smooth)
    {
        if (!smooth || pts.Length < 3)
        {
            for (var i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, true);
            return;
        }
        for (var i = 0; i < pts.Length - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(pts.Length - 1, i + 2)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Min(_plot.Bottom, p1.Y + (p2.Y - p0.Y) / 6));
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Min(_plot.Bottom, p2.Y - (p3.Y - p1.Y) / 6));
            // Flat stretches stay flat (no bumps between two zero days).
            if (Math.Abs(p1.Y - p2.Y) < 0.5) { c1.Y = p1.Y; c2.Y = p2.Y; }
            c.BezierTo(c1, c2, p2, true, true);
        }
    }

    private void DrawReferenceLines(DrawingContext dc, ChartModel m, Palette p, double yMax)
    {
        var usedLabelYs = new List<double>();
        foreach (var l in m.Lines)
        {
            if (l.Y < 0 || l.Y > yMax * 1.02) continue;
            var y = Math.Round(Py(l.Y, yMax)) + 0.5;
            dc.DrawLine(new Pen(ThemeService.Brush(l.Color), 1.5) { DashStyle = l.Dashed ? new DashStyle([4, 3], 0) : null },
                new Point(_plot.Left, y), new Point(_plot.Right, y));
            var t = Text(l.Label, 11.5, l.Color, bold: true);
            var ty = y - t.Height - 3;
            if (usedLabelYs.Any(u => Math.Abs(u - ty) < t.Height)) ty = y + 3; // avoid overlapping labels
            usedLabelYs.Add(ty);
            var box = new Rect(_plot.Right - t.Width - 10, ty - 1, t.Width + 8, t.Height + 2);
            dc.DrawRoundedRectangle(ThemeService.Brush(p.IsDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White, 220), null, box, 3, 3);
            dc.DrawText(t, new Point(box.Left + 4, ty));
        }
    }

    private void DrawHover(DrawingContext dc, ChartModel m, Palette p, double yMax)
    {
        if (_mouse is not { } mouse || !_plot.Contains(mouse)) return;
        var hoverSeries = m.Series.Where(s => s.InTooltip && s.X.Length > 0).ToList();
        if (hoverSeries.Count == 0) return;

        // Nearest X among all tooltip series.
        var allX = hoverSeries.SelectMany(s => s.X).Distinct().ToList();
        var x = allX.OrderBy(v => Math.Abs(Px(v, m) - mouse.X)).First();
        var px = Px(x, m);

        var rows = new List<(string name, string value, Color color)>();
        foreach (var s in hoverSeries)
        {
            var i = Array.IndexOf(s.X, x);
            if (i < 0)
            {
                // Line series sampled at other times: show the latest value at or before x.
                i = Array.FindLastIndex(s.X, v => v <= x);
                if (i < 0 || s.Kind == SeriesKind.Bars) continue;
            }
            rows.Add((s.Name, m.FormatValue(s.Y[i]), s.BarColors?[i] ?? s.Color));
            if (s.Kind != SeriesKind.Bars)
                dc.DrawEllipse(ThemeService.Brush(s.Color), new Pen(ThemeService.Brush(p.IsDark ? Colors.Black : Colors.White), 2),
                    new Point(Px(s.X[i], m), Py(s.Y[i], yMax)), 4.5, 4.5);
        }
        if (rows.Count == 0) return;

        dc.DrawLine(new Pen(ThemeService.Brush(p.Text, 70), 1), new Point(Math.Round(px) + 0.5, _plot.Top), new Point(Math.Round(px) + 0.5, _plot.Bottom));

        var header = m.XKind switch
        {
            XAxisKind.Categories => m.Categories?[Math.Clamp((int)Math.Round(x), 0, m.Categories.Length - 1)] ?? "",
            XAxisKind.Hours => DateTime.FromOADate(x).ToString("ddd d MMM, HH:mm"),
            _ => DateTime.FromOADate(x).ToString(DateTime.FromOADate(x).TimeOfDay == TimeSpan.Zero || DateTime.FromOADate(x).Hour == 12 ? "ddd d MMM" : "ddd d MMM, HH:mm"),
        };
        var title = Text(header, 12.5, p.Text, bold: true);
        var lines = rows.Select(r => (r, name: Text(r.name, 12.5, p.TextSecondary), value: Text(r.value, 12.5, p.Text, bold: true))).ToList();
        var width = Math.Max(title.Width, lines.Max(l => 18 + l.name.Width + 16 + l.value.Width)) + 24;
        var height = 12 + title.Height + 6 + lines.Sum(l => l.name.Height + 3) + 8;
        var bx = px + 14 + width > _plot.Right ? px - 14 - width : px + 14;
        var by = Math.Clamp(mouse.Y - height / 2, _plot.Top, Math.Max(_plot.Top, _plot.Bottom - height));
        var rect = new Rect(bx, by, width, height);

        dc.DrawRoundedRectangle(ThemeService.Brush(Color.FromArgb(40, 0, 0, 0)), null, new Rect(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), 7, 7);
        dc.DrawRoundedRectangle(ThemeService.Brush(p.IsDark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Colors.White),
            new Pen(ThemeService.Brush(p.IsDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0)), 1), rect, 7, 7);
        dc.DrawText(title, new Point(rect.X + 12, rect.Y + 10));
        var yy = rect.Y + 10 + title.Height + 6;
        foreach (var (r, name, value) in lines)
        {
            dc.DrawRoundedRectangle(ThemeService.Brush(r.color), null, new Rect(rect.X + 12, yy + name.Height / 2 - 4, 8, 8), 2, 2);
            dc.DrawText(name, new Point(rect.X + 26, yy));
            dc.DrawText(value, new Point(rect.Right - 12 - value.Width, yy));
            yy += name.Height + 3;
        }
    }
}
