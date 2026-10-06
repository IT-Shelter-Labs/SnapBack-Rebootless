using Microsoft.Win32;

namespace SnapBack.Windows;

/// <summary>
/// Optional "create a baseline after sign-in" autostart. Uses the per-user Run key, which is the
/// standard, user-visible mechanism (Task Manager → Startup apps). This is SnapBack's only
/// registry write, and it happens only when the user enables the setting.
/// </summary>
public static class AutostartRegistration
{
    public const string AutoBaselineArgument = "--auto-baseline";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SnapBack";

    public static string CommandFor(string executablePath) => $"\"{executablePath}\" {AutoBaselineArgument}";

    public static bool IsEnabled(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value
            && string.Equals(value, CommandFor(executablePath), StringComparison.OrdinalIgnoreCase);
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
}
