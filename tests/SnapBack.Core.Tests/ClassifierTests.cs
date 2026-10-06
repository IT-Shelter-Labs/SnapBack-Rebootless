using SnapBack.Core.Classification;
using SnapBack.Core.Model;
using SnapBack.Core.Tests.Fakes;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public class ClassifierTests
{
    private readonly ProcessClassifier _classifier = new();
    private readonly SessionContext _ctx = Context();

    private static ProcessInfo Discord() =>
        App(5000, @"C:\Users\me\AppData\Local\Discord\app-1.0.9165\Discord.exe", 30, visibleWindows: 1);

    private SafetyAssessment Assess(ProcessInfo p) => _classifier.Classify(p, _ctx);

    [Fact]
    public void Normal_user_gui_app_is_safe()
    {
        var a = Assess(Discord());
        Assert.Equal(SafetyLevel.Safe, a.Level);
        Assert.Empty(a.Reasons);
    }

    [Fact]
    public void Every_risk_reason_has_metadata() => Assert.True(RiskReasons.IsComplete());

    public static TheoryData<string, Func<ProcessInfo, ProcessInfo>, RiskReason> ProtectedCases => new()
    {
        { "pid 4", p => p with { Identity = new ProcessIdentity(4, 0) }, RiskReason.KernelOrSystemProcess },
        { "pid 0", p => p with { Identity = new ProcessIdentity(0, 0) }, RiskReason.KernelOrSystemProcess },
        { "self", p => p with { Identity = new ProcessIdentity(SelfPid, At(1)) }, RiskReason.SnapBackItself },
        { "other SnapBack instance", p => p with { ImagePath = Context().SelfImagePath }, RiskReason.SnapBackItself },
        { "other session", p => p with { SessionId = 0 }, RiskReason.OtherSession },
        { "other user", p => p with { OwnerSid = OtherUser }, RiskReason.OtherUser },
        { "critical", p => p with { IsCritical = true }, RiskReason.CriticalProcess },
        { "ppl", p => p with { IsProtectedProcess = true }, RiskReason.ProtectedProcess },
        { "service", p => p with { IsServiceHost = true }, RiskReason.ServiceHost },
        { "elevated", p => p with { IsElevated = true }, RiskReason.Elevated },
        { "high integrity", p => p with { Integrity = IntegrityLevel.High }, RiskReason.Elevated },
        { "system integrity", p => p with { Integrity = IntegrityLevel.System }, RiskReason.Elevated },
        { "cannot open", p => p with { CouldOpen = false }, RiskReason.AccessDenied },
        { "owner unknown", p => p with { OwnerSid = null }, RiskReason.OwnerUnknown },
        { "path unknown", p => p with { ImagePath = null }, RiskReason.ImagePathUnknown },
        { "critical unknown", p => p with { IsCritical = null }, RiskReason.CannotVerify },
        { "protection unknown", p => p with { IsProtectedProcess = null }, RiskReason.CannotVerify },
        { "elevation unknown", p => p with { IsElevated = null }, RiskReason.CannotVerify },
        { "windows folder", p => p with { ImagePath = @"C:\Windows\System32\dllhost.exe", ImageName = "dllhost.exe" }, RiskReason.WindowsComponent },
        { "windows folder (case)", p => p with { ImagePath = @"c:\WINDOWS\system32\Foo.exe", ImageName = "Foo.exe" }, RiskReason.WindowsComponent },
        { "shell name anywhere", p => p with { ImagePath = @"C:\Users\me\AppData\Local\Temp\svchost.exe", ImageName = "svchost.exe" }, RiskReason.ShellComponent },
        { "explorer", p => p with { ImagePath = @"C:\Windows\explorer.exe", ImageName = "explorer.exe" }, RiskReason.ShellComponent },
        { "shell package", p => p with { PackageFullName = "MicrosoftWindows.Client.WebExperience_524.1.0.0_x64__cw5n1h2txyewy" }, RiskReason.ShellComponent },
        { "kernel pseudo name", p => p with { ImageName = "Memory Compression" }, RiskReason.KernelOrSystemProcess },
    };

    [Theory]
    [MemberData(nameof(ProtectedCases))]
    public void Hard_rules_protect(string name, Func<ProcessInfo, ProcessInfo> mutate, RiskReason expected)
    {
        var a = Assess(mutate(Discord()));
        Assert.True(a.IsProtected, name);
        Assert.Contains(expected, a.Reasons);
    }

    [Fact]
    public void Protection_cannot_be_diluted_by_caution_reasons()
    {
        // An elevated editor with unsaved changes is still protected.
        var p = App(5000, @"C:\Program Files\Microsoft VS Code\Code.exe", 30, visibleWindows: 1, titles: "● main.py - Visual Studio Code")
            with { IsElevated = true };
        Assert.Equal(SafetyLevel.Protected, Assess(p).Level);
    }

    [Fact]
    public void Windows_user_tools_need_manual_review()
    {
        var p = App(5000, @"C:\Windows\System32\notepad.exe", 30, visibleWindows: 1);
        var a = Assess(p);
        Assert.Equal(SafetyLevel.ManualReview, a.Level);
        Assert.Contains(RiskReason.WindowsUserTool, a.Reasons);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Python\Python312\python.exe", ImageSubsystem.Console, RiskReason.ScriptOrRuntime)]
    [InlineData(@"C:\Program Files\nodejs\node.exe", ImageSubsystem.Console, RiskReason.ScriptOrRuntime)]
    [InlineData(@"C:\tools\mytool.exe", ImageSubsystem.Console, RiskReason.ConsoleProgram)]
    [InlineData(@"C:\Users\me\Downloads\SomeApp-Setup.exe", ImageSubsystem.Gui, RiskReason.InstallerOrUpdater)]
    [InlineData(@"C:\Program Files\Oracle\VirtualBox\VirtualBoxVM.exe", ImageSubsystem.Gui, RiskReason.VirtualMachine)]
    public void Scripts_consoles_installers_and_vms_need_manual_review(string path, ImageSubsystem subsystem, RiskReason reason)
    {
        var a = Assess(App(5000, path, 30, subsystem: subsystem));
        Assert.Equal(SafetyLevel.ManualReview, a.Level);
        Assert.Contains(reason, a.Reasons);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", RiskReason.PossibleUnsavedWork)]
    [InlineData(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", RiskReason.PossibleUnsavedWork)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", RiskReason.Browser)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe", RiskReason.Terminal)]
    [InlineData(@"C:\Program Files\qBittorrent\qbittorrent.exe", RiskReason.SyncOrTransfer)]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", RiskReason.Launcher)]
    [InlineData(@"C:\Program Files\obs-studio\bin\64bit\obs64.exe", RiskReason.Recording)]
    public void Apps_with_something_to_lose_need_caution(string path, RiskReason reason)
    {
        var a = Assess(App(5000, path, 30, visibleWindows: 1));
        Assert.Equal(SafetyLevel.Caution, a.Level);
        Assert.Contains(reason, a.Reasons);
    }

    [Theory]
    [InlineData("*Untitled - Notepad")]
    [InlineData("● main.py - project - Visual Studio Code")]
    [InlineData("report.txt* - Sublime Text")]
    [InlineData("Document1 (unsaved)")]
    public void Dirty_window_titles_raise_caution(string title)
    {
        var a = Assess(App(5000, @"C:\Apps\Editor\editor.exe", 30, visibleWindows: 1, titles: title));
        Assert.Equal(SafetyLevel.Caution, a.Level);
        Assert.Contains(RiskReason.UnsavedChangesInTitle, a.Reasons);
    }

    [Theory]
    [InlineData("Discord")]
    [InlineData("Telegram (3)")]
    [InlineData("Inbox - Mail")]
    public void Normal_titles_do_not_indicate_unsaved_changes(string title) =>
        Assert.False(KnownProcesses.TitleIndicatesUnsavedChanges(title));

    [Fact]
    public void Hung_app_needs_caution()
    {
        var p = Discord() with { Windows = new WindowSummary { VisibleMainWindows = 1, AnyHung = true } };
        Assert.Contains(RiskReason.NotResponding, Assess(p).Reasons);
    }

    [Fact]
    public void App_that_launched_SnapBack_needs_manual_review()
    {
        var ctx = Context(new HashSet<int> { 5000 });
        var a = _classifier.Classify(Discord(), ctx);
        Assert.Equal(SafetyLevel.ManualReview, a.Level);
        Assert.Contains(RiskReason.LaunchedSnapBack, a.Reasons);
    }
}
