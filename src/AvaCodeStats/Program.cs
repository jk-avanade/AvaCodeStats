namespace AvaCodeStats;

internal static class Program
{
    /// <summary>
    /// Runs in the tray. Options: --open [limits|burn|cost|tokens] opens the dashboard on start.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\AvaCodeStats.SingleInstance", out var first);
        if (!first) return;

        string? view = null;
        var i = Array.IndexOf(args, "--open");
        if (i >= 0) view = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : DashboardForm.LimitsView;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(view));
    }
}
