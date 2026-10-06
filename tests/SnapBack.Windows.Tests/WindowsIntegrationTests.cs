using Microsoft.Extensions.Logging.Abstractions;
using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Execution;
using SnapBack.Core.Grouping;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
using SnapBack.Core.Safety;

namespace SnapBack.Windows.Tests;

/// <summary>
/// Runs against the real operating system. Only ever acts on the test target app it starts itself.
/// </summary>
[Collection("Windows integration")]
public class WindowsIntegrationTests
{
    private static WindowsSystemProbe Probe() => new(NullLogger<WindowsSystemProbe>.Instance);

    private static WindowsProcessController Controller(SessionContext ctx) => new(ctx, NullLogger<WindowsProcessController>.Instance);

    [WindowsOnlyFact]
    public void Snapshot_contains_this_process_with_a_full_identity()
    {
        var snapshot = Probe().Capture();
        var self = snapshot.Processes[Environment.ProcessId];
        Assert.True(self.Identity.CreationTime > 0);
        Assert.True(self.CouldOpen);
        Assert.Equal(snapshot.Context.UserSid, self.OwnerSid);
        Assert.NotNull(self.ImagePath);
        Assert.True(new ProcessClassifier().Classify(self, snapshot.Context).IsProtected);
    }

    [WindowsOnlyFact]
    public void System_and_session_processes_are_classified_as_protected()
    {
        var snapshot = Probe().Capture();
        var classifier = new ProcessClassifier();
        string[] critical = ["System", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "svchost.exe", "dwm.exe", "explorer.exe"];

        var found = snapshot.Processes.Values.Where(p => critical.Contains(p.ImageName, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(found);
        Assert.All(found, p => Assert.True(classifier.Classify(p, snapshot.Context).IsProtected, $"{p.ImageName} must be protected"));
    }

    [WindowsOnlyFact]
    public void Controller_refuses_system_processes_and_itself()
    {
        var probe = Probe();
        var snapshot = probe.Capture();
        var controller = Controller(snapshot.Context);
        var targets = snapshot.Processes.Values
            .Where(p => p.Pid == 4 || p.Pid == Environment.ProcessId
                || p.ImageName.Equals("csrss.exe", StringComparison.OrdinalIgnoreCase)
                || p.ImageName.Equals("winlogon.exe", StringComparison.OrdinalIgnoreCase)
                || p.ImageName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(targets);
        foreach (var p in targets)
        {
            var target = ProcessTarget.From(p);
            Assert.NotEqual(ActionStatus.Done, controller.Terminate(target).Status);
            Assert.NotEqual(ActionStatus.Done, controller.RequestCloseWindows(target).Status);
        }

        Assert.All(targets, p => Assert.NotEqual(ProcessState.Exited, controller.GetState(p.Identity)));
    }

    [WindowsOnlyFact]
    public void Controller_refuses_a_stale_identity()
    {
        using var target = TestTargetProcess.Start("normal");
        var probe = Probe();
        var info = target.WaitForWindow(probe);
        var controller = Controller(probe.BaseContext);

        var stale = new ProcessTarget(new ProcessIdentity(info.Pid, info.Identity.CreationTime - 1), info.ImageName, info.ImagePath);
        Assert.Equal(ActionStatus.IdentityMismatch, controller.Terminate(stale).Status);
        Assert.Equal(ProcessState.Exited, controller.GetState(stale.Identity));
        Assert.False(target.HasExited);
    }

    [WindowsOnlyFact]
    public void Elevated_app_is_protected_and_refused()
    {
        var probe = Probe();
        if (!probe.BaseContext.SelfElevated)
        {
            return; // needs an elevated host to start an elevated target
        }

        using var target = TestTargetProcess.Start("normal");
        target.WaitForWindow(probe);
        var snapshot = probe.Capture();
        var group = new AppGrouper(new ProcessClassifier()).Group(snapshot).Single(g => g.Members.Any(m => m.Pid == target.Pid));
        Assert.True(group.IsProtected);
        Assert.Contains(RiskReason.Elevated, group.Assessment.Reasons);

        var controller = Controller(snapshot.Context);
        Assert.Equal(ActionStatus.Refused, controller.Terminate(ProcessTarget.From(group.Root)).Status);
        Assert.Equal(ActionStatus.Refused, controller.RequestCloseWindows(ProcessTarget.From(group.Root)).Status);
        Assert.False(target.HasExited);
    }

    [WindowsOnlyFact]
    public async Task Graceful_close_closes_a_normal_window_app()
    {
        using var target = StartTarget("normal");
        var (item, operation) = Prepare(target);

        var report = await operation.RunGracefulAsync();
        Assert.Equal(ItemOutcome.Closed, report.Items.Single().Outcome);
        Assert.True(target.WaitForExit(TimeSpan.FromSeconds(5)));
    }

    [WindowsOnlyFact]
    public async Task App_that_refuses_is_reported_and_only_force_terminated_after_authorization()
    {
        using var target = StartTarget("prompt");
        var (item, operation) = Prepare(target);

        var report = await operation.RunGracefulAsync();
        var result = report.Items.Single();
        Assert.Equal(ItemOutcome.StillRunning, result.Outcome);
        Assert.Equal(StillRunningReason.WaitingForUser, result.Reason);
        Assert.False(target.HasExited);

        var auth = ForceAuthorization.FromUserConfirmation(result.RemainingProcesses.Select(p => p.Identity), DateTime.UtcNow);
        var forced = await operation.ForceTerminateAsync(auth, [item.Group.Id]);
        Assert.Equal(ItemOutcome.Terminated, forced.Items.Single().Outcome);
        Assert.True(target.WaitForExit(TimeSpan.FromSeconds(5)));
    }

    [WindowsOnlyFact]
    public async Task Tray_app_is_asked_through_restart_manager_and_never_killed()
    {
        using var target = StartTarget("tray");
        var (_, operation) = Prepare(target, recording: true);

        var report = await operation.RunGracefulAsync();
        var result = report.Items.Single();
        Assert.NotEqual(ItemOutcome.Terminated, result.Outcome);
        Assert.Equal(ItemOutcome.Closed, result.Outcome);
        Assert.Contains(Recorder!.Calls, c => c.StartsWith("rm", StringComparison.Ordinal));
        Assert.DoesNotContain(Recorder.Calls, c => c.StartsWith("terminate", StringComparison.Ordinal));
    }

    private RecordingController? Recorder { get; set; }

    private static TestTargetProcess StartTarget(string mode) =>
        TestTargetProcess.StartAsNormalUser(mode, Probe().BaseContext.SelfElevated);

    private (PlanItem Item, CleanupOperation Operation) Prepare(TestTargetProcess target, bool recording = false)
    {
        var probe = Probe();
        target.WaitForWindow(probe);
        var snapshot = probe.Capture();
        var groups = new AppGrouper(new ProcessClassifier()).Group(snapshot);
        var group = groups.Single(g => g.Members.Any(m => m.Pid == target.Pid));
        Assert.False(group.IsProtected, $"The test target must be an ordinary user app: {string.Join(", ", group.Assessment.Reasons)} "
            + $"(elevated={group.Root.IsElevated}, integrity={group.Root.Integrity}, owner={group.Root.OwnerSid}, host elevated={snapshot.Context.SelfElevated})");

        var emptyBaseline = new Baseline { UserSid = snapshot.Context.UserSid, BootTimeUtc = snapshot.Context.BootTimeUtc, CreatedAtUtc = DateTime.UtcNow };
        var comparison = BaselineComparer.Compare(emptyBaseline, groups, snapshot.Context);
        var plan = new CleanupPlanner().Build(groups, comparison, new PlannerOptions());
        var controller = Controller(snapshot.Context);

        var item = plan.Candidates.Single(i => i.Group.Id == group.Id);
        IProcessController effective = controller;
        if (recording)
        {
            Recorder = new RecordingController(controller);
            effective = Recorder;
        }

        var options = new CleanupOptions { GracefulTimeout = TimeSpan.FromSeconds(8) };
        var operation = new CleanupOperation([item], snapshot.Context, probe, effective, new SafetyGuard(new ProcessClassifier()), options,
            TimeProvider.System, NullLogger.Instance);
        return (item, operation);
    }

    private sealed class RecordingController(IProcessController inner) : IProcessController
    {
        public List<string> Calls { get; } = [];

        public ActionResult RequestCloseWindows(ProcessTarget target)
        {
            Calls.Add("close");
            return inner.RequestCloseWindows(target);
        }

        public ActionResult InterruptConsole(ProcessTarget target, Func<int, string?, bool> isAllowedSharer)
        {
            Calls.Add("ctrlc");
            return inner.InterruptConsole(target, isAllowedSharer);
        }

        public Task<ActionResult> ShutdownGracefullyAsync(IReadOnlyList<ProcessTarget> targets, CancellationToken cancellationToken)
        {
            Calls.Add("rm");
            return inner.ShutdownGracefullyAsync(targets, cancellationToken);
        }

        public ProcessState GetState(ProcessIdentity identity) => inner.GetState(identity);

        public IReadOnlyDictionary<int, WindowSummary> SnapshotWindows() => inner.SnapshotWindows();

        public ActionResult Terminate(ProcessTarget target)
        {
            Calls.Add("terminate");
            return inner.Terminate(target);
        }
    }
}
