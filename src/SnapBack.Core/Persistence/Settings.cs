using System.Text.Json.Serialization;
using SnapBack.Core.Localization;
using SnapBack.Core.Memory;
using SnapBack.Core.Rules;

namespace SnapBack.Core.Persistence;

/// <summary>What the window's close button does.</summary>
public enum CloseButtonAction
{
    /// <summary>Hide the window; SnapBack keeps running in the notification area.</summary>
    MinimizeToTray,

    /// <summary>Exit SnapBack.</summary>
    Exit,
}

/// <summary>User settings. Defaults are the safe choice; values are clamped on load.</summary>
public sealed record Settings
{
    /// <summary>2: rules replace the keep list; autostart split from the automatic baseline.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Seconds to wait for apps to close after asking them (5-120).</summary>
    public int GracefulTimeoutSeconds { get; init; } = 20;

    /// <summary>Also pre-select "caution" apps (possible unsaved data, browsers, ...). Off by default.</summary>
    public bool PreselectCautionItems { get; init; }

    /// <summary>Start SnapBack after signing in to Windows (per-user Run entry; opt-in).</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>When started with Windows, stay in the notification area instead of opening the window.</summary>
    public bool StartMinimizedToTray { get; init; } = true;

    /// <summary>When started with Windows, capture the baseline once startup has settled (opt-in).</summary>
    public bool AutoBaselineOnSignIn { get; init; }

    /// <summary>Profile updated by the automatic baseline; <c>null</c> means the default profile.</summary>
    public Guid? AutoBaselineProfileId { get; init; }

    public CloseButtonAction CloseButton { get; init; } = CloseButtonAction.MinimizeToTray;

    /// <summary>Occasional notifications (high session drift, unusual memory growth).</summary>
    public bool ShowNotifications { get; init; } = true;

    /// <summary>Whether the "SnapBack is still running in the notification area" hint was shown.</summary>
    public bool TrayHintShown { get; init; }

    /// <summary>Minimum time after sign-in before the automatic baseline is taken.</summary>
    public int AutoBaselineMinDelaySeconds { get; init; } = 120;

    /// <summary>How long the set of running apps must stay unchanged before the automatic baseline is taken.</summary>
    public int AutoBaselineQuietSeconds { get; init; } = 45;

    /// <summary>Upper bound on waiting for startup to settle.</summary>
    public int AutoBaselineMaxWaitSeconds { get; init; } = 600;

    /// <summary>Standing per-app rules: always keep, always close, ask every time.</summary>
    public List<AppRule> Rules { get; init; } = [];

    /// <summary>Keep list of settings version 1; migrated into <see cref="Rules"/> on load.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<KeepRule>? KeepList { get; init; }

    /// <summary>Number of operations kept in the history.</summary>
    public int HistoryLimit { get; init; } = 50;

    /// <summary>UI language: "system" (follow Windows), "en", "ru" or "zh-CN".</summary>
    public string Language { get; init; } = Languages.System;

    /// <summary>Deep Memory Cleanup mode last chosen by the user.</summary>
    public DeepCleanupMode DeepCleanupMode { get; init; } = DeepCleanupMode.Standby;

    public Settings Normalize()
    {
        var legacyKeep = (KeepList ?? [])
            .Where(k => k is not null && !string.IsNullOrWhiteSpace(k.AppKey))
            .Select(k => new AppRule(k.AppKey, k.DisplayName ?? k.AppKey, AppRuleKind.AlwaysKeep, k.AddedAtUtc));

        // Explicit rules win over migrated keep-list entries for the same app.
        var rules = (Rules ?? [])
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.AppKey) && Enum.IsDefined(r.Kind))
            .Concat(legacyKeep)
            .DistinctBy(r => r.AppKey, StringComparer.Ordinal)
            .Select(r => r with { DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? r.AppKey : r.DisplayName })
            .ToList();

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            GracefulTimeoutSeconds = Math.Clamp(GracefulTimeoutSeconds, 5, 120),
            AutoBaselineMinDelaySeconds = Math.Clamp(AutoBaselineMinDelaySeconds, 0, 3600),
            AutoBaselineQuietSeconds = Math.Clamp(AutoBaselineQuietSeconds, 10, 600),
            AutoBaselineMaxWaitSeconds = Math.Clamp(AutoBaselineMaxWaitSeconds, 60, 3600),
            HistoryLimit = Math.Clamp(HistoryLimit, 1, 500),
            Language = Languages.NormalizeSetting(Language),
            DeepCleanupMode = Enum.IsDefined(DeepCleanupMode) ? DeepCleanupMode : DeepCleanupMode.Standby,
            CloseButton = Enum.IsDefined(CloseButton) ? CloseButton : CloseButtonAction.MinimizeToTray,

            // Version 1 started SnapBack at sign-in only for the automatic baseline.
            StartWithWindows = StartWithWindows || (SchemaVersion < 2 && AutoBaselineOnSignIn),
            Rules = rules,
            KeepList = null,
        };
    }

    public IReadOnlyDictionary<string, AppRuleKind> RuleLookup => AppRules.ToLookup(Rules);

    public IReadOnlySet<string> KeepAppKeys =>
        Rules.Where(r => r.Kind == AppRuleKind.AlwaysKeep).Select(r => r.AppKey).ToHashSet(StringComparer.Ordinal);

    public AppRuleKind? RuleFor(string appKey) => Rules.FirstOrDefault(r => r.AppKey == appKey)?.Kind;

    public Settings WithRule(string appKey, string displayName, AppRuleKind? kind, DateTime nowUtc) =>
        this with { Rules = AppRules.With(Rules, appKey, displayName, kind, nowUtc) };

    public Settings WithKept(string appKey, string displayName, DateTime nowUtc) =>
        WithRule(appKey, displayName, AppRuleKind.AlwaysKeep, nowUtc);

    public Settings WithoutKept(string appKey) =>
        RuleFor(appKey) == AppRuleKind.AlwaysKeep ? WithRule(appKey, "", null, DateTime.UtcNow) : this;
}

/// <summary>An application the user always wants to keep running (settings version 1).</summary>
public sealed record KeepRule(string AppKey, string DisplayName, DateTime AddedAtUtc);
