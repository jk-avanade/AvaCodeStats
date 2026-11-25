using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvaCodeStats.Core;

namespace AvaCodeStats;

/// <summary>
/// --screenshots &lt;folder&gt;: renders each dashboard page and the tray popup from the recorded data to PNGs
/// and exits. Account logins are redacted (<see cref="Privacy"/>), nothing is polled, and settings change in
/// memory only, so it can run next to the installed copy. Windows are rendered by WPF itself, off screen:
/// no notifications, other windows, tooltips or mouse hover end up in the images.
/// </summary>
internal static class Screenshots
{
    private const double Scale = 1.5;
    private const double OffScreen = -30000;

    /// <summary>Page and the dashboard height (DIPs) that shows it without cutting a card in half.</summary>
    private static readonly (string Page, double Height)[] Pages =
        [("limits", 1274), ("burn", 1500), ("pace", 1500), ("cost", 1180), ("tokens", 1180), ("settings", 1170)];

    public static async Task RunAsync(AppHost host, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var s = host.Settings;
            s.ActiveProfile = Profiles.Unified;
            s.TrayProfile = Profiles.Unified;
            s.CollapsedGroups.Clear();
            // Show folding in the popup: fold the subscription with the most quotas.
            s.CollapsedTrayGroups = host.Collector.Latest.Values
                .GroupBy(x => $"{x.ProviderId}:{x.AccountId}")
                .OrderByDescending(g => g.Count()).Take(1).Select(g => g.Key).ToList();
            host.Collector.UseHistoryAsLastCollection();

            host.ShowDashboard("limits");
            var main = host.Dashboard!;
            AllowTallerThanScreen(main);
            main.WindowState = WindowState.Normal;
            main.Left = OffScreen;
            main.Top = OffScreen;
            main.Width = 1280;
            foreach (var (page, height) in Pages)
            {
                main.Height = height;
                main.Navigate(page);
                await RenderAsync(main, Path.Combine(folder, $"{page}.png"));
            }
            // Burn opens on the overview; also show one quota in detail (the one most over pace).
            if (host.Collector.PickTray(null, x => s.InActiveProfile(x.ProviderId, x.AccountId), s.Thresholds) is { } worst)
            {
                main.Height = 980;
                main.ShowBurn(worst.Key);
                await RenderAsync(main, Path.Combine(folder, "burn-quota.png"));
            }
            main.Close();

            host.ShowSummary();
            var popup = host.Summary!;
            AllowTallerThanScreen(popup);
            // The whole list, no scrolling: size the window to its content's natural height.
            popup.MaxHeight = double.PositiveInfinity;
            popup.SizeToContent = SizeToContent.Manual;
            var content = (FrameworkElement)popup.Content;
            content.Measure(new Size(popup.ActualWidth, double.PositiveInfinity));
            popup.Height = content.DesiredSize.Height;
            popup.Left = OffScreen;
            popup.Top = OffScreen;
            await RenderAsync(popup, Path.Combine(folder, "popup.png"));
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static async Task RenderAsync(Window window, string path)
    {
        Mouse.Synchronize(); // drop the hover state from where the window opened
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(800); // charts lay out and draw after the page loads
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        // The client area on the window's solid base colour (Mica is drawn by Windows, not WPF).
        var root = (FrameworkElement)window.Content;
        var size = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.Black, null, size);
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, Viewbox = size, ViewboxUnits = BrushMappingMode.Absolute }, null, size);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }

    /// <summary>Windows caps a window at the screen size; lift that so a page renders at full height.</summary>
    private static void AllowTallerThanScreen(Window window)
    {
        HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.AddHook((IntPtr _, int msg, IntPtr _, IntPtr lParam, ref bool _) =>
        {
            const int WmGetMinMaxInfo = 0x0024;
            if (msg == WmGetMinMaxInfo)
            {
                var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                info.MaxTrackSize = new Point32 { X = 32000, Y = 32000 };
                Marshal.StructureToPtr(info, lParam, false);
            }
            return IntPtr.Zero;
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public Point32 Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
}
