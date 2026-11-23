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

        _host = new AppHost();
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
