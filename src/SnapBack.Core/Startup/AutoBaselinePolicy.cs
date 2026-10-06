using SnapBack.Core.Profiles;

namespace SnapBack.Core.Startup;

/// <summary>Decisions of the automatic baseline taken after signing in to Windows.</summary>
public static class AutoBaselinePolicy
{
    /// <summary>The profile the automatic baseline updates: the configured one, else the default profile.</summary>
    public static Profile? Target(ProfileSet profiles, Guid? configuredProfileId) =>
        profiles.Find(configuredProfileId) ?? profiles.Default;

    /// <summary>
    /// Whether to capture: not when the target profile was already captured during this sign-in
    /// (e.g. SnapBack was restarted). The sign-in time is used rather than the boot time, because
    /// with Fast Startup Windows resumes the previous boot instead of booting again.
    /// </summary>
    public static bool ShouldCapture(Profile? target, DateTime logonUtc) =>
        target is null || target.Baseline.CreatedAtUtc < logonUtc;
}
