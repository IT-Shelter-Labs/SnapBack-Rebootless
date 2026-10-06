using Microsoft.Win32;

namespace SnapBack.Windows;

/// <summary>
/// "Start SnapBack with Windows", via the per-user Run key: the standard, user-visible mechanism
/// (Settings → Apps → Startup, Task Manager → Startup apps). No administrator rights, no scheduled
/// task, no service. This is SnapBack's only registry write, and it happens only when the user
/// enables the setting.
/// </summary>
public static class AutostartRegistration
{
    public const string AutostartArgument = "--autostart";

    /// <summary>Argument written by version 0.2 (headless automatic baseline); still understood.</summary>
    public const string LegacyAutoBaselineArgument = "--auto-baseline";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "SnapBack";

    public static string CommandFor(string executablePath) => $"\"{executablePath}\" {AutostartArgument}";

    /// <summary>The registered command, or <c>null</c>.</summary>
    public static string? RegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public static bool IsEnabled(string executablePath) =>
        string.Equals(RegisteredCommand(), CommandFor(executablePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the user turned SnapBack off in Windows' Startup apps settings (those settings
    /// override the Run entry; SnapBack never changes them).
    /// </summary>
    public static bool IsDisabledInWindowsSettings()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: false);
        return key?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
    }

    public static void Enable(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, CommandFor(executablePath), RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Makes the Run entry match the setting: writes it (or fixes an outdated path or argument)
    /// when enabled, removes it when disabled. Returns whether something changed.
    /// </summary>
    public static bool Sync(bool enabled, string executablePath)
    {
        var current = RegisteredCommand();
        if (enabled)
        {
            if (string.Equals(current, CommandFor(executablePath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Enable(executablePath);
            return true;
        }

        if (current is null)
        {
            return false;
        }

        Disable();
        return true;
    }
}
