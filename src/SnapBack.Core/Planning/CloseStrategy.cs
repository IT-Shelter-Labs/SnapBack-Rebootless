using SnapBack.Core.Classification;
using SnapBack.Core.Grouping;
using SnapBack.Core.Localization;
using SnapBack.Core.Model;

namespace SnapBack.Core.Planning;

public enum StepKind
{
    /// <summary>Post a close request (WM_CLOSE) to the process's visible windows - like clicking X.</summary>
    CloseWindows,

    /// <summary>Send Ctrl+C to the process's console - like pressing Ctrl+C in its terminal.</summary>
    ConsoleInterrupt,

    /// <summary>Ask the app to exit via Windows Restart Manager (used by installers; never forced).</summary>
    RestartManager,

    /// <summary>No direct action: the process is expected to exit together with its application.</summary>
    ExitsWithApp,

    /// <summary>There is no polite way to close the process; it will be reported, not killed.</summary>
    NoGracefulMethod,

    /// <summary>The process is protected and is not touched at all.</summary>
    LeftAlone,
}

public sealed record PlannedStep(ProcessIdentity Target, string ImageName, StepKind Kind, string Description);

/// <summary>
/// Decides how an application is asked to close. The same rules drive both the preview and the
/// executor, so the preview shows exactly what will happen.
/// </summary>
/// <remarks>
/// Order of preference: (1) close the visible windows, the way a user would; (2) Ctrl+C for console
/// programs; (3) Windows Restart Manager for apps that live in the tray / background. Forced
/// termination is never part of a plan: it requires a separate, explicit user decision afterwards.
/// </remarks>
public static class CloseStrategy
{
    public static bool ShouldCloseWindows(ProcessInfo member) => member.Windows.VisibleMainWindows > 0;

    public static bool ShouldInterruptConsole(AppGroup group, ProcessInfo member) =>
        member.Pid == group.Root.Pid && member.Subsystem == ImageSubsystem.Console;

    public static bool IsRestartManagerCandidate(ProcessInfo member) => member.Windows.HasAnyTopLevelWindow;

    public static IReadOnlyList<PlannedStep> Plan(AppGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var steps = new List<PlannedStep>();
        var actionable = group.ActionableMembers.ToList();

        var closeWindows = actionable.Where(ShouldCloseWindows).ToList();
        var console = actionable.Where(m => ShouldInterruptConsole(group, m)).ToList();
        var directlyAsked = new HashSet<int>();

        foreach (var m in closeWindows)
        {
            steps.Add(Step(m, StepKind.CloseWindows, Loc.Format("Step_CloseWindows", m.ImageName, m.Pid, m.Windows.VisibleMainWindows)));
            directlyAsked.Add(m.Pid);
        }

        foreach (var m in console)
        {
            steps.Add(Step(m, StepKind.ConsoleInterrupt, Loc.Format("Step_ConsoleInterrupt", m.ImageName, m.Pid)));
            directlyAsked.Add(m.Pid);
        }

        if (directlyAsked.Count == 0)
        {
            var rm = actionable.Where(IsRestartManagerCandidate).ToList();
            foreach (var m in rm)
            {
                steps.Add(Step(m, StepKind.RestartManager, Loc.Format("Step_RestartManager", m.ImageName, m.Pid)));
                directlyAsked.Add(m.Pid);
            }

            if (rm.Count == 0 && actionable.Any(m => m.Pid == group.Root.Pid))
            {
                var r = group.Root;
                steps.Add(Step(r, StepKind.NoGracefulMethod, Loc.Format("Step_NoGracefulMethod", r.ImageName, r.Pid)));
                directlyAsked.Add(r.Pid);
            }
        }

        foreach (var m in actionable.Where(m => !directlyAsked.Contains(m.Pid)))
        {
            steps.Add(Step(m, StepKind.ExitsWithApp, Loc.Format("Step_ExitsWithApp", m.ImageName, m.Pid)));
        }

        foreach (var m in group.ProtectedMembers)
        {
            var reason = group.MemberAssessments.TryGetValue(m.Pid, out var a) && a.PrimaryReason is { } pr
                ? RiskReasons.Label(pr)
                : Loc.Get("Step_ProtectedFallback");
            steps.Add(Step(m, StepKind.LeftAlone, Loc.Format("Step_LeftAlone", m.ImageName, m.Pid, reason)));
        }

        return steps;
    }

    /// <summary>Group-level notes shown in the preview.</summary>
    public static IReadOnlyList<string> Notes(AppGroup group)
    {
        var notes = new List<string>();
        var actionable = group.ActionableMembers.ToList();
        if (actionable.Any(ShouldCloseWindows))
        {
            notes.Add(Loc.Get("Note_TrayRetry"));
            notes.Add(Loc.Get("Note_WaitsForUser"));
        }

        notes.Add(Loc.Get("Note_NoForce"));
        return notes;
    }

    private static PlannedStep Step(ProcessInfo p, StepKind kind, string description) =>
        new(p.Identity, p.ImageName, kind, description);
}
