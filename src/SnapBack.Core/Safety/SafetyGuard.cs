using SnapBack.Core.Classification;
using SnapBack.Core.Localization;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
using SnapBack.Core.Text;

namespace SnapBack.Core.Safety;

public enum GuardVerdict
{
    Allowed,
    ProcessExited,
    IdentityMismatch,
    PathChanged,
    ProtectedNow,
    ItemNotEligible,
    NotAuthorized,
    AuthorizationExpired,
    GracefulNotAttempted,
}

public sealed record GuardDecision(GuardVerdict Verdict, string Message)
{
    public bool Allowed => Verdict == GuardVerdict.Allowed;

    public static GuardDecision Allow => new(GuardVerdict.Allowed, Loc.Get("Guard_Allowed"));
}

/// <summary>
/// The last line of defence before any action. It re-checks the <em>live</em> state of a process
/// right before SnapBack touches it, independently of what the planner or the UI decided.
/// </summary>
/// <remarks>
/// Even if the UI were buggy and passed a protected item, or a process changed between analysis and
/// action (exited, PID recycled, elevated, ...), the guard refuses. The Windows layer additionally
/// re-verifies identity on the process handle it acts through.
/// </remarks>
public sealed class SafetyGuard
{
    private readonly ProcessClassifier _classifier;

    public SafetyGuard(ProcessClassifier classifier)
    {
        _classifier = classifier;
    }

    /// <summary>May SnapBack ask this process to close (close request, Ctrl+C, Restart Manager)?</summary>
    public GuardDecision CheckGraceful(PlanItem item, ProcessInfo planned, ProcessInfo? live, SessionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!item.IsSelectable)
        {
            return new(GuardVerdict.ItemNotEligible, Loc.Format("Op_NotEligible", item.Group.DisplayName));
        }

        if (!item.Group.Contains(planned.Identity) || item.Group.IsMemberProtected(planned.Pid))
        {
            return new(GuardVerdict.ItemNotEligible, Loc.Format("Guard_NotPartOfItem", planned.ImageName, planned.Pid, item.Group.DisplayName));
        }

        // Hard-coded checks that do not depend on any classification data.
        if (planned.Pid is 0 or 4 || planned.Pid == ctx.SelfPid)
        {
            return new(GuardVerdict.ProtectedNow, Loc.Format("Guard_NeverTouched", planned.Pid));
        }

        if (live is null)
        {
            return new(GuardVerdict.ProcessExited, Loc.Format("Guard_Exited", planned.ImageName, planned.Pid));
        }

        if (live.Identity != planned.Identity)
        {
            return new(GuardVerdict.IdentityMismatch, Loc.Format("Guard_PidReused", planned.Pid));
        }

        if (planned.ImagePath is not null && !PathUtil.PathEquals(planned.ImagePath, live.ImagePath))
        {
            return new(GuardVerdict.PathChanged, Loc.Format("Guard_PathChanged", planned.Pid, planned.ImageName));
        }

        var now = _classifier.Classify(live, ctx);
        if (now.IsProtected)
        {
            var why = string.Join(", ", now.Reasons.Where(r => RiskReasons.LevelOf(r) == SafetyLevel.Protected).Select(RiskReasons.Label));
            return new(GuardVerdict.ProtectedNow, Loc.Format("Guard_ProtectedNow", live.ImageName, live.Pid, why));
        }

        return GuardDecision.Allow;
    }

    /// <summary>May SnapBack force-terminate this process?</summary>
    public GuardDecision CheckForce(PlanItem item, ProcessInfo planned, ProcessInfo? live, SessionContext ctx,
        ForceAuthorization? authorization, bool gracefulAttempted, DateTime nowUtc)
    {
        if (authorization is null || !authorization.Covers(planned.Identity))
        {
            return new(GuardVerdict.NotAuthorized, Loc.Format("Guard_NotAuthorized", planned.ImageName, planned.Pid));
        }

        if (authorization.IsExpired(nowUtc))
        {
            return new(GuardVerdict.AuthorizationExpired, Loc.Get("Guard_AuthExpired"));
        }

        if (!gracefulAttempted)
        {
            return new(GuardVerdict.GracefulNotAttempted, Loc.Format("Guard_GracefulFirst", planned.ImageName, planned.Pid));
        }

        return CheckGraceful(item, planned, live, ctx);
    }
}
