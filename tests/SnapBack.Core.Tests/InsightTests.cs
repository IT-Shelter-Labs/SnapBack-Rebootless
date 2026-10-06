using Microsoft.Extensions.Logging.Abstractions;
using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Execution;
using SnapBack.Core.Grouping;
using SnapBack.Core.Insights;
using SnapBack.Core.Memory;
using SnapBack.Core.Model;
using SnapBack.Core.Persistence;
using SnapBack.Core.Planning;
using SnapBack.Core.Profiles;
using SnapBack.Core.Startup;
using SnapBack.Core.Tests.Fakes;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public sealed class InsightTests : IDisposable
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;
    private static readonly AppGrouper Grouper = new(new ProcessClassifier());
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snapback-ins-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private SnapBackEngine Engine(FakeSystem system)
    {
        var (settings, profiles, history) = StoreFactory.Create(new AppPaths(_dir), NullLoggerFactory.Instance);
        var engine = new SnapBackEngine(system, system, settings, profiles, history, NullLoggerFactory.Instance);
        engine.UpdateSettings(s => s with { GracefulTimeoutSeconds = 5 });
        return engine;
    }

    private static ProcessInfo NewApp(int pid, string name, long memoryMb, double minutes = 30) =>
        App(pid, $@"C:\Apps\{name}\{name}.exe", minutes, parent: 1100, visibleWindows: 1, memoryMb: memoryMb);

    /// <summary>Clean profile captured, then <paramref name="started"/> apps launched.</summary>
    private (SnapBackEngine Engine, FakeSystem System) Session(params ProcessInfo[] started)
    {
        var system = new FakeSystem(FreshSession());
        var engine = Engine(system);
        engine.CreateBaseline(BaselineOrigin.Manual);
        foreach (var p in started)
        {
            system.Replace(p);
        }

        return (engine, system);
    }

    private static Analysis WithMemory(Analysis a, MemoryStatus memory) => new()
    {
        Snapshot = new SystemSnapshot(a.Snapshot.Context, a.Snapshot.Processes.Values, memory, a.Snapshot.TakenAtUtc),
        Groups = a.Groups,
        Profile = a.Profile,
        Comparison = a.Comparison,
        Plan = a.Plan,
    };

    // ---- Session drift ---------------------------------------------------------------------

    [Fact]
    public void Drift_is_unknown_without_a_profile()
    {
        var engine = Engine(new FakeSystem(FreshSession()));
        var drift = engine.Analyze().Drift(Boot.AddHours(3));
        Assert.Equal(DriftLevel.Unknown, drift.Level);
        Assert.False(drift.IsInSync);
        Assert.NotNull(drift.SessionUptime); // uptime is known even without a profile
    }

    [Fact]
    public void Drift_is_low_when_nothing_was_added()
    {
        var (engine, _) = Session();
        var drift = engine.Analyze().Drift(Boot.AddHours(3));
        Assert.Equal(DriftLevel.Low, drift.Level);
        Assert.True(drift.IsInSync);
        Assert.Equal(0, drift.NewApps);
        Assert.Equal(DriftFactor.None, drift.MainFactor);
    }

    [Fact]
    public void Drift_grows_with_new_apps_and_their_memory()
    {
        var (small, _) = Session(NewApp(5000, "Notes", 150));
        var medium = Session(NewApp(5000, "A", 600), NewApp(5100, "B", 500), NewApp(5200, "C", 400), NewApp(5300, "D", 300)).Engine;
        var high = Session(Enumerable.Range(0, 12).Select(i => NewApp(5000 + (i * 10), "App" + i, 700)).ToArray()).Engine;

        var now = Boot.AddHours(3);
        var d1 = small.Analyze().Drift(now);
        var d2 = medium.Analyze().Drift(now);
        var d3 = high.Analyze().Drift(now);

        Assert.Equal(DriftLevel.Low, d1.Level);
        Assert.Equal(DriftLevel.Medium, d2.Level);
        Assert.Equal(DriftLevel.High, d3.Level);
        Assert.True(d1.Score < d2.Score && d2.Score < d3.Score);
        Assert.Equal(12, d3.NewApps);
        Assert.Equal(12 * 700 * MiB, d3.NewAppMemoryBytes);
        Assert.Equal(12, d3.ProcessDelta);
    }

    [Fact]
    public void Memory_matters_more_than_the_number_of_processes()
    {
        // One app using a third of the RAM drifts more than one app with many small helpers.
        var big = Session(NewApp(5000, "Game", 5 * 1024)).Engine.Analyze().Drift(Boot.AddHours(3));
        var helpers = new[] { NewApp(6000, "Tool", 50) }
            .Concat(Enumerable.Range(1, 40).Select(i => App(6000 + i, @"C:\Apps\Tool\Tool.exe", 31, parent: 6000, memoryMb: 1)))
            .ToArray();
        var many = Session(helpers).Engine.Analyze().Drift(Boot.AddHours(3));

        Assert.Equal(DriftFactor.AppMemory, big.MainFactor);
        Assert.True(big.Score > many.Score);
        Assert.Equal(41, many.ProcessDelta);
    }

    [Fact]
    public void Drift_ignores_cached_memory()
    {
        var (engine, _) = Session(NewApp(5000, "A", 800));
        var analysis = engine.Analyze();
        var lotsOfCache = WithMemory(analysis, new MemoryStatus(16 * GiB, 2 * GiB) { StandbyBytes = 9 * GiB });
        var noCache = WithMemory(analysis, new MemoryStatus(16 * GiB, 2 * GiB) { StandbyBytes = 0 });
        Assert.Equal(noCache.Drift(Boot).Score, lotsOfCache.Drift(Boot).Score);
    }

    // ---- Expected impact -------------------------------------------------------------------

    [Fact]
    public void Impact_breaks_down_memory_in_use_and_keeps_cache_apart()
    {
        var (engine, _) = Session(NewApp(5000, "A", 2048), NewApp(5100, "B", 1024));
        var analysis = engine.Analyze();
        var impact = ImpactEstimate.Compute(analysis, analysis.Plan!.Candidates);
        var b = impact.Breakdown;

        Assert.Equal(analysis.Memory.UsedPhysicalBytes, b.InUseBytes);
        Assert.Equal(3 * GiB, b.NewAppsBytes);
        Assert.Equal(b.InUseBytes, b.NewAppsBytes + b.ProfileAppsBytes + b.SystemAndOtherBytes);
        Assert.Equal(analysis.Memory.CachedBytes, b.CacheBytes);
        Assert.Equal(3 * GiB, impact.EstimatedReclaimBytes);
        Assert.Equal(2, impact.AppsToClose);
        Assert.Equal(ImpactLevel.High, impact.Level); // 3 GB of 16 GB
        Assert.Contains(impact.Explanations, e => e.Contains("estimate", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0, 0, ImpactLevel.None)]
    [InlineData(200, 1, ImpactLevel.Low)]
    [InlineData(800, 1, ImpactLevel.Medium)]
    [InlineData(100, 8, ImpactLevel.Medium)]
    [InlineData(1700, 1, ImpactLevel.High)]
    public void Impact_levels_follow_the_share_of_installed_memory(long reclaimMb, int apps, ImpactLevel expected) =>
        Assert.Equal(expected, ImpactEstimate.LevelFor(reclaimMb * MiB, apps, 16 * GiB));

    [Fact]
    public void Impact_says_so_when_most_memory_is_not_from_new_apps()
    {
        var (engine, _) = Session(NewApp(5000, "Small", 120));
        var analysis = engine.Analyze();
        var impact = ImpactEstimate.Compute(analysis, analysis.Plan!.Candidates);
        Assert.True(impact.MostMemoryIsNotFromNewApps);
        Assert.Equal(ImpactLevel.Low, impact.Level);
        Assert.Contains(impact.Explanations, e => e.Contains("not caused by applications added after", StringComparison.Ordinal));
    }

    [Fact]
    public void Impact_counts_only_what_would_actually_be_closed()
    {
        var (engine, _) = Session(NewApp(5000, "A", 1024), NewApp(5100, "B", 1024));
        var analysis = engine.Analyze();
        var one = analysis.Plan!.Candidates.Take(1).ToList();
        var protectedItem = analysis.Plan.Protected.First();

        var impact = ImpactEstimate.Compute(analysis, [.. one, .. one, protectedItem]);
        Assert.Equal(1, impact.AppsToClose);
        Assert.Equal(GiB, impact.EstimatedReclaimBytes);
        Assert.Equal(ImpactLevel.None, ImpactEstimate.Compute(analysis, []).Level);
    }

    // ---- Memory tracking -------------------------------------------------------------------

    private static IReadOnlyList<AppGroup> Groups(params (int Pid, string Name, long Mb)[] apps) =>
        Grouper.Group(Snapshot([.. FreshSession(), .. apps.Select(a => NewApp(a.Pid, a.Name, a.Mb) with { PrivateBytes = a.Mb * MiB })]));

    [Fact]
    public void Tracker_samples_at_most_every_ten_minutes()
    {
        var tracker = new MemoryTracker();
        var t = Boot.AddHours(1);
        Assert.True(tracker.Record(Groups((5000, "Discord", 400)), t));
        Assert.False(tracker.Record(Groups((5000, "Discord", 420)), t.AddMinutes(5)));
        Assert.True(tracker.Record(Groups((5000, "Discord", 450)), t.AddMinutes(10)));

        var series = tracker.Assessments().Single().Series;
        Assert.Equal([400 * MiB, 450 * MiB], series.Samples.Select(s => s.Bytes));
    }

    [Fact]
    public void Steady_long_growth_is_reported_as_unusual_but_short_or_noisy_growth_is_not()
    {
        var tracker = new MemoryTracker();
        var t = Boot.AddHours(1);
        // Discord grows steadily: 420 MB → ~1.4 GB over 9 hours. Chrome fluctuates. Notes is flat.
        for (var i = 0; i <= 36; i++)
        {
            var discord = 420 + (i * 28);
            var chrome = 900 + (i % 2 == 0 ? 0 : 600);
            tracker.Record(Groups((5000, "Discord", discord), (5100, "Chrome", chrome), (5200, "Notes", 80)), t.AddMinutes(15 * i));
        }

        var all = tracker.Assessments().ToDictionary(x => x.Series.DisplayName);
        Assert.Equal(GrowthLevel.Unusual, all["Discord"].Growth.Level);
        Assert.InRange(all["Discord"].Growth.ToBytes, 1300 * MiB, 1500 * MiB);
        Assert.Equal(GrowthLevel.None, all["Chrome"].Growth.Level);
        Assert.Equal(GrowthLevel.None, all["Notes"].Growth.Level);
        Assert.Single(tracker.Unusual());

        // The same growth within one hour is not enough history.
        var quick = new MemoryTracker();
        for (var i = 0; i <= 6; i++)
        {
            quick.Record(Groups((5000, "Discord", 400 + (i * 200))), t.AddMinutes(10 * i));
        }

        Assert.Empty(quick.Unusual());
    }

    [Fact]
    public void A_restarted_app_starts_a_new_history_and_exited_apps_are_dropped()
    {
        var tracker = new MemoryTracker();
        var t = Boot.AddHours(1);
        tracker.Record(Groups((5000, "Discord", 400)), t);
        tracker.Record(Groups((5050, "Discord", 300)), t.AddMinutes(10)); // restarted: new PID

        var series = Assert.Single(tracker.Assessments()).Series;
        Assert.Equal(5050, series.Instance.Pid);
        Assert.Single(series.Samples);

        tracker.Record(Groups(), t.AddMinutes(20));
        Assert.Empty(tracker.Assessments());
    }

    [Fact]
    public void Tracker_history_is_bounded()
    {
        var tracker = new MemoryTracker();
        var t = Boot.AddHours(1);
        var apps = Enumerable.Range(0, MemoryTracker.MaxSeries + 20).Select(i => (5000 + (i * 10), "App" + i, 50L + i)).ToArray();
        for (var i = 0; i < MemoryTracker.MaxSamples * 3; i++)
        {
            tracker.Record(Groups(apps), t.AddMinutes(10 * i));
        }

        var all = tracker.Assessments();
        Assert.Equal(MemoryTracker.MaxSeries, all.Count);
        Assert.All(all, x => Assert.InRange(x.Series.Samples.Count, MemoryTracker.MaxSamples / 2, MemoryTracker.MaxSamples));

        // Thinning keeps the whole span: the first sample is still there.
        Assert.All(all, x => Assert.Equal(t, x.Series.Samples[0].AtUtc));
        Assert.DoesNotContain(all, x => x.Series.DisplayName == "App0"); // the smallest consumers are dropped
    }

    [Fact]
    public void Protected_processes_are_not_tracked()
    {
        var tracker = new MemoryTracker();
        tracker.Record(Grouper.Group(Snapshot(FreshSession())), Boot.AddHours(1));
        Assert.DoesNotContain(tracker.Assessments(), x => x.Series.DisplayName.Contains("explorer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_engine_feeds_the_tracker_from_its_analyses()
    {
        var (engine, _) = Session(NewApp(5000, "A", 300));
        engine.Analyze();
        Assert.NotNull(engine.Tracker.LastSampleUtc);
        Assert.Contains(engine.Tracker.Assessments(), x => x.Series.DisplayName == "A");
    }

    // ---- Notifications ---------------------------------------------------------------------

    private static SessionDrift Drift(DriftLevel level, int apps = 12) => new() { Level = level, NewApps = apps };

    private static ImpactEstimate Impact(ImpactLevel level, long reclaim, MemoryPressure pressure) => new()
    {
        Level = level,
        EstimatedReclaimBytes = reclaim,
        Pressure = pressure,
        Breakdown = new MemoryBreakdown(),
    };

    [Fact]
    public void High_drift_notifies_once_then_waits_for_cooldown_and_rearm()
    {
        var policy = new NotificationPolicy();
        var t = Boot.AddHours(2);
        var high = Drift(DriftLevel.High);
        var impact = Impact(ImpactLevel.High, 8 * GiB, MemoryPressure.Moderate);

        var first = policy.Evaluate(high, impact, [], enabled: true, windowVisible: false, t);
        Assert.Equal(NotificationKind.HighDrift, first?.Kind);
        Assert.Contains("12", first!.Text, StringComparison.Ordinal);

        Assert.Null(policy.Evaluate(high, impact, [], true, false, t.AddMinutes(15)));
        Assert.Null(policy.Evaluate(high, impact, [], true, false, t.AddHours(7))); // still the same high drift: not re-armed

        policy.Evaluate(Drift(DriftLevel.Medium), impact, [], true, false, t.AddHours(8)); // drops below high
        Assert.NotNull(policy.Evaluate(high, impact, [], true, false, t.AddHours(9)));
    }

    [Fact]
    public void Notifications_respect_the_setting_the_window_and_memory_situation()
    {
        var t = Boot.AddHours(2);
        var high = Drift(DriftLevel.High);
        Assert.Null(new NotificationPolicy().Evaluate(high, Impact(ImpactLevel.High, 8 * GiB, MemoryPressure.Low), [], enabled: false, windowVisible: false, t));
        Assert.Null(new NotificationPolicy().Evaluate(high, Impact(ImpactLevel.High, 8 * GiB, MemoryPressure.Low), [], enabled: true, windowVisible: true, t));

        // Plenty of memory available and only a medium impact: not worth a notification.
        Assert.Null(new NotificationPolicy().Evaluate(high, Impact(ImpactLevel.Medium, 2 * GiB, MemoryPressure.Healthy), [], true, false, t));

        // Little reclaimable memory (e.g. RAM full of cache, not apps): no notification.
        Assert.Null(new NotificationPolicy().Evaluate(high, Impact(ImpactLevel.Low, 300 * MiB, MemoryPressure.Low), [], true, false, t));
        Assert.Null(new NotificationPolicy().Evaluate(Drift(DriftLevel.Medium), Impact(ImpactLevel.High, 8 * GiB, MemoryPressure.Low), [], true, false, t));
    }

    [Fact]
    public void Unusual_growth_notifies_once_per_app_instance_and_never_says_leak_for_sure()
    {
        var tracker = new MemoryTracker();
        var t = Boot.AddHours(1);
        for (var i = 0; i <= 36; i++)
        {
            tracker.Record(Groups((5000, "Discord", 420 + (i * 28))), t.AddMinutes(15 * i));
        }

        var policy = new NotificationPolicy();
        var low = Drift(DriftLevel.Low, 0);
        var none = Impact(ImpactLevel.None, 0, MemoryPressure.Healthy);
        var n = policy.Evaluate(low, none, tracker.Unusual(), true, false, t.AddHours(10));
        Assert.Equal(NotificationKind.UnusualGrowth, n?.Kind);
        Assert.Contains("Discord", n!.Text, StringComparison.Ordinal);
        Assert.Contains("can be normal", n.Text, StringComparison.Ordinal);
        Assert.Null(policy.Evaluate(low, none, tracker.Unusual(), true, false, t.AddHours(20)));
    }

    [Fact]
    public void Never_more_than_three_notifications_a_day()
    {
        var policy = new NotificationPolicy();
        var t = Boot;
        var count = 0;
        for (var hour = 0; hour < 24; hour++)
        {
            policy.Evaluate(Drift(DriftLevel.Low), Impact(ImpactLevel.High, 8 * GiB, MemoryPressure.Low), [], true, false, t.AddHours(hour).AddMinutes(1));
            if (policy.Evaluate(Drift(DriftLevel.High), Impact(ImpactLevel.High, 8 * GiB, MemoryPressure.Low), [], true, false, t.AddHours(hour).AddMinutes(30)) is not null)
            {
                count++;
            }
        }

        Assert.InRange(count, 1, NotificationPolicy.MaxPerDay);
    }

    // ---- History ---------------------------------------------------------------------------

    [Fact]
    public async Task History_keeps_one_entry_per_operation_with_before_after_and_force_counts()
    {
        var (engine, system) = Session(NewApp(5000, "Editor", 400), NewApp(5100, "Viewer", 300));
        system[5000] = Behavior.Ignores;
        var analysis = engine.Analyze();
        var before = SessionStatistics.From(analysis);
        var operation = engine.StartOperation(analysis, analysis.Plan!.Candidates);
        var report = await operation.RunGracefulAsync();
        engine.RecordHistory(report, analysis.Profile);

        var stuck = report.Items.Single(i => i.CanForce);
        var auth = Safety.ForceAuthorization.FromUserConfirmation(stuck.RemainingProcesses.Select(p => p.Identity), DateTime.UtcNow);
        var forced = await operation.ForceTerminateAsync(auth, [stuck.Item.Group.Id]);
        engine.RecordHistory(forced, analysis.Profile);

        var after = SessionStatistics.From(engine.Analyze());
        engine.UpdateHistory(report.OperationId, r => r!.WithStatistics(before, after));
        var cleanup = new DeepCleanupReport(DeepCleanupMode.Standby, DeepCleanupOutcome.For(DeepCleanupStatus.Completed),
            after.Memory, after.Memory with { StandbyBytes = 512 * MiB }, Boot);
        engine.UpdateHistory(report.OperationId, r => r!.WithDeepCleanup(cleanup).WithEnvironmentStopped("WSL"));

        var record = Assert.Single(engine.LoadHistory());
        Assert.Equal("Clean", record.ProfileName);
        Assert.Equal(2, record.Requested);
        Assert.Equal(2, record.Closed);
        Assert.Equal(0, record.StillRunning);
        Assert.Equal(1, record.ForceTerminations);
        Assert.Equal(700 * MiB, record.MemoryFreedBytes);
        Assert.Equal(before.Apps, record.AppsBefore);
        Assert.Equal(before.Apps - 2, record.AppsAfter);
        Assert.Equal(before.Processes - 2, record.ProcessesAfter);
        Assert.Equal(before.Memory.UsedPhysicalBytes - (700 * MiB), record.InUseAfterBytes);
        Assert.True(record.DeepCleanupUsed);
        Assert.Equal(after.Memory.StandbyBytes - (512 * MiB), record.DeepCleanupReclaimedBytes);
        Assert.Equal(700 * MiB, record.MemoryFreedBytes); // the cleanup is never added to what the apps released
        Assert.Equal(["WSL"], record.EnvironmentsStopped);
        Assert.True(record.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public void History_upsert_keeps_position_and_limit()
    {
        var history = new HistoryStore(new JsonFileStore<List<OperationRecord>>(Path.Combine(_dir, "h.json"), NullLogger.Instance));
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
        {
            history.Append(new OperationRecord { OperationId = id }, limit: 3);
        }

        history.Upsert(ids[2], r => r! with { Closed = 9 }, limit: 3);
        var list = history.Load();
        Assert.Equal([ids[3], ids[2], ids[1]], list.Select(r => r.OperationId));
        Assert.Equal(9, list[1].Closed);
    }

    // ---- Automatic baseline after startup --------------------------------------------------

    [Fact]
    public void Automatic_baseline_targets_the_chosen_or_default_profile_once_per_sign_in()
    {
        var baseline = new Baseline { UserSid = User, CreatedAtUtc = Boot.AddMinutes(5) };
        var clean = new Profile { Name = "Clean", Baseline = baseline };
        var gaming = new Profile { Name = "Gaming", Baseline = baseline };
        var set = new ProfileSet { Profiles = [clean, gaming], DefaultProfileId = clean.Id }.Normalize();

        Assert.Equal(clean.Id, AutoBaselinePolicy.Target(set, null)!.Id);
        Assert.Equal(gaming.Id, AutoBaselinePolicy.Target(set, gaming.Id)!.Id);
        Assert.Equal(clean.Id, AutoBaselinePolicy.Target(set, Guid.NewGuid())!.Id); // deleted profile → default
        Assert.Null(AutoBaselinePolicy.Target(ProfileSet.Empty, null));

        Assert.True(AutoBaselinePolicy.ShouldCapture(null, Boot));
        Assert.True(AutoBaselinePolicy.ShouldCapture(clean, logonUtc: Boot.AddHours(1))); // captured before this sign-in
        Assert.False(AutoBaselinePolicy.ShouldCapture(clean, logonUtc: Boot)); // already captured during this sign-in
    }

    [Fact]
    public void Startup_is_not_settled_while_the_cpu_is_busy()
    {
        var start = Boot.AddMinutes(1);
        var d = new StartupSettleDetector(TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), Boot, start);
        string[] apps = ["a", "b"];

        Assert.Equal(SettleState.Waiting, d.Observe(start, apps, 0.9));
        Assert.Equal(SettleState.Waiting, d.Observe(start.AddSeconds(40), apps, 0.8)); // apps stable, CPU busy
        Assert.Equal(SettleState.Waiting, d.Observe(start.AddSeconds(50), apps, 0.1));
        Assert.Equal(SettleState.Settled, d.Observe(start.AddSeconds(85), apps, 0.1));

        // A machine that never calms down still gets its baseline after the maximum wait.
        var busy = new StartupSettleDetector(TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), Boot, start);
        Assert.Equal(SettleState.TimedOut, busy.Observe(start.AddMinutes(11), apps, 1.0));
    }
}
