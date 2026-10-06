using SnapBack.Core.Model;
using SnapBack.Core.Text;

namespace SnapBack.Core.Classification;

/// <summary>
/// Classifies individual processes. Pure and deterministic: the same input always yields the same result.
/// </summary>
/// <remarks>
/// The rules are ordered so that every "protected" rule is evaluated first and independently. There
/// is no setting, list or flag that can lower a protected classification.
/// </remarks>
public sealed class ProcessClassifier
{
    public SafetyAssessment Classify(ProcessInfo p, SessionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(ctx);

        var b = new AssessmentBuilder();
        var name = p.ImageName;

        // ---- Hard protection: identity of the process ---------------------------------------
        if (p.Pid is 0 or 4 || KnownProcesses.KernelPseudoProcesses.Contains(name))
        {
            b.Add(RiskReason.KernelOrSystemProcess);
        }

        if (p.Pid == ctx.SelfPid || PathUtil.PathEquals(p.ImagePath, ctx.SelfImagePath))
        {
            b.Add(RiskReason.SnapBackItself);
        }

        if (p.SessionId != ctx.SessionId)
        {
            b.Add(RiskReason.OtherSession);
        }

        if (p.OwnerSid is not null && !string.Equals(p.OwnerSid, ctx.UserSid, StringComparison.OrdinalIgnoreCase))
        {
            b.Add(RiskReason.OtherUser);
        }

        if (p.IsCritical == true)
        {
            b.Add(RiskReason.CriticalProcess);
        }

        if (p.IsProtectedProcess == true)
        {
            b.Add(RiskReason.ProtectedProcess);
        }

        if (p.IsServiceHost)
        {
            b.Add(RiskReason.ServiceHost);
        }

        if (KnownProcesses.ShellAndSessionComponents.Contains(name) || IsProtectedPackage(p.PackageFullName))
        {
            b.Add(RiskReason.ShellComponent);
        }

        if (PathUtil.IsUnder(p.ImagePath, ctx.WindowsDirectory))
        {
            b.Add(KnownProcesses.WindowsUserTools.Contains(name) ? RiskReason.WindowsUserTool : RiskReason.WindowsComponent);
        }

        if (p.IsElevated == true || p.Integrity >= IntegrityLevel.High)
        {
            b.Add(RiskReason.Elevated);
        }

        // ---- Hard protection: anything we could not verify (fail closed) --------------------
        if (!p.CouldOpen)
        {
            b.Add(RiskReason.AccessDenied);
        }

        if (p.OwnerSid is null)
        {
            b.Add(RiskReason.OwnerUnknown);
        }

        if (string.IsNullOrWhiteSpace(p.ImagePath))
        {
            b.Add(RiskReason.ImagePathUnknown);
        }

        if (p.IsCritical is null || p.IsProtectedProcess is null || p.IsElevated is null)
        {
            b.Add(RiskReason.CannotVerify);
        }

        if (b.Level == SafetyLevel.Protected)
        {
            return b.Build();
        }

        // ---- Manual review --------------------------------------------------------------------
        if (p.Subsystem == ImageSubsystem.Console)
        {
            b.Add(RiskReason.ConsoleProgram);
        }

        if (KnownProcesses.ScriptsAndRuntimes.Contains(name))
        {
            b.Add(RiskReason.ScriptOrRuntime);
        }

        if (KnownProcesses.VirtualMachines.Contains(name))
        {
            b.Add(RiskReason.VirtualMachine);
        }

        if (KnownProcesses.LooksLikeInstaller(name))
        {
            b.Add(RiskReason.InstallerOrUpdater);
        }

        if (ctx.SelfAncestorPids.Contains(p.Pid))
        {
            b.Add(RiskReason.LaunchedSnapBack);
        }

        // ---- Caution --------------------------------------------------------------------------
        if (KnownProcesses.DocumentEditors.Contains(name))
        {
            b.Add(RiskReason.PossibleUnsavedWork);
        }

        if (p.Windows.Titles.Any(KnownProcesses.TitleIndicatesUnsavedChanges))
        {
            b.Add(RiskReason.UnsavedChangesInTitle);
        }

        if (KnownProcesses.Browsers.Contains(name))
        {
            b.Add(RiskReason.Browser);
        }

        if (KnownProcesses.Terminals.Contains(name))
        {
            b.Add(RiskReason.Terminal);
        }

        if (KnownProcesses.SyncAndTransfer.Contains(name))
        {
            b.Add(RiskReason.SyncOrTransfer);
        }

        if (KnownProcesses.Launchers.Contains(name))
        {
            b.Add(RiskReason.Launcher);
        }

        if (KnownProcesses.Recording.Contains(name))
        {
            b.Add(RiskReason.Recording);
        }

        if (p.Windows.AnyHung)
        {
            b.Add(RiskReason.NotResponding);
        }

        return b.Build();
    }

    private static bool IsProtectedPackage(string? packageFullName)
    {
        if (string.IsNullOrEmpty(packageFullName))
        {
            return false;
        }

        foreach (var prefix in KnownProcesses.ProtectedPackagePrefixes)
        {
            if (packageFullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
