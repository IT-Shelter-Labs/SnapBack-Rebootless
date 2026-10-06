namespace SnapBack.Windows.Tests;

/// <summary>A fact that only runs on Windows (integration tests against the real OS).</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
    }
}
