using System.Windows;
using System.Windows.Media;
using AvaCodeStats.Theming;

namespace AvaCodeStats.Controls;

/// <summary>Horizontal 100% stacked bar (cost or tokens by type).</summary>
internal sealed class StackBar : FrameworkElement
{
    private IReadOnlyList<(double value, Color color)> _parts = [];

    public StackBar() => Height = 10;

    public void SetParts(IReadOnlyList<(double value, Color color)> parts)
    {
        _parts = parts;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var total = _parts.Sum(p => Math.Max(0, p.value));
        var r = h / 2;
        if (total <= 0)
        {
            dc.DrawRoundedRectangle(ThemeService.Brush(ThemeService.Current.Track), null, new Rect(0, 0, w, h), r, r);
            return;
        }
        var x = 0.0;
        foreach (var (value, color) in _parts)
        {
            var segment = value / total * w;
            if (segment < 0.75) continue;
            dc.DrawRoundedRectangle(ThemeService.Brush(color), null, new Rect(x, 0, Math.Max(1, segment - 2), h), Math.Min(r, segment / 2), r);
            x += segment;
        }
    }
}
