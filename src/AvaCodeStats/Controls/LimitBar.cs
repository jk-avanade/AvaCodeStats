using System.Windows;
using System.Windows.Media;
using AvaCodeStats.Core;
using AvaCodeStats.Theming;

namespace AvaCodeStats.Controls;

/// <summary>
/// Quota bar. The fill is the share left; the hatched rest is the share used; the tick is the share of
/// time left, i.e. where the fill would end at an even pace. Fill past the tick = budget in hand.
/// </summary>
internal sealed class LimitBar : FrameworkElement
{
    public static readonly DependencyProperty LeftProperty = Dp(nameof(Left), 0.0);
    public static readonly DependencyProperty TimeLeftProperty = Dp(nameof(TimeLeft), 0.0);
    public static readonly DependencyProperty StatusProperty = Dp(nameof(Status), PaceStatus.Unknown);

    public double Left { get => (double)GetValue(LeftProperty); set => SetValue(LeftProperty, value); }
    public double TimeLeft { get => (double)GetValue(TimeLeftProperty); set => SetValue(TimeLeftProperty, value); }
    public PaceStatus Status { get => (PaceStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    public LimitBar()
    {
        Height = 24;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => ThemeService.Changed += InvalidateVisual;
        Unloaded += (_, _) => ThemeService.Changed -= InvalidateVisual;
    }

    private static DependencyProperty Dp<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(LimitBar),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var p = ThemeService.Current;
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var radius = Math.Min(6, h / 2);
        var bar = new Rect(0, 0, w, h);
        var left = Math.Clamp(Left, 0, 1);

        dc.PushClip(new RectangleGeometry(bar, radius, radius));
        dc.DrawRectangle(ThemeService.Brush(p.Track), null, bar);
        dc.DrawRectangle(HatchBrush(p.Hatch, h), null, new Rect(w * left, 0, w * (1 - left), h));
        var fill = p.ForStatus(Status);
        dc.DrawRectangle(ThemeService.Brush(fill, (byte)(p.IsDark ? 200 : 230)), null, new Rect(0, 0, w * left, h));
        dc.Pop();

        if (Status != PaceStatus.Unknown)
        {
            var x = Math.Round(w * Math.Clamp(TimeLeft, 0, 1));
            var marker = new Pen(ThemeService.Brush(p.Marker), 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(marker, new Point(x, -3), new Point(x, h + 3));
        }
    }

    private static Brush HatchBrush(Color c, double height)
    {
        var size = Math.Max(6, height / 3);
        var geometry = new GeometryGroup();
        geometry.Children.Add(new LineGeometry(new Point(0, size), new Point(size, 0)));
        geometry.Children.Add(new LineGeometry(new Point(-size / 2, size / 2), new Point(size / 2, -size / 2)));
        geometry.Children.Add(new LineGeometry(new Point(size / 2, size * 1.5), new Point(size * 1.5, size / 2)));
        var brush = new DrawingBrush(new GeometryDrawing(null, new Pen(ThemeService.Brush(c), 1.5), geometry))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, size, size),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}
