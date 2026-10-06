using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Grouping;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public class BaselineAndPlanTests
{
    private const string DiscordPath = @"C:\Users\me\AppData\Local\Discord\app-1.0.9165\Discord.exe";
    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string TelegramPath = @"C:\Users\me\AppData\Roaming\Telegram Desktop\Telegram.exe";

    private static readonly AppGrouper Grouper = new(new ProcessClassifier());

    private static List<ProcessInfo> AtBaseline() =>
    [
        .. FreshSession(),
        App(2000, ChromePath, 5, parent: 1100, visibleWindows: 1, description: "Google Chrome"),
        App(2001, ChromePath, 5.1, parent: 2000),
        App(3000, DiscordPath, 3, parent: 1100, hiddenWindows: 1, description: "Discord"),
    ];

    private static (Baseline Baseline, SystemSnapshot Snapshot) MakeBaseline(List<ProcessInfo> processes)
    {
        var snap = Snapshot(processes, at: Boot.AddMinutes(10));
        return (BaselineFactory.Create(snap, Grouper.Group(snap), BaselineOrigin.Manual, "PC"), snap);
    }

    private static (ComparisonResult Comparison, IReadOnlyList<AppGroup> Groups) Compare(Baseline baseline, List<ProcessInfo> now, SessionContext? ctx = null)
    {
        var snap = Snapshot(now, ctx);
        var groups = Grouper.Group(snap);
        return (BaselineComparer.Compare(baseline, groups, snap.Context), groups);
    }

    private static CleanupPlan Plan(Baseline baseline, List<ProcessInfo> now, PlannerOptions? options = null)
    {
        var (cmp, groups) = Compare(baseline, now);
        return new CleanupPlanner().Build(groups, cmp, options ?? new PlannerOptions());
    }

    private static AppGroup G(IReadOnlyList<AppGroup> groups, int pid) => groups.Single(g => g.Members.Any(m => m.Pid == pid));

    [Fact]
    public void Baseline_describes_applications_not_pids()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var chrome = baseline.Apps.Single(a => a.DisplayName == "Google Chrome");
        Assert.Equal(2, chrome.ProcessCount);
        Assert.Single(chrome.Instances);
        Assert.Equal(User, baseline.UserSid);
        Assert.Contains(baseline.Apps, a => a.WasProtected && a.AppKey.EndsWith("explorer.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void Same_instances_are_in_baseline_and_new_children_do_not_matter()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var now = AtBaseline();
        // Chrome opened 30 more tabs since the baseline.
        for (var i = 0; i < 30; i++)
        {
            now.Add(App(2100 + i, ChromePath, 100 + i, parent: 2000));
        }

        var (cmp, groups) = Compare(baseline, now);
        Assert.True(cmp.IsSameBoot);
        Assert.Equal(GroupStatus.InBaseline, cmp.For(G(groups, 2000)).Status);
        Assert.Equal(32, G(groups, 2000).ProcessCount);

        var plan = new CleanupPlanner().Build(groups, cmp, new PlannerOptions());
        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Kept, i => i.Group.Root.Pid == 2000 && i.KeepReason == KeepReason.InBaseline);
    }

    [Fact]
    public void Restarted_baseline_app_is_still_part_of_the_baseline()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var now = AtBaseline().Where(p => p.Pid != 3000).ToList();
        now.Add(App(3500, @"C:\Users\me\AppData\Local\Discord\app-1.0.9170\Discord.exe", 120, parent: 1100, visibleWindows: 1));

        var (cmp, groups) = Compare(baseline, now);
        Assert.Equal(GroupStatus.RestartedSinceBaseline, cmp.For(G(groups, 3500)).Status);
    }

    [Fact]
    public void Additional_instance_of_a_baseline_app_is_new_with_caution()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var now = AtBaseline();
        now.Add(App(2500, ChromePath, 130, parent: 1100, visibleWindows: 1));

        var plan = Plan(baseline, now);
        var item = Assert.Single(plan.Candidates);
        Assert.Equal(2500, item.Group.Root.Pid);
        Assert.Equal(GroupStatus.NewInstanceOfBaselineApp, item.Comparison!.Status);
        Assert.Contains(RiskReason.NewInstanceOfBaselineApp, item.Reasons);
        Assert.False(item.DefaultSelected);
    }

    [Fact]
    public void New_apps_are_candidates_and_safe_ones_are_preselected()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var now = AtBaseline();
        now.Add(App(4000, TelegramPath, 100, parent: 1100, visibleWindows: 1, description: "Telegram Desktop"));
        now.Add(App(4100, @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", 101, parent: 1100, visibleWindows: 1));
        now.Add(App(4200, @"C:\Python312\python.exe", 102, parent: 1100, subsystem: ImageSubsystem.Console));

        var plan = Plan(baseline, now);
        Assert.Equal(3, plan.Candidates.Count);

        var telegram = plan.Candidates.Single(c => c.Group.Root.Pid == 4000);
        Assert.Equal(SafetyLevel.Safe, telegram.Level);
        Assert.True(telegram.DefaultSelected);

        var code = plan.Candidates.Single(c => c.Group.Root.Pid == 4100);
        Assert.Equal(SafetyLevel.Caution, code.Level);
        Assert.False(code.DefaultSelected);

        var python = plan.Candidates.Single(c => c.Group.Root.Pid == 4200);
        Assert.Equal(SafetyLevel.ManualReview, python.Level);
        Assert.False(python.DefaultSelected);

        // Ordered by level: safe first.
        Assert.Equal(4000, plan.Candidates[0].Group.Root.Pid);
    }

    [Fact]
    public void Preselecting_caution_never_preselects_manual_review()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var now = AtBaseline();
        now.Add(App(4100, @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", 101, parent: 1100, visibleWindows: 1));
        now.Add(App(4200, @"C:\Python312\python.exe", 102, parent: 1100, subsystem: ImageSubsystem.Console));

        var plan = Plan(baseline, now, new PlannerOptions { PreselectCaution = true });
        Assert.True(plan.Candidates.Single(c => c.Group.Root.Pid == 4100).DefaultSelected);
        Assert.False(plan.Candidates.Single(c => c.Group.Root.Pid == 4200).DefaultSelected);
    }

    [Fact]
    public void Keep_list_wins_over_new()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var now = AtBaseline();
        var telegram = App(4000, TelegramPath, 100, parent: 1100, visibleWindows: 1);
        now.Add(telegram);

        var plan = Plan(baseline, now, new PlannerOptions { KeepAppKeys = new HashSet<string> { AppKeys.For(telegram) } });
        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Kept, k => k.KeepReason == KeepReason.UserKeepList);
    }

    [Fact]
    public void Protected_apps_never_become_candidates()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var now = AtBaseline();
        now.Add(App(4300, @"C:\Windows\System32\dllhost.exe", 100, parent: 1100, visibleWindows: 1));
        now.Add(App(4400, @"C:\Apps\Admin\admintool.exe", 100, parent: 1100, visibleWindows: 1) with { IsElevated = true });
        now.Add(App(4500, @"C:\Apps\Other\other.exe", 100, parent: 1100, visibleWindows: 1) with { OwnerSid = OtherUser });

        var plan = Plan(baseline, now);
        Assert.Empty(plan.Candidates);
        Assert.All(plan.Protected, p => Assert.False(p.IsSelectable));
        Assert.Contains(plan.Protected, p => p.Group.Root.Pid == 4400);
    }

    [Fact]
    public void Apps_started_by_a_baseline_app_get_caution_and_a_reason()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var now = AtBaseline();
        now.Add(App(4600, @"C:\Games\SomeGame\game.exe", 100, parent: 3000, visibleWindows: 1));

        var plan = Plan(baseline, now);
        var game = Assert.Single(plan.Candidates);
        Assert.Equal("Discord", game.Comparison!.StartedByBaselineApp);
        Assert.Contains(RiskReason.StartedByBaselineApp, game.Reasons);
        Assert.Equal(SafetyLevel.Caution, game.Level);
        Assert.Contains("Started by Discord", game.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Apps_started_by_explorer_are_not_flagged_as_started_by_baseline()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var now = AtBaseline();
        now.Add(App(4000, TelegramPath, 100, parent: 1100, visibleWindows: 1));

        var (cmp, groups) = Compare(baseline, now);
        Assert.Null(cmp.For(G(groups, 4000)).StartedByBaselineApp);
    }

    [Fact]
    public void Baseline_from_a_previous_boot_matches_by_application()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var nextBoot = Boot.AddDays(1);
        var ctx = Context() with { BootTimeUtc = nextBoot };
        var now = new List<ProcessInfo>
        {
            App(1100, @"C:\Windows\explorer.exe", 60 * 24 + 1, visibleWindows: 1),
            App(7000, ChromePath, 60 * 24 + 5, parent: 1100, visibleWindows: 1),
            App(7100, TelegramPath, 60 * 24 + 6, parent: 1100, visibleWindows: 1),
        };

        var (cmp, groups) = Compare(baseline, now, ctx);
        Assert.False(cmp.IsSameBoot);
        Assert.Equal(GroupStatus.RestartedSinceBaseline, cmp.For(G(groups, 7000)).Status);
        Assert.Equal(GroupStatus.New, cmp.For(G(groups, 7100)).Status);
        Assert.Contains(cmp.ClosedSinceBaseline, a => a.DisplayName == "Discord");
    }

    [Fact]
    public void Baseline_of_another_user_is_rejected()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        Assert.Null(BaselineFactory.Validate(baseline, Context()));
        Assert.NotNull(BaselineFactory.Validate(baseline with { UserSid = OtherUser }, Context()));
        Assert.NotNull(BaselineFactory.Validate(baseline with { SchemaVersion = 99 }, Context()));
    }

    [Fact]
    public void Close_steps_follow_the_strategy_and_never_include_termination()
    {
        var (baseline, _) = MakeBaseline(AtBaseline());
        var now = AtBaseline();
        now.Add(App(4000, TelegramPath, 100, parent: 1100, visibleWindows: 1));
        now.Add(App(4001, @"C:\Windows\System32\conhost.exe", 100.01, parent: 4000));
        now.Add(App(4100, @"C:\Apps\Tray\tray.exe", 100, parent: 1100, hiddenWindows: 1));
        now.Add(App(4200, @"C:\Python312\python.exe", 102, parent: 1100, subsystem: ImageSubsystem.Console));
        now.Add(App(4300, @"C:\Apps\Daemon\daemon.exe", 103, parent: 1100));

        var plan = Plan(baseline, now);
        StepKind[] Kinds(int pid) => plan.Candidates.Single(c => c.Group.Root.Pid == pid).Steps.Select(s => s.Kind).ToArray();

        Assert.Equal([StepKind.CloseWindows, StepKind.LeftAlone], Kinds(4000));
        Assert.Equal([StepKind.RestartManager], Kinds(4100));
        Assert.Equal([StepKind.ConsoleInterrupt], Kinds(4200));
        Assert.Equal([StepKind.NoGracefulMethod], Kinds(4300));
        Assert.All(plan.Candidates, c => Assert.Contains(c.Notes, n => n.Contains("force-terminated", StringComparison.Ordinal)));
    }
}
