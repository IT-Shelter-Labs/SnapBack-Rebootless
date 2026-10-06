using SnapBack.Windows;

namespace SnapBack.App.Services;

/// <summary>Command-line options of the GUI.</summary>
public sealed record StartupOptions
{
    public const string TrayArgument = "--tray";
    public const string QuitArgument = "--quit";

    /// <summary>Started by Windows at sign-in (<c>--autostart</c>, or <c>--auto-baseline</c> of version 0.2).</summary>
    public bool Autostart { get; init; }

    /// <summary>Start in the notification area without opening the window.</summary>
    public bool Tray { get; init; }

    /// <summary>Ask a running SnapBack to exit, then exit.</summary>
    public bool Quit { get; init; }

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        bool Has(string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);
        return new StartupOptions
        {
            Autostart = Has(AutostartRegistration.AutostartArgument) || Has(AutostartRegistration.LegacyAutoBaselineArgument),
            Tray = Has(TrayArgument),
            Quit = Has(QuitArgument),
        };
    }
}
