using Microsoft.Extensions.Logging;
using SnapBack.Core.Baselines;
using SnapBack.Core.Startup;
using SnapBack.Windows;

namespace SnapBack.App.Services;

/// <summary>
/// Automatic baseline after signing in (when SnapBack starts with Windows and the option is on):
/// waits until autostart programs have finished launching, then captures the profile. It never
/// closes anything.
/// </summary>
public static class AutoBaselineRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(AppServices services, CancellationToken cancellationToken)
    {
        var log = services.Loggers.CreateLogger("AutoBaseline");
        var engine = services.Engine;
        var settings = engine.Settings;

        if (!settings.AutoBaselineOnSignIn)
        {
            log.LogInformation("Automatic baseline is disabled in settings; nothing to do");
            return;
        }

        var first = services.Probe.Capture();
        var logon = StartupSettleDetector.EstimateLogonUtc(first);
        var target = AutoBaselinePolicy.Target(engine.Profiles, settings.AutoBaselineProfileId);
        if (!AutoBaselinePolicy.ShouldCapture(target, logon))
        {
            log.LogInformation("Profile {Profile} was already captured during this sign-in ({Created:u}); keeping it", target!.Name, target.Baseline.CreatedAtUtc);
            return;
        }

        var detector = new StartupSettleDetector(
            TimeSpan.FromSeconds(settings.AutoBaselineMinDelaySeconds),
            TimeSpan.FromSeconds(settings.AutoBaselineQuietSeconds),
            TimeSpan.FromSeconds(settings.AutoBaselineMaxWaitSeconds),
            logon,
            DateTime.UtcNow);
        var cpu = new CpuUsageMeter();
        cpu.Sample();

        log.LogInformation("Waiting for startup to settle before taking the automatic baseline");
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            var (state, snapshot, groups) = await Task.Run(() =>
            {
                using var _ = ResourceModes.BackgroundThread();
                var busy = cpu.Sample();
                var s = services.Probe.Capture();
                var g = engine.Analyze(s).Groups;
                return (detector.Observe(DateTime.UtcNow, StartupSettleDetector.RelevantKeys(g), busy), s, g);
            }, cancellationToken).ConfigureAwait(false);

            if (state == SettleState.Waiting)
            {
                continue;
            }

            // Settings or profiles may have changed while waiting: re-check everything.
            if (!engine.Settings.AutoBaselineOnSignIn)
            {
                log.LogInformation("Automatic baseline was turned off while waiting; nothing captured");
                return;
            }

            target = AutoBaselinePolicy.Target(engine.Profiles, engine.Settings.AutoBaselineProfileId);
            if (!AutoBaselinePolicy.ShouldCapture(target, logon))
            {
                log.LogInformation("Profile {Profile} was captured during this sign-in while waiting; keeping it", target!.Name);
                return;
            }

            var profile = target is null
                ? engine.CreateProfile(Core.Localization.Loc.Get("Profile_DefaultName"), BaselineOrigin.Automatic, snapshot)
                : engine.UpdateProfile(target.Id, BaselineOrigin.Automatic, snapshot);
            log.LogInformation("Automatic baseline taken ({State}) for profile {Profile}: {Apps} apps",
                state, profile.Name, groups.Count(g => !g.IsProtected));
            return;
        }
    }
}
