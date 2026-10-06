using SnapBack.Core.Model;

namespace SnapBack.Core.Execution;

/// <summary>Reads the state of the system. Implemented by the Windows layer; faked in tests.</summary>
public interface ISystemProbe
{
    /// <summary>Takes a complete snapshot. Never throws because of a single inaccessible process.</summary>
    SystemSnapshot Capture();

    /// <summary>
    /// Fresh, complete information about one process instance, or <c>null</c> if that instance no
    /// longer exists (including when its PID now belongs to a different process).
    /// </summary>
    ProcessInfo? Inspect(ProcessIdentity identity, SessionContext context);

    MemoryStatus GetMemoryStatus();
}

/// <summary>A process SnapBack is about to act on, with the facts that must still hold.</summary>
public sealed record ProcessTarget(ProcessIdentity Identity, string ImageName, string? ExpectedImagePath)
{
    public static ProcessTarget From(ProcessInfo p) => new(p.Identity, p.ImageName, p.ImagePath);
}

public enum ProcessState
{
    Running,
    Exited,
    Unknown,
}

public enum ActionStatus
{
    /// <summary>The request was delivered.</summary>
    Done,

    /// <summary>The process is already gone.</summary>
    Exited,

    /// <summary>The PID now belongs to a different process, or the executable changed.</summary>
    IdentityMismatch,

    /// <summary>The low-level safety check refused (critical, protected, other user, elevated, ...).</summary>
    Refused,

    AccessDenied,

    /// <summary>Nothing to act on (no windows, no console, ...).</summary>
    NotApplicable,

    /// <summary>The process shares its console with programs that are not being closed.</summary>
    SharedConsole,

    /// <summary>The application declined (e.g. vetoed the Restart Manager request).</summary>
    Declined,

    Cancelled,

    Failed,
}

public sealed record ActionResult(ActionStatus Status, string Message, int Count = 0)
{
    public bool Succeeded => Status == ActionStatus.Done;
}

/// <summary>
/// Performs actions on processes. Every implementation must re-verify the target's identity
/// (PID + creation time + executable) on the very handle it acts through, and must refuse
/// critical, protected, elevated or foreign processes regardless of what the caller asks.
/// </summary>
public interface IProcessController
{
    /// <summary>Posts a close request (WM_CLOSE) to every visible main window of the process.</summary>
    ActionResult RequestCloseWindows(ProcessTarget target);

    /// <summary>
    /// Sends Ctrl+C to the console of a console program, but only if every other program attached
    /// to that console is accepted by <paramref name="isAllowedSharer"/> (called with the PID and,
    /// when known, the executable name of each other attached program).
    /// </summary>
    ActionResult InterruptConsole(ProcessTarget target, Func<int, string?, bool> isAllowedSharer);

    /// <summary>
    /// Asks processes to exit through Windows Restart Manager without forcing them. Only window
    /// applications are eligible; services, consoles, Explorer and critical processes are refused.
    /// </summary>
    Task<ActionResult> ShutdownGracefullyAsync(IReadOnlyList<ProcessTarget> targets, CancellationToken cancellationToken);

    ProcessState GetState(ProcessIdentity identity);

    /// <summary>Current window summary for every PID that owns top-level windows.</summary>
    IReadOnlyDictionary<int, WindowSummary> SnapshotWindows();

    /// <summary>Forcibly terminates the process. Only ever called after explicit user authorization.</summary>
    ActionResult Terminate(ProcessTarget target);
}
