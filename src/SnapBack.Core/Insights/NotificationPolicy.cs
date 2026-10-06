using SnapBack.Core.Localization;
using SnapBack.Core.Memory;
using SnapBack.Core.Text;

namespace SnapBack.Core.Insights;

public enum NotificationKind
{
    HighDrift,
    UnusualGrowth,
}

public sealed record NotificationRequest(NotificationKind Kind, string Title, string Text);

/// <summary>
/// Decides when a tray notification is worth showing. Deliberately conservative: notifications
/// are about applications, never about cached memory, and they are rate-limited.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>High drift: only when the drift is high, returning to the profile would reclaim at least
/// 1 GB, and either memory is getting scarce or the estimated impact is high. At most once every
/// six hours, and only again after the drift has dropped below high in between.</item>
/// <item>Unusual memory growth: once per application instance, at most once every three hours.</item>
/// <item>Never more than three notifications per day, and none while the SnapBack window is visible.</item>
/// </list>
/// </remarks>
public sealed class NotificationPolicy
{
    public static readonly TimeSpan DriftCooldown = TimeSpan.FromHours(6);
    public static readonly TimeSpan GrowthCooldown = TimeSpan.FromHours(3);
    public const int MaxPerDay = 3;
    public const long MinReclaimBytes = 1L << 30;

    private readonly object _gate = new();
    private readonly List<DateTime> _sent = [];
    private readonly HashSet<Model.ProcessIdentity> _growthNotified = [];
    private DateTime _lastDriftUtc = DateTime.MinValue;
    private DateTime _lastGrowthUtc = DateTime.MinValue;
    private bool _driftArmed = true;

    public NotificationRequest? Evaluate(
        SessionDrift drift,
        ImpactEstimate impact,
        IReadOnlyList<(MemorySeries Series, GrowthAssessment Growth)> unusualGrowth,
        bool enabled,
        bool windowVisible,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(drift);
        ArgumentNullException.ThrowIfNull(impact);
        ArgumentNullException.ThrowIfNull(unusualGrowth);

        lock (_gate)
        {
            if (drift.Level is DriftLevel.Low or DriftLevel.Medium)
            {
                _driftArmed = true;
            }

            if (!enabled || windowVisible)
            {
                return null;
            }

            _sent.RemoveAll(t => nowUtc - t > TimeSpan.FromDays(1));
            if (_sent.Count >= MaxPerDay)
            {
                return null;
            }

            if (_driftArmed && IsDriftWorthNotifying(drift, impact) && nowUtc - _lastDriftUtc >= DriftCooldown)
            {
                _driftArmed = false;
                _lastDriftUtc = nowUtc;
                _sent.Add(nowUtc);
                return new NotificationRequest(NotificationKind.HighDrift, Loc.Get("Notify_Drift_Title"),
                    Loc.Format("Notify_Drift_Text", drift.NewApps, ByteSize.Format(impact.EstimatedReclaimBytes)));
            }

            if (nowUtc - _lastGrowthUtc >= GrowthCooldown)
            {
                foreach (var (series, growth) in unusualGrowth)
                {
                    if (growth.Level != GrowthLevel.Unusual || !_growthNotified.Add(series.Instance))
                    {
                        continue;
                    }

                    _lastGrowthUtc = nowUtc;
                    _sent.Add(nowUtc);
                    return new NotificationRequest(NotificationKind.UnusualGrowth, Loc.Get("Notify_Growth_Title"),
                        Loc.Format("Notify_Growth_Text", series.DisplayName, ByteSize.Format(growth.FromBytes),
                            ByteSize.Format(growth.ToBytes), Math.Max(1, (int)Math.Round(growth.Span.TotalHours))));
                }
            }

            return null;
        }
    }

    public static bool IsDriftWorthNotifying(SessionDrift drift, ImpactEstimate impact) =>
        drift.Level == DriftLevel.High
        && impact.Level is ImpactLevel.Medium or ImpactLevel.High
        && impact.EstimatedReclaimBytes >= MinReclaimBytes
        && (impact.Pressure is MemoryPressure.Moderate or MemoryPressure.Low || impact.Level == ImpactLevel.High);
}
