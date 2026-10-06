using SnapBack.Core.Model;

namespace SnapBack.Core.Safety;

/// <summary>
/// Explicit, time-limited permission from the user to force-terminate specific process instances.
/// </summary>
/// <remarks>
/// Forced termination can lose unsaved data, so it is never implicit. The UI creates this object
/// only after the user confirmed a dialog listing the affected applications. The authorization
/// covers exact process identities (PID + creation time) and expires quickly, so it cannot be
/// replayed against a different process that happens to reuse a PID.
/// </remarks>
public sealed class ForceAuthorization
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly HashSet<ProcessIdentity> _approved;

    private ForceAuthorization(HashSet<ProcessIdentity> approved, DateTime grantedAtUtc)
    {
        _approved = approved;
        GrantedAtUtc = grantedAtUtc;
    }

    public DateTime GrantedAtUtc { get; }

    public IReadOnlyCollection<ProcessIdentity> ApprovedProcesses => _approved;

    /// <summary>Records that the user explicitly confirmed forced termination of these processes.</summary>
    public static ForceAuthorization FromUserConfirmation(IEnumerable<ProcessIdentity> approvedProcesses, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(approvedProcesses);
        return new ForceAuthorization(approvedProcesses.ToHashSet(), nowUtc);
    }

    public bool Covers(ProcessIdentity identity) => _approved.Contains(identity);

    public bool IsExpired(DateTime nowUtc) => nowUtc - GrantedAtUtc > Lifetime || nowUtc < GrantedAtUtc - TimeSpan.FromMinutes(1);
}
