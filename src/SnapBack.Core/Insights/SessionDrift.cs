using SnapBack.Core.Startup;

namespace SnapBack.Core.Insights;

public enum DriftLevel
{
    /// <summary>No usable baseline / profile to compare with.</summary>
    Unknown,
    Low,
    Medium,
    High,
}

/// <summary>What contributes most to the drift.</summary>
public enum DriftFactor
{
    None,
    NewApps,
    AppMemory,
    Processes,
    Age,
}

/// <summary>
/// How far the session has moved away from the selected profile.
/// </summary>
/// <remarks>
/// The score (0–100) adds four capped components, weighted towards what matters to the user:
/// new applications (4 points each, up to 40), memory used by them relative to installed RAM
/// (2 points per percent, up to 40), additional processes (1 point per 8, up to 10), and the age of
/// the baseline (up to 10 after a day, only while something is new). Below 20 is low, below 50
/// medium, otherwise high. Cached memory is never counted: it is not used by applications.
/// </remarks>
public sealed record SessionDrift
{
    public const int MediumThreshold = 20;
    public const int HighThreshold = 50;

    public static readonly SessionDrift Unknown = new() { Level = DriftLevel.Unknown };

    public DriftLevel Level { get; init; }

    public int Score { get; init; }

    /// <summary>Applications that are not part of the profile (the candidates).</summary>
    public int NewApps { get; init; }

    /// <summary>Profile applications that are no longer running.</summary>
    public int ClosedApps { get; init; }

    /// <summary>Processes now minus processes when the profile was captured.</summary>
    public int ProcessDelta { get; init; }

    /// <summary>Private memory of the new applications.</summary>
    public long NewAppMemoryBytes { get; init; }

    public TimeSpan? SessionUptime { get; init; }

    public TimeSpan? BaselineAge { get; init; }

    public DriftFactor MainFactor { get; init; }

    public bool IsInSync => Level != DriftLevel.Unknown && NewApps == 0;

    public static SessionDrift Compute(Analysis analysis, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var uptime = Uptime(analysis, nowUtc);
        if (analysis.Plan is not { } plan || analysis.Baseline is not { } baseline)
        {
            return Unknown with { SessionUptime = uptime };
        }

        var newApps = plan.Candidates.Count;
        var newMemory = plan.Candidates.Sum(c => c.Group.ActionablePrivateWorkingSet);
        var processes = analysis.Groups.Sum(g => g.ProcessCount);
        var processDelta = baseline.ProcessCount > 0 ? processes - baseline.ProcessCount : 0;
        var age = nowUtc - baseline.CreatedAtUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        var total = analysis.Memory.TotalPhysicalBytes;
        var appPoints = Math.Min(newApps * 4.0, 40);
        var memoryPoints = total > 0
            ? Math.Min(200.0 * newMemory / total, 40)
            : Math.Min(10.0 * newMemory / (1L << 30), 40);
        var processPoints = Math.Clamp(processDelta / 8.0, 0, 10);
        var agePoints = newApps > 0 ? Math.Min(age.TotalHours / 2.4, 10) : 0;

        var score = (int)Math.Round(Math.Clamp(appPoints + memoryPoints + processPoints + agePoints, 0, 100));
        var level = score >= HighThreshold ? DriftLevel.High : score >= MediumThreshold ? DriftLevel.Medium : DriftLevel.Low;

        var factors = new (DriftFactor Factor, double Points)[]
        {
            (DriftFactor.NewApps, appPoints),
            (DriftFactor.AppMemory, memoryPoints),
            (DriftFactor.Processes, processPoints),
            (DriftFactor.Age, agePoints),
        };
        var main = factors.MaxBy(f => f.Points);

        return new SessionDrift
        {
            Level = level,
            Score = score,
            NewApps = newApps,
            ClosedApps = analysis.Comparison?.ClosedSinceBaseline.Count ?? 0,
            ProcessDelta = processDelta,
            NewAppMemoryBytes = newMemory,
            SessionUptime = uptime,
            BaselineAge = age,
            MainFactor = main.Points > 0 ? main.Factor : DriftFactor.None,
        };
    }

    private static TimeSpan? Uptime(Analysis analysis, DateTime nowUtc)
    {
        var logon = StartupSettleDetector.EstimateLogonUtc(analysis.Snapshot);
        var uptime = nowUtc - logon;
        return uptime > TimeSpan.Zero ? uptime : null;
    }
}
