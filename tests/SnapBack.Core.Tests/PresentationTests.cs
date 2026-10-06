using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Grouping;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
using SnapBack.Core.Presentation;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public class PresentationTests
{
    private static CleanupPlan PlanWith(IEnumerable<ProcessInfo> apps)
    {
        var grouper = new AppGrouper(new ProcessClassifier());
        var baselineSnap = Snapshot(FreshSession(), at: Boot.AddMinutes(10));
        var baseline = BaselineFactory.Create(baselineSnap, grouper.Group(baselineSnap), BaselineOrigin.Manual, "PC");
        var snap = Snapshot([.. FreshSession(), .. apps]);
        var groups = grouper.Group(snap);
        return new CleanupPlanner().Build(groups, BaselineComparer.Compare(baseline, groups, snap.Context), new PlannerOptions());
    }

    private static readonly ProcessInfo Small = App(5000, @"C:\Apps\Tiny\JoyToKey.exe", 100, parent: 1100, visibleWindows: 1, memoryMb: 1, description: "JoyToKey");
    private static readonly ProcessInfo Medium = App(5100, @"C:\Apps\Tg\Telegram.exe", 100, parent: 1100, visibleWindows: 1, memoryMb: 650, description: "Telegram Desktop");
    private static readonly ProcessInfo Large = App(5200, @"C:\Program Files\Mozilla Firefox\firefox.exe", 100, parent: 1100, visibleWindows: 1, memoryMb: 1434, description: "Firefox");
    private static readonly ProcessInfo Script = App(5300, @"C:\Python312\python.exe", 100, parent: 1100, memoryMb: 740, subsystem: ImageSubsystem.Console, description: "Python")
        with { CommandLineHint = "train.py" };

    private static List<ProcessInfo> Apps()
    {
        var list = new List<ProcessInfo> { Small, Medium, Large, Script };
        for (var i = 0; i < 25; i++)
        {
            list.Add(App(5201 + i, Large.ImagePath!, 100.1, parent: 5200, memoryMb: 200));
        }

        return list;
    }

    private static string[] Names(IEnumerable<PlanItem> items) => items.Select(i => i.Group.DisplayName).ToArray();

    [Fact]
    public void Memory_sort_uses_bytes_not_text()
    {
        var plan = PlanWith(Apps());
        // As text, "650 MB" > "6.3 GB" > "740 MB"... would be wrong; numerically Firefox (6.3 GB) is largest.
        Assert.Equal(["Firefox", "Python", "Telegram Desktop", "JoyToKey"], Names(AppListOrdering.Sort(plan.Candidates, AppSortKey.Memory, descending: true)));
        Assert.Equal(["JoyToKey", "Telegram Desktop", "Python", "Firefox"], Names(AppListOrdering.Sort(plan.Candidates, AppSortKey.Memory, descending: false)));
    }

    [Fact]
    public void Process_count_sort_breaks_ties_by_name()
    {
        var plan = PlanWith(Apps());
        Assert.Equal(["Firefox", "JoyToKey", "Python", "Telegram Desktop"], Names(AppListOrdering.Sort(plan.Candidates, AppSortKey.Processes, descending: true)));
    }

    [Fact]
    public void Name_sort_is_case_insensitive_and_reversible()
    {
        var plan = PlanWith(Apps());
        var asc = Names(AppListOrdering.Sort(plan.Candidates, AppSortKey.Name, false));
        Assert.Equal(["Firefox", "JoyToKey", "Python", "Telegram Desktop"], asc);
        Assert.Equal(asc.Reverse(), Names(AppListOrdering.Sort(plan.Candidates, AppSortKey.Name, true)));
    }

    [Fact]
    public void Risk_sort_orders_safe_caution_review_then_kept_and_protected()
    {
        var plan = PlanWith(Apps());
        var sorted = AppListOrdering.Sort(plan.All, AppSortKey.Risk, false);
        var ranks = sorted.Select(AppListOrdering.RiskRank).ToList();
        Assert.Equal(ranks.Order(), ranks);
        Assert.Equal(SafetyLevel.Safe, sorted[0].Level);
        Assert.Equal(PlanCategory.Protected, sorted[^1].Category);
        Assert.Equal("Python", Names(AppListOrdering.Sort(plan.Candidates, AppSortKey.Risk, true))[0]);
    }

    [Fact]
    public void Default_order_is_the_planner_order()
    {
        var plan = PlanWith(Apps());
        Assert.Equal(Names(plan.Candidates), Names(AppListOrdering.Sort(plan.Candidates, AppSortKey.Default, false)));
    }

    [Theory]
    [InlineData("fire", new[] { "Firefox" })]
    [InlineData("FIREFOX.EXE", new[] { "Firefox" })]
    [InlineData("train.py", new[] { "Python" })]
    [InlineData("python.exe", new[] { "Python" })]
    [InlineData("telegram desktop", new[] { "Telegram Desktop" })]
    [InlineData("joy key", new[] { "JoyToKey" })]
    [InlineData("nothing-matches", new string[0])]
    [InlineData("", new[] { "Firefox", "JoyToKey", "Python", "Telegram Desktop" })]
    [InlineData("   ", new[] { "Firefox", "JoyToKey", "Python", "Telegram Desktop" })]
    public void Search_matches_names_executables_and_details(string query, string[] expected)
    {
        var plan = PlanWith(Apps());
        Assert.Equal(expected, Names(AppListOrdering.Sort(AppListOrdering.Filter(plan.Candidates, query), AppSortKey.Name, false)));
    }

    [Fact]
    public void Search_finds_helper_processes_by_their_executable()
    {
        var host = App(6000, @"C:\Apps\Studio\studio.exe", 100, parent: 1100, visibleWindows: 1, description: "Studio");
        var helper = App(6001, @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0\msedgewebview2.exe", 100.1, parent: 6000);
        var plan = PlanWith([host, helper]);
        Assert.Equal(["Studio"], Names(AppListOrdering.Filter(plan.Candidates, "webview2")));
    }

    [Fact]
    public void Selection_follows_defaults_until_the_user_chooses()
    {
        var plan = PlanWith(Apps());
        var selection = new SelectionState();
        var telegram = plan.Candidates.Single(c => c.Group.Root.Pid == 5100);
        var python = plan.Candidates.Single(c => c.Group.Root.Pid == 5300);

        Assert.True(selection.IsSelected(telegram));   // Safe -> preselected
        Assert.False(selection.IsSelected(python));    // Manual review -> not preselected

        selection.Set(telegram, false);
        selection.Set(python, true);
        Assert.False(selection.IsSelected(telegram));
        Assert.True(selection.IsSelected(python));
    }

    [Fact]
    public void Selection_survives_sorting_searching_and_refresh()
    {
        var selection = new SelectionState();
        var first = PlanWith(Apps());
        selection.Set(first.Candidates.Single(c => c.Group.Root.Pid == 5300), true);
        selection.Set(first.Candidates.Single(c => c.Group.Root.Pid == 5100), false);

        _ = AppListOrdering.Sort(first.Candidates, AppSortKey.Memory, true);
        _ = AppListOrdering.Filter(first.Candidates, "fire");

        // A refresh produces brand-new PlanItem objects for the same app instances.
        var refreshed = PlanWith(Apps());
        selection.Prune(refreshed.Candidates.Select(c => c.Group.Id));
        Assert.True(selection.IsSelected(refreshed.Candidates.Single(c => c.Group.Root.Pid == 5300)));
        Assert.False(selection.IsSelected(refreshed.Candidates.Single(c => c.Group.Root.Pid == 5100)));
        Assert.True(selection.IsSelected(refreshed.Candidates.Single(c => c.Group.Root.Pid == 5200)) == refreshed.Candidates.Single(c => c.Group.Root.Pid == 5200).DefaultSelected);
    }

    [Fact]
    public void Selection_of_a_closed_and_restarted_app_is_forgotten()
    {
        var selection = new SelectionState();
        var plan = PlanWith(Apps());
        var python = plan.Candidates.Single(c => c.Group.Root.Pid == 5300);
        selection.Set(python, true);

        // Python exited; a new python.exe reused PID 5300 (different creation time).
        var restarted = PlanWith([Small, Medium, Large, Script with { Identity = new ProcessIdentity(5300, At(150)) }]);
        selection.Prune(restarted.Candidates.Select(c => c.Group.Id));
        Assert.False(selection.IsSelected(restarted.Candidates.Single(c => c.Group.Root.Pid == 5300)));
        Assert.False(selection.HasExplicitChoice(python.Group.Id));
    }

    [Fact]
    public void Protected_or_kept_items_can_never_be_selected()
    {
        var plan = PlanWith(Apps());
        var selection = new SelectionState();
        foreach (var item in plan.Protected.Concat(plan.Kept))
        {
            selection.Set(item, true);
            Assert.False(selection.IsSelected(item));
        }

        Assert.All(selection.Selected(plan.All), i => Assert.True(i.IsSelectable));
    }
}
