using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AvaCodeStats.Core;
using AvaCodeStats.Theming;
using Gdi = System.Drawing;

namespace AvaCodeStats.Tray;

/// <summary>
/// The tray ring (GDI+, because a tray icon is an HICON). The arc is usage so far; the notch is where an
/// even pace would be. Over pace, the stretch past the notch is drawn in the status colour; under pace,
/// the stretch up to the notch is drawn faint (budget banked).
/// </summary>
internal static class RingRenderer
{
    public static Gdi.Icon CreateIcon(int size, double used, double pace, PaceStatus status, bool darkTaskbar)
    {
        using var bmp = new Gdi.Bitmap(size, size);
        using (var g = Gdi.Graphics.FromImage(bmp))
        {
            g.Clear(Gdi.Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var p = ThemeService.Current;
            var thickness = Math.Max(3f, size * 0.22f);
            var pad = thickness * 0.5f + size * 0.04f;
            var r = new Gdi.RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
            var statusColor = ToGdi(Palette.For(darkTaskbar, p.Brand).ForStatus(status));
            var baseColor = darkTaskbar ? Gdi.Color.FromArgb(235, 255, 255, 255) : Gdi.Color.FromArgb(220, 30, 30, 30);
            used = Math.Clamp(used, 0, 1);
            pace = Math.Clamp(pace, 0, 1);

            using (var track = new Gdi.Pen(darkTaskbar ? Gdi.Color.FromArgb(70, 255, 255, 255) : Gdi.Color.FromArgb(60, 0, 0, 0), thickness))
                g.DrawEllipse(track, r);

            if (status == PaceStatus.Unknown)
            {
                Arc(g, r, thickness, 0, used, baseColor);
            }
            else if (used <= pace)
            {
                Arc(g, r, thickness, 0, used, statusColor);
                Arc(g, r, thickness, used, pace, Gdi.Color.FromArgb(80, statusColor));
            }
            else
            {
                Arc(g, r, thickness, 0, pace, baseColor);
                Arc(g, r, thickness, pace, used, statusColor);
            }

            if (status != PaceStatus.Unknown)
            {
                var angle = (pace * 360 - 90) * Math.PI / 180;
                var cx = r.X + r.Width / 2f;
                var cy = r.Y + r.Height / 2f;
                var radius = r.Width / 2f;
                using var notch = new Gdi.Pen(darkTaskbar ? Gdi.Color.White : Gdi.Color.Black, Math.Max(1.5f, thickness * 0.35f));
                g.DrawLine(notch,
                    cx + (float)(Math.Cos(angle) * (radius - thickness * 0.8f)), cy + (float)(Math.Sin(angle) * (radius - thickness * 0.8f)),
                    cx + (float)(Math.Cos(angle) * (radius + thickness * 0.8f)), cy + (float)(Math.Sin(angle) * (radius + thickness * 0.8f)));
            }
        }
        return Gdi.Icon.FromHandle(bmp.GetHicon());
    }

    private static void Arc(Gdi.Graphics g, Gdi.RectangleF r, float thickness, double from, double to, Gdi.Color c)
    {
        var sweep = (float)((to - from) * 360);
        if (sweep <= 0.1f) return;
        using var pen = new Gdi.Pen(c, thickness);
        g.DrawArc(pen, r, (float)(from * 360 - 90), sweep);
    }

    private static Gdi.Color ToGdi(System.Windows.Media.Color c) => Gdi.Color.FromArgb(c.A, c.R, c.G, c.B);

    public static void DestroyIcon(Gdi.Icon? icon)
    {
        if (icon is null) return;
        var handle = icon.Handle;
        icon.Dispose();
        NativeDestroyIcon(handle);
    }

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static extern bool NativeDestroyIcon(IntPtr handle);
}
