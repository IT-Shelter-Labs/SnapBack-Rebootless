using Microsoft.Extensions.Logging.Abstractions;
using SnapBack.Core.Classification;
using SnapBack.Core.Execution;
using SnapBack.Core.Grouping;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
using SnapBack.Core.Safety;
using SnapBack.Core.Tests.Fakes;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public class CleanupOperationTests
{
    private static readonly CleanupOptions FastOptions = new()
    {
        GracefulTimeout = TimeSpan.FromMilliseconds(800),
        PollInterval = TimeSpan.FromMilliseconds(10),
        EscalationDelay = TimeSpan.FromMilliseconds(50),
        LeftoverGrace = TimeSpan.FromMilliseconds(100),
        RestartManagerDrainTimeout = TimeSpan.FromMilliseconds(200),
    };

    private static readonly ProcessInfo Telegram = App(4000, @"C:\Apps\Telegram\Telegram.exe", 100, parent: 1100, visibleWindows: 1, description: "Telegram");
    private static readonly ProcessInfo Discord = App(4100, @"C:\Apps\Discord\Discord.exe", 100, parent: 1100, visibleWindows: 1, description: "Discord");
    private static readonly ProcessInfo DiscordHelper = App(4101, @"C:\Apps\Discord\Discord.exe", 100.1, parent: 4100);
    private static readonly ProcessInfo Notepad = App(4200, @"C:\Apps\Editor\editor.exe", 100, parent: 1100, visibleWindows: 1, titles: "*notes.txt - Editor");
    private static readonly ProcessInfo Python = App(4300, @"C:\Python312\python.exe", 100, parent: 1100, subsystem: ImageSubsystem.Console);
    private static readonly ProcessInfo Daemon = App(4400, @"C:\Apps\Daemon\daemon.exe", 100, parent: 1100);

    private static (FakeSystem System, List<PlanItem> Items, CleanupOperation Operation) Setup(params ProcessInfo[] apps)
    {
        var system = new FakeSystem([.. FreshSession(), .. apps]);
        var groups = new AppGrouper(new ProcessClassifier()).Group(system.Capture());
        var roots = apps.Select(a => a.Pid).ToHashSet();
        var items = groups.Where(g => roots.Contains(g.Root.Pid)).Select(g => Item(g)).ToList();
        return (system, items, Operation(system, items));
    }

    private static PlanItem Item(AppGroup g, PlanCategory category = PlanCategory.Candidate) => new()
    {
        Group = g,
        Category = g.IsProtected ? PlanCategory.Protected : category,
        Level = g.Assessment.Level,
        Reasons = g.Assessment.Reasons,
        Steps = CloseStrategy.Plan(g),
        Notes = [],
    };

    private static CleanupOperation Operation(FakeSystem system, IEnumerable<PlanItem> items) =>
        new(items, system.Context, system, system, new SafetyGuard(new ProcessClassifier()), FastOptions, TimeProvider.System, NullLogger.Instance);

    private static ItemResult Result(OperationReport r, int rootPid) => r.Items.Single(i => i.Item.Group.Root.Pid == rootPid);

    [Fact]
    public async Task Closes_a_window_app_politely_and_reports_freed_memory()
    {
        var (system, _, op) = Setup(Discord, DiscordHelper);
        var report = await op.RunGracefulAsync();

        var r = Result(report, 4100);
        Assert.Equal(ItemOutcome.Closed, r.Outcome);
        Assert.Equal(2, r.ProcessesExited);
        Assert.Equal(200L * 1024 * 1024, r.MemoryFreedBytes);
        Assert.Contains("close 4100", system.Calls);
        Assert.Equal(0, system.TerminateCalls);
        Assert.Equal(1, report.ClosedCount);
    }

    [Fact]
    public async Task Tray_app_is_asked_again_through_restart_manager()
    {
        var (system, _, op) = Setup(Discord);
        system[4100] = Behavior.HideToTrayThenExitOnRestartManager;

        var r = Result(await op.RunGracefulAsync(), 4100);
        Assert.Equal(ItemOutcome.Closed, r.Outcome);
        Assert.Contains(system.Calls, c => c == "rm 4100");
        Assert.Equal(0, system.TerminateCalls);
    }

    [Fact]
    public async Task App_waiting_for_the_user_is_never_forced()
    {
        var (system, _, op) = Setup(Notepad);
        system[4200] = Behavior.PromptsUser;

        var r = Result(await op.RunGracefulAsync(), 4200);
        Assert.Equal(ItemOutcome.StillRunning, r.Outcome);
        Assert.Equal(StillRunningReason.WaitingForUser, r.Reason);
        Assert.True(r.CanForce);
        Assert.True(system.IsAlive(4200));
        Assert.Equal(0, system.TerminateCalls);
        Assert.DoesNotContain(system.Calls, c => c.StartsWith("rm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Declined_restart_manager_request_is_reported_not_forced()
    {
        var (system, _, op) = Setup(Discord);
        system[4100] = Behavior.DeclinesRestartManager;

        var r = Result(await op.RunGracefulAsync(), 4100);
        Assert.Equal(StillRunningReason.Declined, r.Reason);
        Assert.Equal(0, system.TerminateCalls);
    }

    [Fact]
    public async Task Windowless_process_has_no_graceful_method_and_is_left_running()
    {
        var (system, _, op) = Setup(Daemon);
        var r = Result(await op.RunGracefulAsync(), 4400);
        Assert.Equal(ItemOutcome.StillRunning, r.Outcome);
        Assert.Equal(StillRunningReason.NoGracefulMethod, r.Reason);
        Assert.Empty(system.Calls);
        Assert.True(system.IsAlive(4400));
    }

    [Fact]
    public async Task Console_program_gets_ctrl_c()
    {
        var (system, _, op) = Setup(Python);
        system[4300] = Behavior.ExitOnCtrlC;
        system.ConsoleSharers[4300] = [(5555, "cmd.exe")];

        var r = Result(await op.RunGracefulAsync(), 4300);
        Assert.Equal(ItemOutcome.Closed, r.Outcome);
        Assert.Contains("ctrlc 4300", system.Calls);
    }

    [Fact]
    public async Task Ctrl_c_is_not_sent_to_a_console_shared_with_unrelated_programs()
    {
        var (system, _, op) = Setup(Python);
        system[4300] = Behavior.ExitOnCtrlC;
        system.ConsoleSharers[4300] = [(5555, "cmd.exe"), (5556, "important-job.exe")];

        var r = Result(await op.RunGracefulAsync(), 4300);
        Assert.Equal(ItemOutcome.StillRunning, r.Outcome);
        Assert.Equal(StillRunningReason.SharedConsole, r.Reason);
        Assert.True(system.IsAlive(4300));
    }

    [Fact]
    public async Task Process_that_exited_before_the_operation_is_reported_as_such()
    {
        var (system, _, op) = Setup(Telegram);
        system.Kill(4000);

        var r = Result(await op.RunGracefulAsync(), 4000);
        Assert.Equal(ItemOutcome.AlreadyExited, r.Outcome);
        Assert.Empty(system.Calls);
    }

    [Fact]
    public async Task Recycled_pid_is_never_acted_on()
    {
        var (system, _, op) = Setup(Telegram);
        system.Kill(4000);
        var impostor = App(4000, @"C:\Apps\Other\other.exe", 150, parent: 1100, visibleWindows: 1);
        system.Replace(impostor);

        var r = Result(await op.RunGracefulAsync(), 4000);
        Assert.Equal(ItemOutcome.AlreadyExited, r.Outcome);
        Assert.Empty(system.Calls);
        Assert.True(system.IsAlive(4000));
    }

    [Fact]
    public async Task Process_that_became_protected_is_skipped()
    {
        var (system, _, op) = Setup(Telegram);
        system.Replace(Telegram with { IsElevated = true });

        var r = Result(await op.RunGracefulAsync(), 4000);
        Assert.Equal(ItemOutcome.Skipped, r.Outcome);
        Assert.Empty(system.Calls);
    }

    [Fact]
    public async Task Protected_or_kept_items_from_a_buggy_UI_are_refused()
    {
        var system = new FakeSystem([.. FreshSession(), Telegram]);
        var groups = new AppGrouper(new ProcessClassifier()).Group(system.Capture());
        var explorer = Item(groups.Single(g => g.Root.Pid == 1100));
        var kept = Item(groups.Single(g => g.Root.Pid == 4000), PlanCategory.Kept);
        var forgedProtected = new PlanItem
        {
            Group = groups.Single(g => g.Root.Pid == 1100),
            Category = PlanCategory.Candidate,
            Level = SafetyLevel.Safe,
            Reasons = [],
            Steps = [],
            Notes = [],
        };

        var report = await Operation(system, [explorer, kept, forgedProtected]).RunGracefulAsync();
        Assert.All(report.Items, i => Assert.Equal(ItemOutcome.Skipped, i.Outcome));
        Assert.Empty(system.Calls);
        Assert.True(system.IsAlive(1100));
    }

    [Fact]
    public async Task A_failing_process_does_not_stop_the_others()
    {
        var (system, _, op) = Setup(Telegram, Discord);
        system[4000] = Behavior.Ignores;
        system.CloseOverride = t => t.Identity.Pid == 4000
            ? throw new InvalidOperationException("boom")
            : FakeClose(system, t);

        var report = await op.RunGracefulAsync();
        Assert.Equal(ItemOutcome.Closed, Result(report, 4100).Outcome);
        Assert.NotEqual(ItemOutcome.Closed, Result(report, 4000).Outcome);
        Assert.Contains(Result(report, 4000).Notes, n => n.Contains("boom", StringComparison.Ordinal));
    }

    private static ActionResult FakeClose(FakeSystem system, ProcessTarget t)
    {
        system.Kill(t.Identity.Pid);
        return new ActionResult(ActionStatus.Done, "ok", 1);
    }

    [Fact]
    public async Task Cancellation_stops_waiting_without_forcing()
    {
        var (system, _, op) = Setup(Notepad);
        system[4200] = Behavior.PromptsUser;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var r = Result(await op.RunGracefulAsync(cancellationToken: cts.Token), 4200);
        Assert.Equal(StillRunningReason.Cancelled, r.Reason);
        Assert.Equal(0, system.TerminateCalls);
    }

    [Fact]
    public async Task Recheck_notices_apps_closed_by_the_user_later()
    {
        var (system, _, op) = Setup(Notepad);
        system[4200] = Behavior.PromptsUser;
        await op.RunGracefulAsync();

        system.Kill(4200); // the user answered the save prompt
        var r = op.Recheck().Single();
        Assert.Equal(ItemOutcome.Closed, r.Outcome);
    }

    [Fact]
    public async Task Force_is_impossible_before_the_graceful_phase()
    {
        var (_, items, op) = Setup(Notepad);
        var auth = ForceAuthorization.FromUserConfirmation([Notepad.Identity], DateTime.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => op.ForceTerminateAsync(auth, [items[0].Group.Id]));
    }

    [Fact]
    public async Task Force_requires_authorization_covering_the_process()
    {
        var (system, items, op) = Setup(Notepad);
        system[4200] = Behavior.PromptsUser;
        await op.RunGracefulAsync();

        var wrong = ForceAuthorization.FromUserConfirmation([Telegram.Identity], DateTime.UtcNow);
        var r = Result(await op.ForceTerminateAsync(wrong, [items[0].Group.Id]), 4200);
        Assert.Equal(0, system.TerminateCalls);
        Assert.True(system.IsAlive(4200));
        Assert.True(r.CanForce);
    }

    [Fact]
    public async Task Force_terminates_only_what_the_user_confirmed()
    {
        var (system, items, op) = Setup(Notepad, Discord);
        system[4200] = Behavior.PromptsUser;
        system[4100] = Behavior.PromptsUser;
        await op.RunGracefulAsync();

        var notepadItem = items.Single(i => i.Group.Root.Pid == 4200);
        var auth = ForceAuthorization.FromUserConfirmation(notepadItem.Group.ActionableMembers.Select(m => m.Identity), DateTime.UtcNow);
        var report = await op.ForceTerminateAsync(auth, [notepadItem.Group.Id]);

        Assert.Equal(ItemOutcome.Terminated, Result(report, 4200).Outcome);
        Assert.Equal(["terminate 4200"], system.Calls.Where(c => c.StartsWith("terminate", StringComparison.Ordinal)));
        Assert.True(system.IsAlive(4100));
    }

    [Fact]
    public async Task Force_re_checks_protection_and_identity()
    {
        var (system, items, op) = Setup(Notepad);
        system[4200] = Behavior.PromptsUser;
        await op.RunGracefulAsync();

        system.Replace(Notepad with { IsCritical = true });
        var auth = ForceAuthorization.FromUserConfirmation([Notepad.Identity], DateTime.UtcNow);
        var r = Result(await op.ForceTerminateAsync(auth, [items[0].Group.Id]), 4200);

        Assert.Equal(0, system.TerminateCalls);
        Assert.Equal(ItemOutcome.Failed, r.Outcome);
    }

    [Fact]
    public async Task Graceful_phase_runs_only_once()
    {
        var (_, _, op) = Setup(Telegram);
        await op.RunGracefulAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => op.RunGracefulAsync());
    }

    [Fact]
    public void Dry_run_never_touches_anything()
    {
        var (system, items, _) = Setup(Telegram, Python, Daemon);
        system.Replace(Daemon with { IsElevated = true });

        var report = CleanupOperation.DryRun(items, system.Context, system, new SafetyGuard(new ProcessClassifier()), DateTime.UtcNow);
        Assert.Empty(system.Calls);
        Assert.True(report.Items.Single(i => i.Item.Group.Root.Pid == 4000).ItemDecision.Allowed);
        var daemon = report.Items.Single(i => i.Item.Group.Root.Pid == 4400);
        Assert.False(daemon.ItemDecision.Allowed);
        Assert.Equal(GuardVerdict.ProtectedNow, daemon.ItemDecision.Verdict);
        Assert.Equal(2, report.WouldActOn);
    }
}
