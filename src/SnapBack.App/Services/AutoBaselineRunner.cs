using Microsoft.Extensions.Logging;
using SnapBack.Core.Baselines;
using SnapBack.Core.Startup;

namespace SnapBack.App.Services;

/// <summary>
/// Headless mode started at sign-in (<c>--auto-baseline</c>): waits until autostart programs have
/// finished launching, takes the baseline, and exits. It never closes anything.
/// </summary>
public static class AutoBaselineRunner
{
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

        var ctx = services.Probe.BaseContext;
        if (engine.Baseline is { } existing
            && (existing.BootTimeUtc - ctx.BootTimeUtc).Duration() <= BaselineComparer.BootTimeTolerance)
        {
            log.LogInformation("A baseline for the current boot already exists ({Created:u}); keeping it", existing.CreatedAtUtc);
            return;
        }

        var first = services.Probe.Capture();
        var detector = new StartupSettleDetector(
            TimeSpan.FromSeconds(settings.AutoBaselineMinDelaySeconds),
            TimeSpan.FromSeconds(settings.AutoBaselineQuietSeconds),
            TimeSpan.FromSeconds(settings.AutoBaselineMaxWaitSeconds),
            StartupSettleDetector.EstimateLogonUtc(first),
            DateTime.UtcNow);

        log.LogInformation("Waiting for startup to settle before taking the automatic baseline");
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = services.Probe.Capture();
            var groups = engine.Analyze(snapshot).Groups;
            var state = detector.Observe(DateTime.UtcNow, StartupSettleDetector.RelevantKeys(groups));
            if (state != SettleState.Waiting)
            {
                var baseline = engine.CreateBaseline(BaselineOrigin.Automatic, snapshot);
                log.LogInformation("Automatic baseline taken ({State}): {Apps} apps", state, baseline.Apps.Count);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }
}
