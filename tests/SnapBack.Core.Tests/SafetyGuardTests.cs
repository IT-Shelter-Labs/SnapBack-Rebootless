using SnapBack.Core.Classification;
using SnapBack.Core.Grouping;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
using SnapBack.Core.Safety;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public class SafetyGuardTests
{
    private static readonly SafetyGuard Guard = new(new ProcessClassifier());
    private static readonly DateTime Now = Boot.AddHours(3);

    private static readonly ProcessInfo Telegram =
        App(4000, @"C:\Users\me\AppData\Roaming\Telegram Desktop\Telegram.exe", 100, parent: 1100, visibleWindows: 1);

    internal static PlanItem ItemFor(ProcessInfo root, PlanCategory category = PlanCategory.Candidate, SafetyLevel level = SafetyLevel.Safe)
    {
        var group = new AppGrouper(new ProcessClassifier()).Group(Snapshot([.. FreshSession(), root])).Single(g => g.Root.Pid == root.Pid);
        return new PlanItem
        {
            Group = group,
            Category = category,
            Level = level,
            Reasons = group.Assessment.Reasons,
            Steps = CloseStrategy.Plan(group),
            Notes = [],
        };
    }

    [Fact]
    public void Allows_an_unchanged_safe_process()
    {
        var d = Guard.CheckGraceful(ItemFor(Telegram), Telegram, Telegram, Context());
        Assert.True(d.Allowed);
    }

    [Theory]
    [InlineData(PlanCategory.Kept, SafetyLevel.Safe)]
    [InlineData(PlanCategory.Protected, SafetyLevel.Protected)]
    [InlineData(PlanCategory.Candidate, SafetyLevel.Protected)]
    public void Refuses_items_that_are_not_candidates_even_if_the_UI_passes_them(PlanCategory category, SafetyLevel level)
    {
        var d = Guard.CheckGraceful(ItemFor(Telegram, category, level), Telegram, Telegram, Context());
        Assert.Equal(GuardVerdict.ItemNotEligible, d.Verdict);
    }

    [Fact]
    public void Refuses_a_process_that_is_not_part_of_the_item()
    {
        var stranger = App(4999, @"C:\Apps\x.exe", 100);
        Assert.Equal(GuardVerdict.ItemNotEligible, Guard.CheckGraceful(ItemFor(Telegram), stranger, stranger, Context()).Verdict);
    }

    [Fact]
    public void Detects_exit_and_pid_reuse()
    {
        Assert.Equal(GuardVerdict.ProcessExited, Guard.CheckGraceful(ItemFor(Telegram), Telegram, null, Context()).Verdict);

        var reused = Telegram with { Identity = new ProcessIdentity(Telegram.Pid, At(200)) };
        Assert.Equal(GuardVerdict.IdentityMismatch, Guard.CheckGraceful(ItemFor(Telegram), Telegram, reused, Context()).Verdict);
    }

    [Fact]
    public void Detects_a_changed_executable()
    {
        var changed = Telegram with { ImagePath = @"C:\Apps\Other\Telegram.exe" };
        Assert.Equal(GuardVerdict.PathChanged, Guard.CheckGraceful(ItemFor(Telegram), Telegram, changed, Context()).Verdict);
    }

    [Theory]
    [InlineData("elevated")]
    [InlineData("critical")]
    [InlineData("service")]
    [InlineData("owner")]
    public void Re_checks_protection_on_the_live_process(string change)
    {
        var live = change switch
        {
            "elevated" => Telegram with { IsElevated = true },
            "critical" => Telegram with { IsCritical = true },
            "service" => Telegram with { IsServiceHost = true },
            _ => Telegram with { OwnerSid = null },
        };

        Assert.Equal(GuardVerdict.ProtectedNow, Guard.CheckGraceful(ItemFor(Telegram), Telegram, live, Context()).Verdict);
    }

    [Fact]
    public void Self_and_kernel_pids_are_refused_without_consulting_anything_else()
    {
        var self = App(SelfPid, @"C:\Apps\Fake\fake.exe", 100, visibleWindows: 1);
        var item = ItemFor(self);
        Assert.False(Guard.CheckGraceful(item, self, self, Context()).Allowed);
    }

    [Fact]
    public void Force_requires_authorization_for_that_exact_process()
    {
        var item = ItemFor(Telegram);
        Assert.Equal(GuardVerdict.NotAuthorized, Guard.CheckForce(item, Telegram, Telegram, Context(), null, true, Now).Verdict);

        var otherInstance = ForceAuthorization.FromUserConfirmation([new ProcessIdentity(Telegram.Pid, At(1))], Now);
        Assert.Equal(GuardVerdict.NotAuthorized, Guard.CheckForce(item, Telegram, Telegram, Context(), otherInstance, true, Now).Verdict);

        var auth = ForceAuthorization.FromUserConfirmation([Telegram.Identity], Now);
        Assert.True(Guard.CheckForce(item, Telegram, Telegram, Context(), auth, true, Now).Allowed);
    }

    [Fact]
    public void Force_authorization_expires()
    {
        var auth = ForceAuthorization.FromUserConfirmation([Telegram.Identity], Now);
        var later = Now + ForceAuthorization.Lifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(GuardVerdict.AuthorizationExpired, Guard.CheckForce(ItemFor(Telegram), Telegram, Telegram, Context(), auth, true, later).Verdict);
    }

    [Fact]
    public void Force_requires_a_graceful_attempt_first()
    {
        var auth = ForceAuthorization.FromUserConfirmation([Telegram.Identity], Now);
        Assert.Equal(GuardVerdict.GracefulNotAttempted, Guard.CheckForce(ItemFor(Telegram), Telegram, Telegram, Context(), auth, false, Now).Verdict);
    }

    [Fact]
    public void Force_still_refuses_protected_processes()
    {
        var auth = ForceAuthorization.FromUserConfirmation([Telegram.Identity], Now);
        var live = Telegram with { IsCritical = true };
        Assert.Equal(GuardVerdict.ProtectedNow, Guard.CheckForce(ItemFor(Telegram), Telegram, live, Context(), auth, true, Now).Verdict);
    }
}
