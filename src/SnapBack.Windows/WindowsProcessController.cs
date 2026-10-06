using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SnapBack.Core.Execution;
using SnapBack.Core.Model;
using SnapBack.Windows.Native;

namespace SnapBack.Windows;

/// <summary>
/// Acts on processes. Every method first obtains a <see cref="VerifiedProcess"/> and acts while
/// holding that handle, so the action can only reach the exact, verified process instance.
/// </summary>
public sealed unsafe class WindowsProcessController : IProcessController
{
    private static readonly object ConsoleGate = new();

    private readonly SessionContext _ctx;
    private readonly ILogger _log;

    public WindowsProcessController(SessionContext context, ILogger<WindowsProcessController> log)
    {
        _ctx = context;
        _log = log;
    }

    public ActionResult RequestCloseWindows(ProcessTarget target)
    {
        using var p = VerifiedProcess.Open(target, _ctx, 0, out var failure);
        if (p is null)
        {
            return failure!;
        }

        var windows = WindowEnumerator.Enumerate().Where(w => w.Pid == p.Pid && w.IsMainWindow).ToList();
        if (windows.Count == 0)
        {
            return new ActionResult(ActionStatus.NotApplicable, "The process has no visible windows.");
        }

        var posted = 0;
        var lastError = 0;
        foreach (var w in windows)
        {
            if (User32.PostMessage(w.Handle, User32.WM_CLOSE, 0, 0))
            {
                posted++;
            }
            else
            {
                lastError = Marshal.GetLastPInvokeError();
            }
        }

        if (posted > 0)
        {
            return new ActionResult(ActionStatus.Done, $"Close request sent to {posted} window(s).", posted);
        }

        return lastError == Kernel32.ERROR_ACCESS_DENIED
            ? new ActionResult(ActionStatus.AccessDenied, "Windows blocked the close request.")
            : new ActionResult(ActionStatus.Failed, $"The close request could not be sent (error {lastError}).");
    }

    public ActionResult InterruptConsole(ProcessTarget target, Func<int, string?, bool> isAllowedSharer)
    {
        ArgumentNullException.ThrowIfNull(isAllowedSharer);

        // Attaching to another console requires that SnapBack has none of its own (true for the GUI).
        if (Kernel32.GetConsoleWindow() != 0)
        {
            return new ActionResult(ActionStatus.NotApplicable, "Ctrl+C can only be sent from the SnapBack window, not from a console.");
        }

        using var p = VerifiedProcess.Open(target, _ctx, 0, out var failure);
        if (p is null)
        {
            return failure!;
        }

        lock (ConsoleGate)
        {
            if (!Kernel32.AttachConsole((uint)p.Pid))
            {
                var error = Marshal.GetLastPInvokeError();
                return error == Kernel32.ERROR_INVALID_HANDLE
                    ? new ActionResult(ActionStatus.NotApplicable, "The process has no console.")
                    : new ActionResult(ActionStatus.AccessDenied, $"Could not attach to its console (error {error}).");
            }

            try
            {
                foreach (var other in ConsoleProcesses())
                {
                    if (other == p.Pid || other == Environment.ProcessId)
                    {
                        continue;
                    }

                    var name = ProcessQueries.ImageNameOf((int)other);
                    if (!isAllowedSharer((int)other, name))
                    {
                        return new ActionResult(ActionStatus.SharedConsole, $"Its console is shared with {name ?? "PID " + other}, which is not being closed.");
                    }
                }

                // Make SnapBack itself ignore the Ctrl+C it is about to broadcast to this console.
                Kernel32.SetConsoleCtrlHandler(0, true);
                if (!Kernel32.GenerateConsoleCtrlEvent(Kernel32.CTRL_C_EVENT, 0))
                {
                    return new ActionResult(ActionStatus.Failed, $"Ctrl+C could not be sent (error {Marshal.GetLastPInvokeError()}).");
                }

                // Give the console host a moment to deliver the event before detaching.
                Thread.Sleep(100);
                return new ActionResult(ActionStatus.Done, "Ctrl+C sent.");
            }
            finally
            {
                Kernel32.FreeConsole();
            }
        }
    }

    private static uint[] ConsoleProcesses()
    {
        var capacity = 64u;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var list = new uint[capacity];
            uint count;
            fixed (uint* ptr = list)
            {
                count = Kernel32.GetConsoleProcessList(ptr, capacity);
            }

            if (count == 0)
            {
                throw new InvalidOperationException($"Could not list console processes (error {Marshal.GetLastPInvokeError()}).");
            }

            if (count <= capacity)
            {
                return list[..(int)count];
            }

            capacity = count + 16;
        }

        throw new InvalidOperationException("Could not list console processes.");
    }

    public Task<ActionResult> ShutdownGracefullyAsync(IReadOnlyList<ProcessTarget> targets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return Task.Factory.StartNew(
            () => RestartManagerShutdown(targets, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private ActionResult RestartManagerShutdown(IReadOnlyList<ProcessTarget> targets, CancellationToken ct)
    {
        var verified = new List<VerifiedProcess>();
        try
        {
            foreach (var t in targets)
            {
                var p = VerifiedProcess.Open(t, _ctx, 0, out var failure);
                if (p is null)
                {
                    if (failure!.Status is ActionStatus.Exited or ActionStatus.IdentityMismatch)
                    {
                        continue;
                    }

                    return failure;
                }

                verified.Add(p);
            }

            if (verified.Count == 0)
            {
                return new ActionResult(ActionStatus.Exited, "Nothing left to close.");
            }

            var key = stackalloc char[RestartManager.CCH_RM_SESSION_KEY + 1];
            var rc = RestartManager.RmStartSession(out var session, 0, key);
            if (rc != RestartManager.ERROR_SUCCESS)
            {
                return new ActionResult(ActionStatus.Failed, $"Restart Manager is unavailable (error {rc}).");
            }

            try
            {
                return ShutdownInSession(session, verified, ct);
            }
            finally
            {
                RestartManager.RmEndSession(session);
            }
        }
        finally
        {
            foreach (var p in verified)
            {
                p.Dispose();
            }
        }
    }

    private ActionResult ShutdownInSession(uint session, List<VerifiedProcess> verified, CancellationToken ct)
    {
        var apps = new RM_UNIQUE_PROCESS[verified.Count];
        for (var i = 0; i < verified.Count; i++)
        {
            var created = verified[i].Target.Identity.CreationTime;
            apps[i] = new RM_UNIQUE_PROCESS
            {
                dwProcessId = (uint)verified[i].Pid,
                ProcessStartTimeLow = (uint)(created & 0xFFFFFFFF),
                ProcessStartTimeHigh = (uint)(created >> 32),
            };
        }

        int rc;
        fixed (RM_UNIQUE_PROCESS* ptr = apps)
        {
            rc = RestartManager.RmRegisterResources(session, 0, 0, (uint)apps.Length, ptr, 0, 0);
        }

        if (rc != RestartManager.ERROR_SUCCESS)
        {
            return new ActionResult(ActionStatus.Failed, $"Restart Manager registration failed (error {rc}).");
        }

        // Ask Restart Manager how it sees these processes, and refuse anything that is not a plain
        // window application: services, consoles, Explorer and critical processes are never shut down.
        var problem = CheckEligibility(session, verified.Select(v => v.Pid).ToHashSet());
        if (problem is not null)
        {
            return problem;
        }

        if (ct.IsCancellationRequested)
        {
            return new ActionResult(ActionStatus.Cancelled, "Cancelled.");
        }

        using (ct.Register(() => RestartManager.RmCancelCurrentTask(session)))
        {
            // lActionFlags = 0: never RmForceShutdown. Apps may decline; they are never killed here.
            rc = RestartManager.RmShutdown(session, 0, 0);
        }

        _log.LogInformation("RmShutdown for {Pids} returned {Code}", string.Join(",", verified.Select(v => v.Pid)), rc);
        return rc switch
        {
            RestartManager.ERROR_SUCCESS => new ActionResult(ActionStatus.Done, "The app agreed to exit."),
            RestartManager.ERROR_FAIL_SHUTDOWN or RestartManager.ERROR_FAIL_NOACTION_REBOOT => new ActionResult(ActionStatus.Declined, "The app did not agree to exit."),
            RestartManager.ERROR_CANCELLED => new ActionResult(ActionStatus.Cancelled, "Cancelled."),
            _ => new ActionResult(ActionStatus.Failed, $"Restart Manager failed (error {rc})."),
        };
    }

    private ActionResult? CheckEligibility(uint session, HashSet<int> expectedPids)
    {
        uint count = 0;
        var rc = RestartManager.RmGetList(session, out var needed, ref count, null, out _);
        if (rc == RestartManager.ERROR_SUCCESS && needed == 0)
        {
            return new ActionResult(ActionStatus.NotApplicable, "Restart Manager found nothing it could close politely.");
        }

        if (rc != RestartManager.ERROR_MORE_DATA && rc != RestartManager.ERROR_SUCCESS)
        {
            return new ActionResult(ActionStatus.Failed, $"Restart Manager query failed (error {rc}).");
        }

        var infos = new RM_PROCESS_INFO[needed + 4];
        count = (uint)infos.Length;
        fixed (RM_PROCESS_INFO* ptr = infos)
        {
            rc = RestartManager.RmGetList(session, out needed, ref count, ptr, out _);
        }

        if (rc != RestartManager.ERROR_SUCCESS)
        {
            return new ActionResult(ActionStatus.Failed, $"Restart Manager query failed (error {rc}).");
        }

        for (var i = 0; i < count; i++)
        {
            var info = infos[i];
            var pid = (int)info.Process.dwProcessId;
            if (!expectedPids.Contains(pid))
            {
                return new ActionResult(ActionStatus.Refused, $"Restart Manager would also affect PID {pid}; nothing was done.");
            }

            if (info.TSSessionId != _ctx.SessionId)
            {
                return new ActionResult(ActionStatus.Refused, $"PID {pid} is in another session; nothing was done.");
            }

            if (info.ApplicationType is not (RestartManager.RmMainWindow or RestartManager.RmOtherWindow))
            {
                var kind = info.ApplicationType switch
                {
                    RestartManager.RmService => "a service",
                    RestartManager.RmExplorer => "Windows Explorer",
                    RestartManager.RmConsole => "a console program",
                    RestartManager.RmCritical => "a critical process",
                    _ => "not a window application",
                };
                return new ActionResult(ActionStatus.NotApplicable, $"Restart Manager reports PID {pid} as {kind}; it was not asked to exit.");
            }
        }

        return null;
    }

    public ProcessState GetState(ProcessIdentity identity)
    {
        var handle = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION | Kernel32.SYNCHRONIZE, false, (uint)identity.Pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return error == Kernel32.ERROR_INVALID_PARAMETER ? ProcessState.Exited : ProcessState.Unknown;
        }

        using (handle)
        {
            if (ProcessQueries.CreationTime(handle) is { } created && created != identity.CreationTime)
            {
                return ProcessState.Exited; // the original instance is gone; the PID was recycled
            }

            return ProcessQueries.HasExited(handle) ? ProcessState.Exited : ProcessState.Running;
        }
    }

    public IReadOnlyDictionary<int, WindowSummary> SnapshotWindows() => WindowEnumerator.SummarizeAll();

    public ActionResult Terminate(ProcessTarget target)
    {
        using var p = VerifiedProcess.Open(target, _ctx, Kernel32.PROCESS_TERMINATE, out var failure);
        if (p is null)
        {
            return failure!;
        }

        _log.LogWarning("Force-terminating {Name} (PID {Pid}) after user confirmation", target.ImageName, p.Pid);
        if (!Kernel32.TerminateProcess(p.Handle, 1))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == Kernel32.ERROR_ACCESS_DENIED
                ? new ActionResult(ActionStatus.AccessDenied, "Windows denied the termination.")
                : new ActionResult(ActionStatus.Failed, $"Termination failed (error {error}).");
        }

        var wait = Kernel32.WaitForSingleObject(p.Handle, 5000);
        return wait == Kernel32.WAIT_OBJECT_0
            ? new ActionResult(ActionStatus.Done, "Terminated.")
            : new ActionResult(ActionStatus.Failed, "The process did not exit after termination was requested.");
    }
}
