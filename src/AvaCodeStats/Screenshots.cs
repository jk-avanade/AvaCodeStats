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

    /// <summary>A rendered shot and where its key parts are, in pixels of the image.</summary>
    private sealed record Shot(string File, Dictionary<string, double[]> Rects);

    private const double TourScale = 3;

    /// <summary>
    /// --tour &lt;folder&gt;: the shots of the showcase video, rendered at 3x so the video can zoom in, with a
    /// manifest (tour.json) of where each shot's key parts are (charts, the profile switch, the ring) so it
    /// can zoom to and highlight them. scripts/make-video.py cuts them into docs/tour.mp4 and docs/tour.gif.
    /// </summary>
    public static async Task TourAsync(AppHost host, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "*.png")) File.Delete(old);
            Prepare(host);
            var s = host.Settings;
            var shots = new Dictionary<string, Shot>();
            // A normal window, not the saved (maybe maximized) one.
            s.WindowMaximized = false;
            s.WindowWidth = 1280;
            s.WindowHeight = 800;

            async Task Take(Window w, string name, params (string Name, FrameworkElement? Element)[] parts)
            {
                await RenderAsync(w, Path.Combine(folder, $"{name}.png"), 800, TourScale);
                var root = (Visual)w.Content;
                var rects = new Dictionary<string, double[]>();
                foreach (var (part, e) in parts)
                {
                    if (e is not { IsVisible: true }) continue;
                    var r = e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
                    rects[part] = [r.X * TourScale, r.Y * TourScale, r.Width * TourScale, r.Height * TourScale];
                }
                shots[name] = new Shot($"{name}.png", rects);
            }
            void Profile(string profile)
            {
                s.ActiveProfile = profile;
                host.SaveSettings();
            }

            host.ShowDashboard("limits");
            var main = host.Dashboard!;
            main.WindowState = WindowState.Normal;
            main.Left = OffScreen;
            main.Top = OffScreen;
            main.Width = 1280;
            main.Height = 800;
            (string, FrameworkElement?)[] chrome =
                [("switch", main.ProfileSwitch), ("work", main.ProfileWork), ("personal", main.ProfilePersonal), ("all", main.ProfileUnified)];
            await Take(main, "limits", chrome);

            main.Navigate("pace");
            var pace = (BurnPage)main.CurrentPage!;
            pace.SetRange("30d");
            (string, FrameworkElement?)[] Overview(BurnPage page) =>
                [.. chrome, ("chart", page.OvChart), ("groups", page.OvGroups), ("hero", page.OvHero), ("kpis", page.OvKpis),
                 ("rows", page.OvRows), ("picker", page.Picker), ("range", page.RangeHost)];
            foreach (var (profile, name) in new[] { (Profiles.Work, "pace-work"), (Profiles.Personal, "pace-personal"), (Profiles.Unified, "pace-all") })
            {
                Profile(profile);
                pace.ShowScope("all");
                await Take(main, name, Overview(pace));
            }
            if (Find<ScrollViewer>(pace) is { } sv)
            {
                sv.ScrollToVerticalOffset(pace.OvRows.TransformToAncestor(sv).Transform(new Point()).Y - 60);
                sv.UpdateLayout();
                await Take(main, "pace-table", Overview(pace));
                sv.ScrollToTop();
            }

            main.Navigate("burn");
            var burn = (BurnPage)main.CurrentPage!;
            burn.SetRange("30d");
            foreach (var (profile, name) in new[] { (Profiles.Work, "burn-work"), (Profiles.Personal, "burn-personal"), (Profiles.Unified, "burn-all") })
            {
                Profile(profile);
                burn.ShowScope("all");
                await Take(main, name, Overview(burn));
            }
            if (host.Collector.PickTray(null, x => s.InActiveProfile(x.ProviderId, x.AccountId), s.Thresholds) is { } worst)
            {
                main.ShowBurn(worst.Key);
                await Take(main, "quota", [.. chrome, ("ring", burn.Ring), ("kpis", burn.Kpis), ("chart", burn.Chart)]);
            }

            main.Navigate("cost");
            await Take(main, "cost", chrome);
            main.Navigate("tokens");
            await Take(main, "tokens", chrome);
            main.Close();

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
            await Take(popup, "popup");

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
