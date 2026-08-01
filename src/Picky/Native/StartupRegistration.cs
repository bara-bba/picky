using Microsoft.Win32;

namespace Picky.Native;

/// <summary>
/// "Start with Windows", via the per-user Run key
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>).
///
/// Per-user deliberately: HKLM or a scheduled task would need elevation, and Picky is a
/// user-session tray app. The registry is treated as the source of truth rather than the
/// settings file, since the user can remove the entry from Task Manager's Startup tab or
/// via Settings without Picky ever knowing.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Picky";

    /// <summary>The launch command written to the Run key. Quoted — the path contains spaces.</summary>
    private static string? LaunchCommand
    {
        get
        {
            var exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : $"\"{exe}\"";
        }
    }

    /// <summary>True when the Run key currently points at this executable.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            // An entry left behind by a copy that has since moved doesn't count as enabled.
            var expected = LaunchCommand;
            return expected is null
                || string.Equals(value.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Adds or removes the Run entry. Returns false if the registry rejected the change.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var command = LaunchCommand;
                if (command is null)
                {
                    return false;
                }

                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
