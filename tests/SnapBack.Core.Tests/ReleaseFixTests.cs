using Microsoft.Extensions.Logging.Abstractions;
using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Environments;
using SnapBack.Core.Execution;
using SnapBack.Core.Grouping;
using SnapBack.Core.Insights;
using SnapBack.Core.Model;
using SnapBack.Core.Persistence;
using SnapBack.Core.Planning;
using SnapBack.Core.Rules;
using SnapBack.Core.Tests.Fakes;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

/// <summary>Regression tests for issues found in the pre-release review.</summary>
public sealed class ReleaseFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snapback-rel-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void Always_close_never_preselects_items_that_need_manual_review()
    {
        var script = App(5000, @"C:\Python\python.exe", 30, parent: 1100, subsystem: ImageSubsystem.Console);
        var system = new FakeSystem(FreshSession());
        var engine = Engine(system);
        engine.CreateBaseline(BaselineOrigin.Manual);
        system.Replace(script);
        engine.UpdateSettings(s => s.WithRule(AppKeys.For(script), "Python", AppRuleKind.AlwaysClose, DateTime.UtcNow));

        var item = engine.Analyze().Plan!.Candidates.Single(c => c.Group.Root.Pid == 5000);
        Assert.Equal(SafetyLevel.ManualReview, item.Level);
        Assert.Equal(AppRuleKind.AlwaysClose, item.Rule);
        Assert.True(item.IsSelectable);
        Assert.False(item.DefaultSelected);
    }

    [Fact]
    public async Task History_keeps_two_instances_of_the_same_app_apart()
    {
        var system = new FakeSystem(FreshSession());
        var engine = Engine(system);
        engine.CreateBaseline(BaselineOrigin.Manual);
        system.Replace(App(5000, @"C:\Apps\Term\Term.exe", 30, parent: 1100, visibleWindows: 1, memoryMb: 100));
        system.Replace(App(5100, @"C:\Apps\Term\Term.exe", 31, parent: 1100, visibleWindows: 1, memoryMb: 200));

        var analysis = engine.Analyze();
        Assert.Equal(2, analysis.Plan!.Candidates.Count);
        var report = await engine.StartOperation(analysis, analysis.Plan.Candidates).RunGracefulAsync();
        engine.RecordHistory(report);

        var record = Assert.Single(engine.LoadHistory());
        Assert.Equal(2, record.Requested);
        Assert.Equal(2, record.Closed);
        Assert.Equal(300L << 20, record.MemoryFreedBytes);
    }

    [Fact]
    public void A_corrupt_profiles_file_is_not_replaced_by_the_old_baseline()
    {
        var paths = new AppPaths(_dir);
        var snap = new FakeSystem(FreshSession()).Capture();
        var legacy = BaselineFactory.Create(snap, new AppGrouper(new ProcessClassifier()).Group(snap), BaselineOrigin.Manual, "PC");
        new BaselineStore(new JsonFileStore<Baseline>(paths.Baseline, NullLogger.Instance)).Save(legacy);
        File.WriteAllText(paths.Profiles, "{ broken");

        var engine = Engine(new FakeSystem(FreshSession()));
        Assert.Empty(engine.Profiles.Profiles);
        Assert.False(File.Exists(paths.Profiles)); // moved aside, not overwritten with a migrated profile
        Assert.Single(Directory.GetFiles(_dir, "profiles.json.corrupt-*"));
    }

    [Fact]
    public void Changes_made_by_another_process_are_picked_up_and_not_overwritten()
    {
        var system = new FakeSystem(FreshSession());
        var gui = Engine(system);       // e.g. the window running in the tray
        gui.CreateBaseline(BaselineOrigin.Manual);

        var cli = Engine(system);       // e.g. snapback-cli in a terminal
        Thread.Sleep(20);
        var work = cli.CreateProfile("Work", BaselineOrigin.Manual);
        cli.UpdateSettings(s => s.WithRule("path:c:\\apps\\a.exe", "A", AppRuleKind.AlwaysClose, DateTime.UtcNow));

        // The GUI changes something else afterwards: the CLI's changes survive.
        Thread.Sleep(20);
        gui.UpdateSettings(s => s with { TrayHintShown = true });
        gui.RenameProfile(gui.Profiles.Profiles[0].Id, "Clean");

        var reloaded = Engine(system);
        Assert.Equal(AppRuleKind.AlwaysClose, reloaded.Settings.RuleFor("path:c:\\apps\\a.exe"));
        Assert.True(reloaded.Settings.TrayHintShown);
        Assert.Contains(reloaded.Profiles.Profiles, p => p.Id == work.Id);

        // And an analysis in the GUI sees the new profile.
        gui.Analyze();
        Assert.Contains(gui.Profiles.Profiles, p => p.Name == "Work");
    }

    [Fact]
    public void Memory_history_handed_out_is_a_copy()
    {
        var tracker = new MemoryTracker();
        var groups = new AppGrouper(new ProcessClassifier()).Group(Snapshot([.. FreshSession(), App(5000, @"C:\Apps\A\A.exe", 30, parent: 1100, visibleWindows: 1)]));
        var id = groups.Single(g => g.Root.Pid == 5000).Id;
        tracker.Record(groups, Boot.AddHours(1));
        var copy = tracker.For(id)!;
        tracker.Record(groups, Boot.AddHours(2));

        Assert.Single(copy.Samples);
        Assert.Equal(2, tracker.For(id)!.Samples.Count);
    }

    [Fact]
    public void Docker_is_stopped_before_wsl()
    {
        var wsl = EnvironmentStopConfirmation.FromUserConfirmation(new EnvironmentDetails { Kind = EnvironmentKind.Wsl, IsRunning = true }, Boot);
        var docker = EnvironmentStopConfirmation.FromUserConfirmation(new EnvironmentDetails { Kind = EnvironmentKind.Docker, IsRunning = true }, Boot);
        Assert.Equal([EnvironmentKind.Docker, EnvironmentKind.Wsl], EnvironmentStopConfirmation.InStopOrder([wsl, docker]).Select(c => c.Kind));
    }

    private sealed class FailingWsl : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResult(-1, "There are no running distributions."));
    }

    [Fact]
    public async Task A_failed_wsl_listing_is_unknown_not_an_empty_list()
    {
        var vm = new ProcessInfo { Identity = new ProcessIdentity(7000, At(20)), ImageName = "vmmemWSL", SessionId = 0 };
        var service = new EnvironmentService(new FailingWsl(), new EnvironmentTools(@"C:\Windows\System32\wsl.exe", null),
            () => Snapshot([.. FreshSession(), vm]), TimeProvider.System, NullLogger.Instance);

        var d = await service.InspectAsync(EnvironmentKind.Wsl);
        Assert.False(d.ItemsKnown);
        Assert.Empty(d.RunningItems);
        Assert.Contains("All WSL distributions", d.Warning, StringComparison.Ordinal);
    }
}
