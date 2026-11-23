using System.Windows;
using System.Windows.Media;
using AvaCodeStats.Core;
using AvaCodeStats.Theming;

namespace AvaCodeStats.Controls;

/// <summary>Large burn ring for the Burn page: arc = used, notch = even pace (same rules as the tray ring).</summary>
internal sealed class RingView : FrameworkElement
{
    public static readonly DependencyProperty UsedProperty = Dp(nameof(Used), 0.0);
    public static readonly DependencyProperty PaceProperty = Dp(nameof(Pace), 0.0);
    public static readonly DependencyProperty StatusProperty = Dp(nameof(Status), PaceStatus.Unknown);

    public double Used { get => (double)GetValue(UsedProperty); set => SetValue(UsedProperty, value); }
    public double Pace { get => (double)GetValue(PaceProperty); set => SetValue(PaceProperty, value); }
    public PaceStatus Status { get => (PaceStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    public RingView()
    {
        Loaded += (_, _) => ThemeService.Changed += InvalidateVisual;
        Unloaded += (_, _) => ThemeService.Changed -= InvalidateVisual;
    }

    private static DependencyProperty Dp<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(RingView),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var p = ThemeService.Current;
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;
        var thickness = side * 0.09;
        var radius = side / 2 - thickness;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var used = Math.Clamp(Used, 0, 1);
        var pace = Math.Clamp(Pace, 0, 1);
        var status = p.ForStatus(Status);

        dc.DrawEllipse(null, new Pen(ThemeService.Brush(p.Track), thickness), c, radius, radius);
        if (used <= pace)
        {
            Arc(dc, c, radius, thickness, 0, used, ThemeService.Brush(status));
            Arc(dc, c, radius, thickness, used, pace, ThemeService.Brush(status, 70));
        }
        else
        {
            Arc(dc, c, radius, thickness, 0, pace, ThemeService.Brush(status, 110));
            Arc(dc, c, radius, thickness, pace, used, ThemeService.Brush(status));
        }

        if (Status != PaceStatus.Unknown)
        {
            var a = (pace * 360 - 90) * Math.PI / 180;
            var dir = new Vector(Math.Cos(a), Math.Sin(a));
            dc.DrawLine(new Pen(ThemeService.Brush(p.Marker), Math.Max(2, thickness * 0.28)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                c + dir * (radius - thickness * 0.85), c + dir * (radius + thickness * 0.85));
        }
    }

    private static void Arc(DrawingContext dc, Point c, double r, double thickness, double from, double to, Brush brush)
    {
        var sweep = (to - from) * 360;
        if (sweep <= 0.2) return;
        if (sweep >= 359.9) { dc.DrawEllipse(null, new Pen(brush, thickness), c, r, r); return; }
        Point At(double f)
        {
            var a = (f * 360 - 90) * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(from), false, false);
            ctx.ArcTo(At(to), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat }, g);
    }
}
