using SnapBack.Core.Grouping;
using SnapBack.Core.Model;

namespace SnapBack.Core.Startup;

public enum SettleState
{
    Waiting,
    Settled,
    TimedOut,
}

/// <summary>
/// Decides when startup has finished, so the automatic baseline is not taken while autostart
/// programs are still launching.
/// </summary>
/// <remarks>
/// Startup counts as settled when (1) at least <c>minSinceLogon</c> has passed since sign-in,
/// (2) the set of running applications has not changed for <c>quietPeriod</c>, and (3) the CPU has
/// not been busy (average below <see cref="BusyThreshold"/>) during that quiet period — autostart
/// programs often keep loading for a while after their process appears. If that never happens,
/// the detector gives up after <c>maxWait</c> (the caller then takes the baseline anyway).
/// </remarks>
public sealed class StartupSettleDetector
{
    /// <summary>System-wide CPU usage above which startup is considered still busy.</summary>
    public const double BusyThreshold = 0.5;

    private readonly TimeSpan _minSinceLogon;
    private readonly TimeSpan _quietPeriod;
    private readonly DateTime _logonUtc;
    private readonly DateTime _giveUpAtUtc;
    private HashSet<string>? _lastKeys;
    private DateTime _lastChangeUtc;

    public StartupSettleDetector(TimeSpan minSinceLogon, TimeSpan quietPeriod, TimeSpan maxWait, DateTime logonUtc, DateTime startedUtc)
    {
        _minSinceLogon = minSinceLogon;
        _quietPeriod = quietPeriod;
        _logonUtc = logonUtc;
        _giveUpAtUtc = startedUtc + maxWait;
        _lastChangeUtc = startedUtc;
    }

    /// <param name="nowUtc">Current time.</param>
    /// <param name="runningAppKeys">Running user applications.</param>
    /// <param name="cpuBusy">System CPU usage since the previous observation (0–1), when known.</param>
    public SettleState Observe(DateTime nowUtc, IEnumerable<string> runningAppKeys, double? cpuBusy = null)
    {
        var keys = runningAppKeys.ToHashSet(StringComparer.Ordinal);
        if (_lastKeys is null || !_lastKeys.SetEquals(keys) || cpuBusy >= BusyThreshold)
        {
            // A busy CPU restarts the quiet period just like a newly started app.
            _lastKeys = keys;
            _lastChangeUtc = nowUtc;
        }

        if (nowUtc - _logonUtc >= _minSinceLogon && nowUtc - _lastChangeUtc >= _quietPeriod)
        {
            return SettleState.Settled;
        }

        return nowUtc >= _giveUpAtUtc ? SettleState.TimedOut : SettleState.Waiting;
    }

    /// <summary>App keys used for settle detection: user applications only.</summary>
    public static IEnumerable<string> RelevantKeys(IEnumerable<AppGroup> groups) =>
        groups.Where(g => !g.IsProtected).Select(g => g.AppKey);

    /// <summary>
    /// Estimates when the user signed in: the creation time of the oldest process owned by the user
    /// in this session (typically userinit/sihost/explorer).
    /// </summary>
    public static DateTime EstimateLogonUtc(SystemSnapshot snapshot)
    {
        var ctx = snapshot.Context;
        var times = snapshot.SessionProcesses
            .Where(p => string.Equals(p.OwnerSid, ctx.UserSid, StringComparison.OrdinalIgnoreCase) && p.Identity.CreationTime > 0)
            .Select(p => p.Identity.CreationTimeUtc)
            .ToList();
        return times.Count > 0 ? times.Min() : ctx.BootTimeUtc;
    }
}
