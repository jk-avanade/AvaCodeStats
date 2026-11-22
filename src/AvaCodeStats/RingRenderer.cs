using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AvaCodeStats.Core;

namespace AvaCodeStats;

/// <summary>
/// Draws the burn ring: the arc is usage so far, the white notch is where an even pace would be.
/// Over pace: the stretch past the notch is the overburn (status colour).
/// Under pace: the stretch up to the notch is drawn faint, as budget banked.
/// </summary>
internal static class RingRenderer
{
    public static void Draw(Graphics g, RectangleF bounds, double used, double pace, PaceStatus status, float thickness)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var inset = thickness / 2f;
        var r = RectangleF.Inflate(bounds, -inset, -inset);
        var color = Theme.ForStatus(status);
        used = Math.Clamp(used, 0, 1);
        pace = Math.Clamp(pace, 0, 1);

        using (var track = new Pen(Theme.Track, thickness))
            g.DrawEllipse(track, r);

        if (status == PaceStatus.Unknown)
        {
            if (used > 0) Arc(g, r, thickness, 0, used, Theme.Grey);
            return;
        }

        if (used <= pace)
        {
            Arc(g, r, thickness, 0, used, color);
            Arc(g, r, thickness, used, pace, Color.FromArgb(70, color));
        }
        else
        {
            // Within pace in a calm colour, overburn in the status colour.
            Arc(g, r, thickness, 0, pace, status == PaceStatus.OnPace ? color : Theme.Blue);
            Arc(g, r, thickness, pace, used, color);
        }

        // Pace notch, crossing the ring.
        var angle = (pace * 360 - 90) * Math.PI / 180;
        var cx = r.X + r.Width / 2f;
        var cy = r.Y + r.Height / 2f;
        var radius = r.Width / 2f;
        var inner = radius - thickness * 0.75f;
        var outer = radius + thickness * 0.75f;
        using var notch = new Pen(Color.White, Math.Max(1.5f, thickness * 0.3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(notch,
            cx + (float)(Math.Cos(angle) * inner), cy + (float)(Math.Sin(angle) * inner),
            cx + (float)(Math.Cos(angle) * outer), cy + (float)(Math.Sin(angle) * outer));
    }

    private static void Arc(Graphics g, RectangleF r, float thickness, double from, double to, Color c)
    {
        var sweep = (float)((to - from) * 360);
        if (sweep <= 0.1f) return;
        using var pen = new Pen(c, thickness) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        g.DrawArc(pen, r, (float)(from * 360 - 90), sweep);
    }

    /// <summary>Tray icon. The caller owns the returned icon and must dispose it via <see cref="DestroyIcon(Icon)"/>.</summary>
    public static Icon CreateIcon(int size, double used, double pace, PaceStatus status)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            var thickness = Math.Max(3f, size * 0.24f);
            // Leave room for the notch overhang.
            var pad = thickness * 0.3f;
            Draw(g, new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad), used, pace, status, thickness);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static void DestroyIcon(Icon? icon)
    {
        if (icon is null) return;
        var handle = icon.Handle;
        icon.Dispose();
        NativeDestroyIcon(handle);
    }

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static extern bool NativeDestroyIcon(IntPtr handle);
}

/// <summary>Large ring with the percentage in the middle, for the details window.</summary>
internal sealed class RingControl : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Used { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Pace { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public PaceStatus Status { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string CenterText { get; set; } = "";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string CenterSubText { get; set; } = "";

    public RingControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var side = Math.Min(Width, Height) - 16f;
        var rect = new RectangleF((Width - side) / 2f, (Height - side) / 2f, side, side);
        RingRenderer.Draw(e.Graphics, rect, Used, Pace, Status, side * 0.11f);

        using var big = new Font("Segoe UI Semibold", side * 0.13f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", side * 0.065f, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Theme.Text);
        using var muted = new SolidBrush(Theme.Muted);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var cx = Width / 2f;
        var cy = Height / 2f;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        e.Graphics.DrawString(CenterText, big, text, cx, cy - side * 0.04f, fmt);
        e.Graphics.DrawString(CenterSubText, small, muted, cx, cy + side * 0.1f, fmt);
    }
}
