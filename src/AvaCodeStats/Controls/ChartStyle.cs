using AvaCodeStats.Theming;
using SP = ScottPlot;

namespace AvaCodeStats.Controls;

/// <summary>Applies the current palette to a ScottPlot plot (transparent, so the card shows through).</summary>
internal static class ChartStyle
{
    public static void Apply(SP.Plot plot)
    {
        var p = ThemeService.Current;
        plot.FigureBackground.Color = SP.Colors.Transparent;
        plot.DataBackground.Color = SP.Colors.Transparent;
        plot.Axes.Color(Sp(p.TextSecondary));
        plot.Axes.FrameColor(SP.Colors.Transparent);
        plot.Grid.MajorLineColor = Sp(p.Grid);
        plot.Grid.XAxisStyle.MajorLineStyle.Width = 0;
        plot.Legend.BackgroundColor = SP.Colors.Transparent;
        plot.Legend.OutlineColor = SP.Colors.Transparent;
        plot.Legend.FontColor = Sp(p.Text);
        plot.Legend.ShadowColor = SP.Colors.Transparent;
        plot.Legend.FontName = "Segoe UI";
        plot.Legend.FontSize = 12;
        foreach (var axis in new SP.IAxis[] { plot.Axes.Bottom, plot.Axes.Left })
        {
            axis.TickLabelStyle.FontName = "Segoe UI";
            axis.TickLabelStyle.FontSize = 11;
            axis.Label.FontName = "Segoe UI";
            axis.Label.FontSize = 12;
        }
    }

    /// <summary>Date axis with short local labels ("23 Nov", "14:00").</summary>
    public static void DateAxis(SP.Plot plot, bool hours = false)
    {
        var axis = plot.Axes.DateTimeTicksBottom();
        if (axis.TickGenerator is SP.TickGenerators.DateTimeAutomatic auto)
            auto.LabelFormatter = d => hours && d.TimeOfDay != TimeSpan.Zero ? d.ToString("HH:mm") : d.ToString("d MMM");
    }

    public static SP.Color Sp(System.Windows.Media.Color c, byte? alpha = null) => new(c.R, c.G, c.B, alpha ?? c.A);
}
