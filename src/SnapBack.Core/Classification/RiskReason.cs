namespace SnapBack.Core.Classification;

/// <summary>Why a process or application received its <see cref="SafetyLevel"/>.</summary>
public enum RiskReason
{
    // ---- Protected ------------------------------------------------------------------------
    KernelOrSystemProcess,
    SnapBackItself,
    OtherSession,
    OtherUser,
    CriticalProcess,
    ProtectedProcess,
    ServiceHost,
    ShellComponent,
    WindowsComponent,
    Elevated,
    AccessDenied,
    OwnerUnknown,
    ImagePathUnknown,
    CannotVerify,

    // ---- Manual review --------------------------------------------------------------------
    ConsoleProgram,
    ScriptOrRuntime,
    NoWindow,
    VirtualMachine,
    InstallerOrUpdater,
    LaunchedSnapBack,
    WindowsUserTool,

    // ---- Caution --------------------------------------------------------------------------
    PossibleUnsavedWork,
    UnsavedChangesInTitle,
    Browser,
    Terminal,
    SyncOrTransfer,
    Launcher,
    Recording,
    NotResponding,
    NewInstanceOfBaselineApp,
    StartedByBaselineApp,

    // ---- Informational (do not change the level) ------------------------------------------
    RunsInBackground,
}

/// <summary>Severity and human-readable texts for <see cref="RiskReason"/> values.</summary>
public static class RiskReasons
{
    private sealed record Info(SafetyLevel Level, string Label, string Explanation);

    private static readonly Dictionary<RiskReason, Info> Infos = new()
    {
        [RiskReason.KernelOrSystemProcess] = new(SafetyLevel.Protected, "Windows kernel process", "Part of the Windows kernel. SnapBack never touches it."),
        [RiskReason.SnapBackItself] = new(SafetyLevel.Protected, "SnapBack itself", "This is SnapBack."),
        [RiskReason.OtherSession] = new(SafetyLevel.Protected, "Another session", "Runs in a different Windows session (system services or another signed-in user)."),
        [RiskReason.OtherUser] = new(SafetyLevel.Protected, "Another user", "Runs under a different user account."),
        [RiskReason.CriticalProcess] = new(SafetyLevel.Protected, "Critical system process", "Windows marks this process as critical: ending it would crash the computer."),
        [RiskReason.ProtectedProcess] = new(SafetyLevel.Protected, "Protected process", "Windows protects this process (for example anti-malware or DRM)."),
        [RiskReason.ServiceHost] = new(SafetyLevel.Protected, "Windows service", "Hosts a Windows service. SnapBack does not manage services."),
        [RiskReason.ShellComponent] = new(SafetyLevel.Protected, "Windows component", "An essential part of the Windows desktop, session or security."),
        [RiskReason.WindowsComponent] = new(SafetyLevel.Protected, "Windows component", "Runs from the Windows folder."),
        [RiskReason.Elevated] = new(SafetyLevel.Protected, "Runs as administrator", "Runs with administrator rights. SnapBack never touches elevated programs."),
        [RiskReason.AccessDenied] = new(SafetyLevel.Protected, "Access denied", "Windows does not allow SnapBack to inspect this process."),
        [RiskReason.OwnerUnknown] = new(SafetyLevel.Protected, "Owner unknown", "The owner of this process could not be verified."),
        [RiskReason.ImagePathUnknown] = new(SafetyLevel.Protected, "Unknown executable", "The executable of this process could not be verified."),
        [RiskReason.CannotVerify] = new(SafetyLevel.Protected, "Cannot verify", "Some safety attributes of this process could not be verified."),

        [RiskReason.ConsoleProgram] = new(SafetyLevel.ManualReview, "Console program", "May be running a job; stopping it can interrupt work in progress."),
        [RiskReason.ScriptOrRuntime] = new(SafetyLevel.ManualReview, "Script or runtime", "Python, Node.js and similar runtimes may be in the middle of a task."),
        [RiskReason.NoWindow] = new(SafetyLevel.ManualReview, "Background process", "Has no window, so it cannot be asked to close politely."),
        [RiskReason.VirtualMachine] = new(SafetyLevel.ManualReview, "Virtual machine", "Closing it may stop a virtual machine and lose its unsaved state."),
        [RiskReason.InstallerOrUpdater] = new(SafetyLevel.ManualReview, "Installer or updater", "Interrupting it may leave an installation broken."),
        [RiskReason.LaunchedSnapBack] = new(SafetyLevel.ManualReview, "Started SnapBack", "SnapBack was launched from this program; closing it may close SnapBack too."),
        [RiskReason.WindowsUserTool] = new(SafetyLevel.ManualReview, "Windows tool", "A tool that ships with Windows."),

        [RiskReason.PossibleUnsavedWork] = new(SafetyLevel.Caution, "Possible unsaved data", "Editors, IDEs and office apps may hold unsaved documents. They are asked to close normally, so they can prompt you."),
        [RiskReason.UnsavedChangesInTitle] = new(SafetyLevel.Caution, "Unsaved changes", "A window title indicates unsaved changes."),
        [RiskReason.Browser] = new(SafetyLevel.Caution, "Web browser", "Tabs are usually restored, but downloads in progress and unsent forms may be lost."),
        [RiskReason.Terminal] = new(SafetyLevel.Caution, "Terminal", "Closing a terminal ends every shell and command running in it."),
        [RiskReason.SyncOrTransfer] = new(SafetyLevel.Caution, "May be transferring files", "Sync, torrent and download apps may be in the middle of a transfer."),
        [RiskReason.Launcher] = new(SafetyLevel.Caution, "Launcher", "Game and app launchers may be downloading or installing updates."),
        [RiskReason.Recording] = new(SafetyLevel.Caution, "Recording or streaming", "May be recording or streaming right now."),
        [RiskReason.NotResponding] = new(SafetyLevel.Caution, "Not responding", "The app is not responding at the moment."),
        [RiskReason.NewInstanceOfBaselineApp] = new(SafetyLevel.Caution, "Extra instance", "Another instance of this app was already running at the baseline."),
        [RiskReason.StartedByBaselineApp] = new(SafetyLevel.Caution, "Started by a baseline app", "Started by an app that was already running at the baseline; it may still need it."),

        [RiskReason.RunsInBackground] = new(SafetyLevel.Safe, "Runs in background", "No visible window (for example minimized to the tray)."),
    };

    /// <summary>The minimum level a reason imposes.</summary>
    public static SafetyLevel LevelOf(RiskReason reason) => Infos.TryGetValue(reason, out var i) ? i.Level : SafetyLevel.Protected;

    public static string Label(RiskReason reason) => Infos.TryGetValue(reason, out var i) ? i.Label : reason.ToString();

    public static string Explain(RiskReason reason) => Infos.TryGetValue(reason, out var i) ? i.Explanation : reason.ToString();

    /// <summary>Whether every enum value has metadata. Used by tests.</summary>
    internal static bool IsComplete() => Enum.GetValues<RiskReason>().All(Infos.ContainsKey);
}
