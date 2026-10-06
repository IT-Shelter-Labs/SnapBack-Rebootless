namespace SnapBack.Core.Persistence;

/// <summary>User settings. Defaults are the safe choice; values are clamped on load.</summary>
public sealed record Settings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Seconds to wait for apps to close after asking them (5-120).</summary>
    public int GracefulTimeoutSeconds { get; init; } = 20;

    /// <summary>Also pre-select "caution" apps (possible unsaved data, browsers, ...). Off by default.</summary>
    public bool PreselectCautionItems { get; init; }

    /// <summary>Create a baseline automatically after signing in to Windows (opt-in).</summary>
    public bool AutoBaselineOnSignIn { get; init; }

    /// <summary>Minimum time after sign-in before the automatic baseline is taken.</summary>
    public int AutoBaselineMinDelaySeconds { get; init; } = 120;

    /// <summary>How long the set of running apps must stay unchanged before the automatic baseline is taken.</summary>
    public int AutoBaselineQuietSeconds { get; init; } = 45;

    /// <summary>Upper bound on waiting for startup to settle.</summary>
    public int AutoBaselineMaxWaitSeconds { get; init; } = 600;

    /// <summary>Apps that are always kept (the user's whitelist).</summary>
    public List<KeepRule> KeepList { get; init; } = [];

    /// <summary>Number of operations kept in the history.</summary>
    public int HistoryLimit { get; init; } = 50;

    public Settings Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        GracefulTimeoutSeconds = Math.Clamp(GracefulTimeoutSeconds, 5, 120),
        AutoBaselineMinDelaySeconds = Math.Clamp(AutoBaselineMinDelaySeconds, 0, 3600),
        AutoBaselineQuietSeconds = Math.Clamp(AutoBaselineQuietSeconds, 10, 600),
        AutoBaselineMaxWaitSeconds = Math.Clamp(AutoBaselineMaxWaitSeconds, 60, 3600),
        HistoryLimit = Math.Clamp(HistoryLimit, 1, 500),
        KeepList = (KeepList ?? [])
            .Where(k => k is not null && !string.IsNullOrWhiteSpace(k.AppKey))
            .DistinctBy(k => k.AppKey, StringComparer.Ordinal)
            .ToList(),
    };

    public IReadOnlySet<string> KeepAppKeys => KeepList.Select(k => k.AppKey).ToHashSet(StringComparer.Ordinal);

    public Settings WithKept(string appKey, string displayName, DateTime nowUtc) =>
        KeepList.Any(k => k.AppKey == appKey)
            ? this
            : this with { KeepList = [.. KeepList, new KeepRule(appKey, displayName, nowUtc)] };

    public Settings WithoutKept(string appKey) => this with { KeepList = KeepList.Where(k => k.AppKey != appKey).ToList() };
}

/// <summary>An application the user always wants to keep running.</summary>
public sealed record KeepRule(string AppKey, string DisplayName, DateTime AddedAtUtc);
