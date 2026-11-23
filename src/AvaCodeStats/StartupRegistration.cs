using System.IO;
using Microsoft.Win32;

namespace AvaCodeStats;

/// <summary>"Start with Windows" via the per-user Run key.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "AvaCodeStats";

    /// <summary>Where scripts/install.ps1 puts the app. Only this copy registers itself, never a dev build.</summary>
    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AvaCodeStats");

    public static bool IsInstalledCopy =>
        Environment.ProcessPath is { } p
        && string.Equals(Path.GetDirectoryName(Path.GetFullPath(p))?.TrimEnd('\\'), InstallDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, Command);
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }

    /// <summary>
    /// Called on every start of the installed copy: registers it when the setting is on (the default),
    /// removes it when the user turned it off, and repairs a stale path.
    /// </summary>
    public static void Sync(bool wanted)
    {
        if (!IsInstalledCopy) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var current = key?.GetValue(Name) as string;
            if (wanted && current != Command) Set(true);
            else if (!wanted && current is not null) Set(false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            CrashLog.Write(e);
        }
    }
}
