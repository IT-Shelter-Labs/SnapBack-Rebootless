using SnapBack.Core.Classification;
using SnapBack.Core.Grouping;
using SnapBack.Core.Model;
using SnapBack.Core.Tests.Fakes;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public class GroupingTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    private static IReadOnlyList<AppGroup> Group(IEnumerable<ProcessInfo> extra)
    {
        var all = FreshSession().Concat(extra).ToList();
        return new AppGrouper(new ProcessClassifier()).Group(Snapshot(all));
    }

    private static AppGroup GroupOf(IReadOnlyList<AppGroup> groups, int pid) => groups.Single(g => g.Members.Any(m => m.Pid == pid));

    [Fact]
    public void Chrome_and_its_many_processes_are_one_application()
    {
        var procs = new List<ProcessInfo> { App(2000, Chrome, 60, parent: 1100, visibleWindows: 2, description: "Google Chrome") };
        for (var i = 0; i < 25; i++)
        {
            procs.Add(App(2001 + i, Chrome, 60.1 + (i / 100.0), parent: 2000));
        }

        var groups = Group(procs);
        var chrome = GroupOf(groups, 2000);
        Assert.Equal(26, chrome.ProcessCount);
        Assert.Equal("Google Chrome", chrome.DisplayName);
        Assert.Equal(2600L * 1024 * 1024, chrome.PrivateWorkingSet);
        Assert.Equal(1100, chrome.LaunchedBy?.Pid);
    }

    [Fact]
    public void Recycled_parent_pid_does_not_create_a_false_relationship()
    {
        // PID 3000 is a new process (started later) that reuses the PID of the child's real, dead parent.
        var procs = new[]
        {
            App(3000, Chrome, 120, parent: 1100, visibleWindows: 1),
            App(3100, @"C:\Users\me\AppData\Local\Discord\app-1.0.1\Discord.exe", 60, parent: 3000, visibleWindows: 1),
        };

        var groups = Group(procs);
        Assert.NotSame(GroupOf(groups, 3000), GroupOf(groups, 3100));
        Assert.Null(GroupOf(groups, 3100).LaunchedBy);
    }

    [Fact]
    public void WebView2_helpers_belong_to_their_host_app()
    {
        var procs = new[]
        {
            App(4000, @"C:\Users\me\AppData\Local\Programs\SomeApp\SomeApp.exe", 60, parent: 1100, visibleWindows: 1),
            App(4001, @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0.1\msedgewebview2.exe", 60.1, parent: 4000),
            App(4002, @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0.1\msedgewebview2.exe", 60.2, parent: 4001),
        };

        var groups = Group(procs);
        Assert.Equal(3, GroupOf(groups, 4000).ProcessCount);
    }

    [Fact]
    public void Same_publisher_helpers_inside_install_folder_join_but_games_do_not()
    {
        const string steamDir = @"C:\Games\Steam";
        var procs = new[]
        {
            App(5000, steamDir + @"\steam.exe", 60, parent: 1100, hiddenWindows: 2, company: "Valve Corporation"),
            App(5001, steamDir + @"\bin\cef\cef.win7x64\steamwebhelper.exe", 60.1, parent: 5000, visibleWindows: 1, company: "Valve Corporation"),
            App(5002, steamDir + @"\steamapps\common\SomeGame\Game.exe", 90, parent: 5000, visibleWindows: 1, company: "Indie Studio"),
        };

        var groups = Group(procs);
        var steam = GroupOf(groups, 5000);
        Assert.Contains(steam.Members, m => m.Pid == 5001);
        var game = GroupOf(groups, 5002);
        Assert.NotSame(steam, game);
        Assert.Equal(steam.Id, game.LaunchedBy);
        Assert.Equal("steam", game.LaunchedByName);
    }

    [Fact]
    public void Apps_started_by_explorer_never_merge_into_the_Windows_folder()
    {
        var procs = new[]
        {
            App(6000, @"C:\Windows\System32\notepad.exe", 60, parent: 1100, visibleWindows: 1, company: "Microsoft Corporation"),
            App(6001, @"C:\Windows\System32\mspaint.exe", 61, parent: 1100, visibleWindows: 1, company: "Microsoft Corporation"),
        };

        var groups = Group(procs);
        Assert.NotSame(GroupOf(groups, 1100), GroupOf(groups, 6000));
        Assert.NotSame(GroupOf(groups, 6000), GroupOf(groups, 6001));
        Assert.True(GroupOf(groups, 1100).IsProtected);
        Assert.False(GroupOf(groups, 6000).IsProtected);
    }

    [Fact]
    public void Protected_members_are_left_out_of_actions_but_do_not_protect_the_app()
    {
        var procs = new[]
        {
            App(7000, @"C:\Tools\Builder\builder.exe", 60, parent: 1100, visibleWindows: 1),
            App(7001, @"C:\Windows\System32\conhost.exe", 60.01, parent: 7000),
            App(7002, @"C:\Tools\Builder\builder.exe", 60.02, parent: 7000) with { IsElevated = true },
        };

        var group = GroupOf(Group(procs), 7000);
        Assert.Equal(3, group.ProcessCount);
        Assert.False(group.IsProtected);
        Assert.Equal([7000], group.ActionableMembers.Select(m => m.Pid));
        Assert.Equal([7001, 7002], group.ProtectedMembers.Select(m => m.Pid).Order());
    }

    [Fact]
    public void Window_presence_shapes_the_group_assessment()
    {
        var groups = Group(
        [
            App(8000, @"C:\Apps\Tray\tray.exe", 60, parent: 1100, hiddenWindows: 1),
            App(8100, @"C:\Apps\Daemon\daemon.exe", 60, parent: 1100),
        ]);

        var tray = GroupOf(groups, 8000).Assessment;
        Assert.Equal(SafetyLevel.Safe, tray.Level);
        Assert.Contains(RiskReason.RunsInBackground, tray.Reasons);

        var daemon = GroupOf(groups, 8100).Assessment;
        Assert.Equal(SafetyLevel.ManualReview, daemon.Level);
        Assert.Contains(RiskReason.NoWindow, daemon.Reasons);
    }

    [Fact]
    public void Member_reasons_raise_the_group_to_caution_at_most()
    {
        var groups = Group(
        [
            App(9100, @"C:\Apps\Studio\studio.exe", 60, parent: 1100, visibleWindows: 1, company: "Studio Inc"),
            App(9101, @"C:\Apps\Studio\runtime\node.exe", 60.1, parent: 9100, company: "Studio Inc", subsystem: ImageSubsystem.Console),
        ]);

        var g = GroupOf(groups, 9100);
        Assert.Equal(2, g.ProcessCount);
        Assert.Equal(SafetyLevel.Caution, g.Assessment.Level);
        Assert.Contains(RiskReason.ScriptOrRuntime, g.Assessment.Reasons);
    }

    [Fact]
    public void Processes_of_other_sessions_are_not_grouped()
    {
        var groups = Group([App(9500, @"C:\Apps\Foo\foo.exe", 60) with { SessionId = 2 }]);
        Assert.DoesNotContain(groups, g => g.Members.Any(m => m.Pid == 9500));
    }

    [Fact]
    public void Install_root_skips_version_folders_and_ignores_shared_folders()
    {
        var ctx = Context();
        Assert.Equal(@"C:\Users\me\AppData\Local\Discord",
            AppGrouper.InstallRootOf(App(1, @"C:\Users\me\AppData\Local\Discord\app-1.0.9\Discord.exe", 1), ctx));
        Assert.Null(AppGrouper.InstallRootOf(App(1, @"C:\Program Files\tool.exe", 1), ctx));
        Assert.Null(AppGrouper.InstallRootOf(App(1, @"C:\Users\me\AppData\Local\Programs\x.exe", 1), ctx));
        Assert.Null(AppGrouper.InstallRootOf(App(1, @"C:\Windows\System32\x.exe", 1), ctx));
        Assert.Null(AppGrouper.InstallRootOf(App(1, @"C:\x.exe", 1), ctx));
    }
}

public class AppKeyTests
{
    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Discord\app-1.0.9165\Discord.exe", @"c:\users\me\appdata\local\discord\app-*\discord.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\JetBrains\Toolbox\apps\IDEA-U\ch-0\242.20224.300\bin\idea64.exe", @"c:\users\me\appdata\local\jetbrains\toolbox\apps\idea-u\ch-0\*\bin\idea64.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe", @"c:\program files\windowsapps\microsoft.windowsterminal_*_x64__8wekyb3d8bbwe\windowsterminal.exe")]
    [InlineData(@"C:/Program Files/App/app.exe", @"c:\program files\app\app.exe")]
    [InlineData(@"C:\Python312\python.exe", @"c:\python312\python.exe")]
    public void Paths_are_normalized_across_versions(string path, string expected) =>
        Assert.Equal(expected, AppKeys.NormalizePath(path));

    [Fact]
    public void Updated_squirrel_app_keeps_its_key()
    {
        var before = TestData.App(1, @"C:\Users\me\AppData\Local\Discord\app-1.0.9165\Discord.exe", 1);
        var after = TestData.App(2, @"C:\Users\me\AppData\Local\Discord\app-1.0.9170\Discord.exe", 2);
        Assert.Equal(AppKeys.For(before), AppKeys.For(after));
    }

    [Fact]
    public void Packaged_apps_are_keyed_by_family_and_executable()
    {
        var p = TestData.App(1, @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2410.21.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe", 1)
            with { PackageFullName = "Microsoft.WindowsNotepad_11.2410.21.0_x64__8wekyb3d8bbwe" };
        Assert.Equal("pkg:microsoft.windowsnotepad_8wekyb3d8bbwe!notepad.exe", AppKeys.For(p));
    }

    [Fact]
    public void Unknown_path_falls_back_to_name() =>
        Assert.Equal("name:foo.exe", AppKeys.For(TestData.App(1, @"C:\x\Foo.exe", 1) with { ImagePath = null }));
}
