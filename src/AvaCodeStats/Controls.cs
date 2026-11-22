using System.ComponentModel;
using System.Drawing.Drawing2D;
using ScottPlot.WinForms;
using SP = ScottPlot;

namespace AvaCodeStats;

/// <summary>A row of toggle buttons where exactly one is selected (Cost | Tokens | Limits…).</summary>
internal sealed class Segmented : FlowLayoutPanel
{
    private readonly List<(string value, Button button)> _items = [];

    public event EventHandler? Changed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value { get; private set; } = "";

    public Segmented(params (string value, string label)[] items)
    {
        AutoSize = true;
        WrapContents = false;
        BackColor = Theme.Panel;
        Padding = new Padding(3);
        Margin = new Padding(0, 0, 10, 0);
        foreach (var (value, label) in items)
        {
            var b = new Button
            {
                Text = label, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Muted,
                BackColor = Theme.Panel, Margin = new Padding(0), Padding = new Padding(8, 2, 8, 2),
                Font = Theme.Body, Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.Selected;
            b.Click += (_, _) => Select(value, raise: true);
            _items.Add((value, b));
            Controls.Add(b);
        }
        if (_items.Count > 0) Select(_items[0].value, raise: false);
    }

    public void Select(string value, bool raise)
    {
        Value = value;
        foreach (var (v, b) in _items)
        {
            b.BackColor = v == value ? Theme.Selected : Theme.Panel;
            b.ForeColor = v == value ? Theme.Text : Theme.Muted;
        }
        if (raise) Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A control painted by a delegate; used for the summary blocks.</summary>
internal sealed class PaintBox : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<Graphics, Rectangle>? Painter { get; set; }

    public PaintBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        Margin = new Padding(0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Painter?.Invoke(e.Graphics, ClientRectangle);
    }
}

internal static class Ui
{
    public static Button FlatButton(string text)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text, BackColor = Theme.Panel,
            Padding = new Padding(8, 2, 8, 2), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Theme.PanelBorder;
        b.FlatAppearance.MouseOverBackColor = Theme.Selected;
        return b;
    }

    public static FormsPlot NewPlot(Color? background = null)
    {
        var fp = new FormsPlot { Margin = new Padding(0) };
        var bg = Sp(background ?? Theme.Panel);
        var plot = fp.Plot;
        plot.FigureBackground.Color = bg;
        plot.DataBackground.Color = bg;
        plot.Axes.Color(Sp(Theme.Muted));
        plot.Grid.MajorLineColor = Sp(Theme.Grid);
        plot.Legend.BackgroundColor = Sp(Theme.Panel);
        plot.Legend.FontColor = Sp(Theme.Text);
        plot.Legend.OutlineColor = Sp(Theme.PanelBorder);
        return fp;
    }

    public static SP.Color Sp(Color c, byte? alpha = null) => new(c.R, c.G, c.B, alpha ?? c.A);

    public static void DrawText(Graphics g, string text, Font font, Color color, float x, float y)
    {
        using var b = new SolidBrush(color);
        g.DrawString(text, font, b, x, y);
    }

    public static float Width(Graphics g, string text, Font font) => g.MeasureString(text, font).Width;
}
