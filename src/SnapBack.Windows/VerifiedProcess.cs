using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SnapBack.Core.Classification;
using SnapBack.Core.Execution;
using SnapBack.Core.Model;
using SnapBack.Core.Text;
using SnapBack.Windows.Native;

namespace SnapBack.Windows;

/// <summary>
/// A process handle whose identity and safety have been verified <em>on that handle</em>.
/// </summary>
/// <remarks>
/// This is the lowest-level safety net, independent of the planner, the UI and the core guard.
/// While the handle is open, Windows cannot reuse the PID, so the action that follows is guaranteed
/// to hit exactly the process that was verified. Refused: PID 0/4, SnapBack itself, critical and
/// protected processes, other sessions, other users, elevated processes, and anything whose
/// creation time or executable path does not match what the user saw.
/// </remarks>
internal sealed class VerifiedProcess : IDisposable
{
    private VerifiedProcess(SafeProcessHandle handle, ProcessTarget target)
    {
        Handle = handle;
        Target = target;
    }

    public SafeProcessHandle Handle { get; }

    public ProcessTarget Target { get; }

    public int Pid => Target.Identity.Pid;

    public void Dispose() => Handle.Dispose();

    public static VerifiedProcess? Open(ProcessTarget target, SessionContext ctx, uint extraAccess, out ActionResult? failure)
    {
        var pid = target.Identity.Pid;
        if (pid is 0 or 4 || pid == ctx.SelfPid || pid == Environment.ProcessId)
        {
            failure = new ActionResult(ActionStatus.Refused, "This process is never touched.");
            return null;
        }

        var access = extraAccess | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION | Kernel32.SYNCHRONIZE;
        var handle = Kernel32.OpenProcess(access, false, (uint)pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            failure = error switch
            {
                Kernel32.ERROR_INVALID_PARAMETER => new ActionResult(ActionStatus.Exited, "The process has exited."),
                Kernel32.ERROR_ACCESS_DENIED => new ActionResult(ActionStatus.AccessDenied, "Access denied."),
                _ => new ActionResult(ActionStatus.Failed, $"Could not open the process (error {error})."),
            };
            return null;
        }

        var problem = Verify(handle, target, ctx);
        if (problem is not null)
        {
            handle.Dispose();
            failure = problem;
            return null;
        }

        failure = null;
        return new VerifiedProcess(handle, target);
    }

    private static ActionResult? Verify(SafeProcessHandle h, ProcessTarget target, SessionContext ctx)
    {
        if (ProcessQueries.CreationTime(h) is not { } created || created != target.Identity.CreationTime)
        {
            return new ActionResult(ActionStatus.IdentityMismatch, "The PID now belongs to a different process.");
        }

        if (ProcessQueries.HasExited(h))
        {
            return new ActionResult(ActionStatus.Exited, "The process has exited.");
        }

        var path = ProcessQueries.ImagePath(h);
        if (path is null)
        {
            return new ActionResult(ActionStatus.Refused, "The executable could not be verified.");
        }

        if (target.ExpectedImagePath is not null && !PathUtil.PathEquals(path, target.ExpectedImagePath))
        {
            return new ActionResult(ActionStatus.IdentityMismatch, "The executable does not match.");
        }

        var fileName = Path.GetFileName(path);
        if (KnownProcesses.ShellAndSessionComponents.Contains(fileName)
            || (PathUtil.IsUnder(path, ctx.WindowsDirectory) && !KnownProcesses.WindowsUserTools.Contains(fileName)))
        {
            return new ActionResult(ActionStatus.Refused, "Windows components are never touched.");
        }

        if (ProcessQueries.IsCritical(h) != false)
        {
            return new ActionResult(ActionStatus.Refused, "Critical (or unverifiable) process.");
        }

        if (ProcessQueries.IsProtected(h) != false)
        {
            return new ActionResult(ActionStatus.Refused, "Protected (or unverifiable) process.");
        }

        if (ProcessQueries.SessionOf(target.Identity.Pid) != ctx.SessionId)
        {
            return new ActionResult(ActionStatus.Refused, "The process belongs to another session.");
        }

        var token = ProcessQueries.Token(h);
        if (!string.Equals(token.OwnerSid, ctx.UserSid, StringComparison.OrdinalIgnoreCase))
        {
            return new ActionResult(ActionStatus.Refused, "The process belongs to another (or unknown) user.");
        }

        if (token.IsElevated != false || token.Integrity >= IntegrityLevel.High)
        {
            return new ActionResult(ActionStatus.Refused, "The process runs with administrator rights.");
        }

        return null;
    }
}
