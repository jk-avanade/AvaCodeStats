using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvaCodeStats.Core;
using AvaCodeStats.Views;

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
            // Shows folding in the popup: the subscription with the most quotas is folded.
            Prepare(host);

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

    /// <summary>Puts the recorded data in the same state as the screenshots: every profile, nothing folded.</summary>
    private static void Prepare(AppHost host)
    {
        var s = host.Settings;
        s.ActiveProfile = Profiles.Unified;
        s.TrayProfile = Profiles.Unified;
        s.CollapsedGroups.Clear();
        s.CollapsedTrayGroups = host.Collector.Latest.Values
            .GroupBy(x => $"{x.ProviderId}:{x.AccountId}")
            .OrderByDescending(g => g.Count()).Take(1).Select(g => g.Key).ToList();
        host.Collector.UseHistoryAsLastCollection();
    }

    private sealed record Shot(string File, double Seconds, bool Fade, string? Caption);

    private const double TourScale = 1.25;

    /// <summary>
    /// --tour &lt;folder&gt;: frames of a short walk through the dashboard at a normal window size, with a
    /// manifest (tour.json) of how long to hold each one, where to cross-fade and what caption to show.
    /// scripts/make-video.py turns them into an MP4 and a GIF.
    /// </summary>
    public static async Task TourAsync(AppHost host, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "*.png")) File.Delete(old);
            Prepare(host);
            var s = host.Settings;
            var shots = new List<Shot>();
            var n = 0;
            async Task Frame(Window w, double seconds, bool fade, string? caption, int settle = 800)
            {
                var file = $"{++n:0000}.png";
                await RenderAsync(w, Path.Combine(folder, file), settle, TourScale);
                shots.Add(new Shot(file, seconds, fade, caption));
            }
            // Eases the page down to the end and holds there.
            async Task Scroll(Window w, string? caption, double hold)
            {
                if (Find<ScrollViewer>((DependencyObject)w.Content) is not { ScrollableHeight: > 0 } sv) return;
                const int steps = 28;
                for (var i = 1; i <= steps; i++)
                {
                    var t = (double)i / steps;
                    sv.ScrollToVerticalOffset(sv.ScrollableHeight * (t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2));
                    sv.UpdateLayout();
                    await Frame(w, i == steps ? hold : 1.0 / 30, false, caption, settle: 0);
                }
            }

            host.ShowDashboard("limits");
            var main = host.Dashboard!;
            main.WindowState = WindowState.Normal;
            main.Left = OffScreen;
            main.Top = OffScreen;
            main.Width = 1280;
            main.Height = 800;
            const string limits = "Limits: every quota, grouped by subscription";
            await Frame(main, 3.0, true, limits);
            await Scroll(main, limits, 1.5);

            main.Navigate("burn");
            var burn = (BurnPage)main.CurrentPage!;
            burn.ShowScope("all");
            burn.SetRange("30d");
            const string all = "Burn: every quota at once";
            await Frame(main, 3.5, true, all);
            await Scroll(main, all, 2.0);

            s.ActiveProfile = Profiles.Work;
            host.SaveSettings();
            burn.ShowScope("all");
            await Frame(main, 3.0, true, "Or only the Work subscriptions");

            s.ActiveProfile = Profiles.Unified;
            host.SaveSettings();
            if (host.Collector.PickTray(null, x => s.InActiveProfile(x.ProviderId, x.AccountId), s.Thresholds) is { } worst)
            {
                main.ShowBurn(worst.Key);
                await Frame(main, 3.5, true, "One quota against an even pace");
            }

            main.Navigate("pace");
            var pace = (BurnPage)main.CurrentPage!;
            pace.ShowScope("all");
            pace.SetRange("30d");
            await Frame(main, 4.0, true, "Pace: over (+) or under (\u2212) an even pace");
            pace.SetRange("24h");
            const string range = "Any range from 24 hours to 90 days";
            await Frame(main, 2.5, true, range);
            await Scroll(main, range, 2.0);

            main.Navigate("cost");
            await Frame(main, 3.0, true, "Cost: an API-price estimate from local session logs");
            main.Navigate("tokens");
            await Frame(main, 3.0, true, "Tokens per model, tool and day");
            main.Close();

            // The tray popup, laid over the last frame by make-video.py.
            host.ShowSummary();
            var popup = host.Summary!;
            AllowTallerThanScreen(popup);
            popup.MaxHeight = double.PositiveInfinity;
            popup.SizeToContent = SizeToContent.Manual;
            var content = (FrameworkElement)popup.Content;
            content.Measure(new Size(popup.ActualWidth, double.PositiveInfinity));
            popup.Height = content.DesiredSize.Height;
            popup.Left = OffScreen;
            popup.Top = OffScreen;
            await RenderAsync(popup, Path.Combine(folder, "popup.png"), 800, TourScale);
            shots.Add(new Shot("popup.png", 4.0, true, "The tray popup: every subscription at a glance"));

            File.WriteAllText(Path.Combine(folder, "tour.json"), JsonSerializer.Serialize(shots));
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

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private static async Task RenderAsync(Window window, string path, int settle = 800, double scale = Scale)
    {
        Mouse.Synchronize(); // drop the hover state from where the window opened
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (settle > 0)
        {
            await Task.Delay(settle); // charts lay out and draw after the page loads
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        // The client area on the window's solid base colour (Mica is drawn by Windows, not WPF).
        var root = (FrameworkElement)window.Content;
        var size = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.Black, null, size);
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, Viewbox = size, ViewboxUnits = BrushMappingMode.Absolute }, null, size);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
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
