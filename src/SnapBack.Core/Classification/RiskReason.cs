using SnapBack.Core.Localization;

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

/// <summary>Severity and localized texts for <see cref="RiskReason"/> values.</summary>
public static class RiskReasons
{
    private static readonly Dictionary<RiskReason, SafetyLevel> Levels = new()
    {
        [RiskReason.KernelOrSystemProcess] = SafetyLevel.Protected,
        [RiskReason.SnapBackItself] = SafetyLevel.Protected,
        [RiskReason.OtherSession] = SafetyLevel.Protected,
        [RiskReason.OtherUser] = SafetyLevel.Protected,
        [RiskReason.CriticalProcess] = SafetyLevel.Protected,
        [RiskReason.ProtectedProcess] = SafetyLevel.Protected,
        [RiskReason.ServiceHost] = SafetyLevel.Protected,
        [RiskReason.ShellComponent] = SafetyLevel.Protected,
        [RiskReason.WindowsComponent] = SafetyLevel.Protected,
        [RiskReason.Elevated] = SafetyLevel.Protected,
        [RiskReason.AccessDenied] = SafetyLevel.Protected,
        [RiskReason.OwnerUnknown] = SafetyLevel.Protected,
        [RiskReason.ImagePathUnknown] = SafetyLevel.Protected,
        [RiskReason.CannotVerify] = SafetyLevel.Protected,

        [RiskReason.ConsoleProgram] = SafetyLevel.ManualReview,
        [RiskReason.ScriptOrRuntime] = SafetyLevel.ManualReview,
        [RiskReason.NoWindow] = SafetyLevel.ManualReview,
        [RiskReason.VirtualMachine] = SafetyLevel.ManualReview,
        [RiskReason.InstallerOrUpdater] = SafetyLevel.ManualReview,
        [RiskReason.LaunchedSnapBack] = SafetyLevel.ManualReview,
        [RiskReason.WindowsUserTool] = SafetyLevel.ManualReview,

        [RiskReason.PossibleUnsavedWork] = SafetyLevel.Caution,
        [RiskReason.UnsavedChangesInTitle] = SafetyLevel.Caution,
        [RiskReason.Browser] = SafetyLevel.Caution,
        [RiskReason.Terminal] = SafetyLevel.Caution,
        [RiskReason.SyncOrTransfer] = SafetyLevel.Caution,
        [RiskReason.Launcher] = SafetyLevel.Caution,
        [RiskReason.Recording] = SafetyLevel.Caution,
        [RiskReason.NotResponding] = SafetyLevel.Caution,
        [RiskReason.NewInstanceOfBaselineApp] = SafetyLevel.Caution,
        [RiskReason.StartedByBaselineApp] = SafetyLevel.Caution,

        [RiskReason.RunsInBackground] = SafetyLevel.Safe,
    };

    /// <summary>The minimum level a reason imposes. Unknown reasons are treated as protected.</summary>
    public static SafetyLevel LevelOf(RiskReason reason) => Levels.TryGetValue(reason, out var level) ? level : SafetyLevel.Protected;

    public static string Label(RiskReason reason) => Loc.Get($"Reason_{reason}_Label");

    public static string Explain(RiskReason reason) => Loc.Get($"Reason_{reason}_Explain");

    /// <summary>Whether every enum value has a level. Used by tests.</summary>
    internal static bool IsComplete() => Enum.GetValues<RiskReason>().All(Levels.ContainsKey);
}
