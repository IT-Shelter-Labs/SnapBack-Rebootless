namespace SnapBack.Core.Model;

/// <summary>Integrity level of a process token.</summary>
public enum IntegrityLevel
{
    Unknown = 0,
    Untrusted,
    Low,
    Medium,
    MediumPlus,
    High,
    System,
    Protected,
}

/// <summary>PE subsystem of an executable image.</summary>
public enum ImageSubsystem
{
    Unknown = 0,
    Gui,
    Console,
    Other,
}

/// <summary>
/// Everything SnapBack knows about one process at one moment.
/// </summary>
/// <remarks>
/// Nullable safety attributes use <c>null</c> for "could not be determined". The classifier treats
/// every unknown safety attribute as a reason to protect the process (fail closed).
/// </remarks>
public sealed record ProcessInfo
{
    public required ProcessIdentity Identity { get; init; }

    public int Pid => Identity.Pid;

    /// <summary>PID of the parent at creation time. Only meaningful together with creation times.</summary>
    public int ParentPid { get; init; }

    /// <summary>Executable file name as reported by the kernel, e.g. <c>chrome.exe</c>.</summary>
    public required string ImageName { get; init; }

    /// <summary>Full Win32 path of the executable, or <c>null</c> when it could not be queried.</summary>
    public string? ImagePath { get; init; }

    public int SessionId { get; init; }

    /// <summary>String SID of the token owner, or <c>null</c> when unknown.</summary>
    public string? OwnerSid { get; init; }

    /// <summary>Whether the process runs with an elevated (administrator) token.</summary>
    public bool? IsElevated { get; init; }

    public IntegrityLevel Integrity { get; init; } = IntegrityLevel.Unknown;

    /// <summary>Whether Windows marks the process as critical (terminating it bug-checks the system).</summary>
    public bool? IsCritical { get; init; }

    /// <summary>Whether the process is a protected / protected-light (PPL) process.</summary>
    public bool? IsProtectedProcess { get; init; }

    /// <summary>Whether the Service Control Manager reports a running service in this process.</summary>
    public bool IsServiceHost { get; init; }

    /// <summary>Whether a query handle to the process could be opened at all.</summary>
    public bool CouldOpen { get; init; }

    public ImageSubsystem Subsystem { get; init; }

    /// <summary>Package full name for packaged (MSIX / Store) applications.</summary>
    public string? PackageFullName { get; init; }

    public string? FileDescription { get; init; }

    public string? ProductName { get; init; }

    public string? CompanyName { get; init; }

    /// <summary>Private working set in bytes (what Task Manager shows as "Memory").</summary>
    public long PrivateWorkingSet { get; init; }

    /// <summary>Committed private bytes.</summary>
    public long PrivateBytes { get; init; }

    public long WorkingSet { get; init; }

    public WindowSummary Windows { get; init; } = WindowSummary.None;

    /// <summary>
    /// A short, human-readable hint derived from the command line (e.g. the script name a Python
    /// interpreter runs). Shown in the UI only; never persisted or logged.
    /// </summary>
    public string? CommandLineHint { get; init; }
}

/// <summary>Top-level windows owned by a process.</summary>
public sealed record WindowSummary
{
    public static readonly WindowSummary None = new();

    /// <summary>Visible, unowned, non-tool top-level windows (what Alt+Tab would show).</summary>
    public int VisibleMainWindows { get; init; }

    /// <summary>Top-level windows that exist but are hidden (typical for tray/background apps).</summary>
    public int HiddenTopLevelWindows { get; init; }

    /// <summary>Whether any main window is reported as not responding.</summary>
    public bool AnyHung { get; init; }

    /// <summary>Titles of the visible main windows. UI only; never persisted or logged.</summary>
    public IReadOnlyList<string> Titles { get; init; } = [];

    public bool HasAnyTopLevelWindow => VisibleMainWindows > 0 || HiddenTopLevelWindows > 0;
}
