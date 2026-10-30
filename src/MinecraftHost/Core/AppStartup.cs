using Microsoft.Win32;

namespace MinecraftHost.Core;

/// <summary>
/// "Open when Windows starts": a value under the current user's Run key (no admin rights needed).
/// Windows starts the app with <see cref="Flag"/>, so it can open minimized.
/// </summary>
public static class AppStartup
{
    public const string Flag = "--startup";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Minecraft Self-Hoster";
    const string OldValueName = "Minecraft Server Host";   // before the rename: moved over by RefreshPath

    static string Command => $"\"{Environment.ProcessPath}\" {Flag}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// If the exe Windows would start no longer exists (the app was moved), point it at this one.
    /// Another copy that still exists keeps the entry.
    /// </summary>
    public static void RefreshPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(OldValueName) is string old)
            {
                if (key.GetValue(ValueName) == null) key.SetValue(ValueName, old);
                key.DeleteValue(OldValueName, throwOnMissingValue: false);
            }
            if (key?.GetValue(ValueName) is not string current) return;
            var exe = current.StartsWith('"') ? current[1..current.IndexOf('"', 1)] : current.Split(' ')[0];
            if (!File.Exists(exe)) key.SetValue(ValueName, Command);
        }
        catch { }
    }

    /// <summary>Whether the entry starts this copy of the app (and not another one).</summary>
    public static bool IsThisCopy
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string current &&
                   current.Contains(Environment.ProcessPath ?? "?", StringComparison.OrdinalIgnoreCase);
        }
    }
}
