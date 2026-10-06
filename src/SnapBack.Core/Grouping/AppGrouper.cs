using SnapBack.Core.Classification;
using SnapBack.Core.Model;
using SnapBack.Core.Text;

namespace SnapBack.Core.Grouping;

/// <summary>
/// Turns a flat process list into applications.
/// </summary>
/// <remarks>
/// A process joins the group of its parent when it is clearly part of the same application:
/// <list type="number">
/// <item>it runs the same executable as its parent or as the group root (Chrome, Discord, VS Code);</item>
/// <item>it is a generic helper host (WebView2, console host, crash handler);</item>
/// <item>it lives inside the group's own install folder and has the same publisher
/// (e.g. <c>steamwebhelper.exe</c> under the Steam folder).</item>
/// </list>
/// Anything else starts its own group and remembers which application launched it. Parent links
/// are validated with creation times, so a recycled parent PID never creates a false relationship.
/// </remarks>
public sealed class AppGrouper
{
    private readonly ProcessClassifier _classifier;

    public AppGrouper(ProcessClassifier classifier)
    {
        _classifier = classifier;
    }

    public IReadOnlyList<AppGroup> Group(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ctx = snapshot.Context;

        var ordered = snapshot.SessionProcesses
            .OrderBy(p => p.Identity.CreationTime)
            .ThenBy(p => p.Pid)
            .ToList();

        var builders = new List<Builder>();
        var groupOfPid = new Dictionary<int, Builder>();
        var keyOfPid = new Dictionary<int, string>();
        var classOfPid = new Dictionary<int, SafetyAssessment>();

        foreach (var p in ordered)
        {
            var cls = _classifier.Classify(p, ctx);
            var key = AppKeys.For(p);
            classOfPid[p.Pid] = cls;
            keyOfPid[p.Pid] = key;

            var parent = snapshot.ParentOf(p);
            Builder? parentGroup = null;
            if (parent is not null)
            {
                groupOfPid.TryGetValue(parent.Pid, out parentGroup);
            }

            Builder target;
            if (parentGroup is not null && ShouldJoin(p, key, keyOfPid[parent!.Pid], parentGroup))
            {
                target = parentGroup;
                target.Members.Add(p);
            }
            else
            {
                target = new Builder(p, key, cls, InstallRootOf(p, ctx), parentGroup);
                builders.Add(target);
            }

            groupOfPid[p.Pid] = target;
        }

        return builders.Select(b => Build(b, classOfPid)).ToList();
    }

    private static bool ShouldJoin(ProcessInfo child, string childKey, string parentKey, Builder group)
    {
        if (childKey == parentKey || childKey == group.RootKey)
        {
            return true;
        }

        if (KnownProcesses.HelperHosts.Contains(child.ImageName))
        {
            return true;
        }

        // Install-folder rule never applies below protected roots (explorer.exe, svchost.exe, ...),
        // otherwise everything under C:\Windows would merge into one group.
        if (group.RootAssessment.IsProtected || group.InstallRoot is null)
        {
            return false;
        }

        if (!PathUtil.IsUnder(child.ImagePath, group.InstallRoot))
        {
            return false;
        }

        var rootCompany = group.Root.CompanyName?.Trim();
        var childCompany = child.CompanyName?.Trim();
        return string.IsNullOrEmpty(childCompany)
            || string.Equals(rootCompany, childCompany, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The application's own folder: the executable's folder, skipping version-specific folders
    /// (<c>Discord\app-1.0.9\Discord.exe</c> → <c>Discord</c>). Returns <c>null</c> for shared
    /// folders such as Program Files, AppData or anything under Windows.
    /// </summary>
    internal static string? InstallRootOf(ProcessInfo p, SessionContext ctx)
    {
        var dir = PathUtil.GetDirectory(p.ImagePath);
        if (dir is null)
        {
            return null;
        }

        var leaf = PathUtil.GetFileName(dir);
        if (AppKeys.IsVersionSegment(leaf.ToLowerInvariant()))
        {
            dir = PathUtil.GetDirectory(dir);
            if (dir is null)
            {
                return null;
            }
        }

        if (dir.Length <= 3
            || PathUtil.PathEquals(dir, ctx.WindowsDirectory)
            || PathUtil.IsUnder(dir, ctx.WindowsDirectory)
            || ctx.GenericDirectories.Any(g => PathUtil.PathEquals(dir, g)))
        {
            return null;
        }

        return dir;
    }

    private static AppGroup Build(Builder b, Dictionary<int, SafetyAssessment> classOfPid)
    {
        var memberClasses = b.Members.ToDictionary(m => m.Pid, m => classOfPid[m.Pid]);
        return new AppGroup
        {
            Root = b.Root,
            Members = b.Members.ToArray(),
            AppKey = b.RootKey,
            DisplayName = DisplayNameOf(b.Root),
            InstallRoot = b.InstallRoot,
            LaunchedBy = b.LaunchedBy?.Root.Identity,
            LaunchedByName = b.LaunchedBy is null ? null : DisplayNameOf(b.LaunchedBy.Root),
            Assessment = AssessGroup(b, memberClasses),
            MemberAssessments = memberClasses,
        };
    }

    /// <summary>
    /// The group inherits the root's classification. Other members can add reasons but raise the
    /// level to <see cref="SafetyLevel.Caution"/> at most; protected members are simply left alone.
    /// </summary>
    private static SafetyAssessment AssessGroup(Builder b, Dictionary<int, SafetyAssessment> memberClasses)
    {
        var rootClass = memberClasses[b.Root.Pid];
        if (rootClass.IsProtected)
        {
            return rootClass;
        }

        var builder = new AssessmentBuilder().AddRange(rootClass.Reasons);
        var actionable = b.Members.Where(m => !memberClasses[m.Pid].IsProtected).ToList();

        foreach (var m in actionable)
        {
            if (m.Pid == b.Root.Pid)
            {
                continue;
            }

            foreach (var reason in memberClasses[m.Pid].Reasons)
            {
                builder.Add(reason, SafetyLevel.Caution);
            }
        }

        var visible = actionable.Sum(m => m.Windows.VisibleMainWindows);
        var hidden = actionable.Sum(m => m.Windows.HiddenTopLevelWindows);
        var isConsoleRoot = b.Root.Subsystem == ImageSubsystem.Console;

        if (actionable.Any(m => m.Windows.Titles.Any(KnownProcesses.TitleIndicatesUnsavedChanges)))
        {
            builder.Add(RiskReason.UnsavedChangesInTitle);
        }

        if (actionable.Any(m => m.Windows.AnyHung))
        {
            builder.Add(RiskReason.NotResponding);
        }

        if (!isConsoleRoot)
        {
            if (visible == 0 && hidden == 0)
            {
                builder.Add(RiskReason.NoWindow);
            }
            else if (visible == 0)
            {
                builder.Add(RiskReason.RunsInBackground);
            }
        }

        return builder.Build();
    }

    internal static string DisplayNameOf(ProcessInfo p)
    {
        var fallback = Path.GetFileNameWithoutExtension(p.ImageName);
        foreach (var candidate in new[] { p.FileDescription, p.ProductName })
        {
            var c = candidate?.Trim();
            if (!string.IsNullOrEmpty(c)
                && !string.Equals(c, p.ImageName, StringComparison.OrdinalIgnoreCase)
                && c.Length <= 80)
            {
                return c;
            }
        }

        return fallback;
    }

    private sealed class Builder
    {
        public Builder(ProcessInfo root, string rootKey, SafetyAssessment rootAssessment, string? installRoot, Builder? launchedBy)
        {
            Root = root;
            RootKey = rootKey;
            RootAssessment = rootAssessment;
            InstallRoot = installRoot;
            LaunchedBy = launchedBy;
            Members.Add(root);
        }

        public ProcessInfo Root { get; }

        public string RootKey { get; }

        public SafetyAssessment RootAssessment { get; }

        public string? InstallRoot { get; }

        public Builder? LaunchedBy { get; }

        public List<ProcessInfo> Members { get; } = [];
    }
}
