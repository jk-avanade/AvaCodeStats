using System.Windows;

namespace AvaCodeStats;

/// <summary>
/// Tray app. Options: --open [limits|burn|cost|tokens|settings] opens the dashboard on start;
/// --summary opens the tray summary popup.
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Local\AvaCodeStats.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);

        // A tray app should survive a bad redraw: log it and carry on instead of disappearing.
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => CrashLog.Write(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { CrashLog.Write(args.Exception); args.SetObserved(); };

        _host = new AppHost();
        StartupRegistration.Sync(_host.Settings.StartWithWindows);
        _host.Start();

        var args = e.Args;
        var i = Array.IndexOf(args, "--open");
        if (i >= 0) _host.ShowDashboard(i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null);
        if (args.Contains("--summary")) _host.ShowSummary();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Appends unhandled errors to %LOCALAPPDATA%\AvaCodeStats\errors.log.</summary>
internal static class CrashLog
{
    public static string Path => System.IO.Path.Combine(Core.AppSettings.DataDirectory, "errors.log");

    public static void Write(Exception? e)
    {
        if (e is null) return;
        try
        {
            System.IO.Directory.CreateDirectory(Core.AppSettings.DataDirectory);
            System.IO.File.AppendAllText(Path, $"[{DateTimeOffset.Now:O}] {e}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (System.IO.IOException) { }
    }
}
