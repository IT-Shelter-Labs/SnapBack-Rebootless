using SnapBack.Core.Classification;
using SnapBack.Core.Model;

namespace SnapBack.Core.Grouping;

/// <summary>
/// One running instance of an application: a root process plus the related processes it started
/// (Chrome's renderers, VS Code's helpers, an app's embedded WebView2, ...).
/// </summary>
public sealed class AppGroup
{
    public required ProcessInfo Root { get; init; }

    /// <summary>All processes of the group, root first, then by creation time.</summary>
    public required IReadOnlyList<ProcessInfo> Members { get; init; }

    public required string AppKey { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>The application's own install folder, when one can be determined.</summary>
    public string? InstallRoot { get; init; }

    /// <summary>Root identity of the group whose process started this group's root.</summary>
    public ProcessIdentity? LaunchedBy { get; init; }

    public string? LaunchedByName { get; init; }

    /// <summary>Safety assessment of the application as a whole.</summary>
    public required SafetyAssessment Assessment { get; init; }

    /// <summary>Per-process assessment, keyed by PID.</summary>
    public required IReadOnlyDictionary<int, SafetyAssessment> MemberAssessments { get; init; }

    public ProcessIdentity Id => Root.Identity;

    public int ProcessCount => Members.Count;

    public long PrivateWorkingSet => Members.Sum(m => m.PrivateWorkingSet);

    /// <summary>Memory of the processes SnapBack may act on (excludes protected members).</summary>
    public long ActionablePrivateWorkingSet => ActionableMembers.Sum(m => m.PrivateWorkingSet);

    public int VisibleWindowCount => ActionableMembers.Sum(m => m.Windows.VisibleMainWindows);

    public IReadOnlyList<string> WindowTitles => ActionableMembers.SelectMany(m => m.Windows.Titles).ToArray();

    public string? ExecutablePath => Root.ImagePath;

    /// <summary>Extra detail for the UI, e.g. the script a Python interpreter runs.</summary>
    public string? Detail => Root.CommandLineHint;

    public bool IsProtected => Assessment.IsProtected;

    public IEnumerable<ProcessInfo> ActionableMembers => Members.Where(m => !IsMemberProtected(m.Pid));

    public IEnumerable<ProcessInfo> ProtectedMembers => Members.Where(m => IsMemberProtected(m.Pid));

    public bool IsMemberProtected(int pid) =>
        !MemberAssessments.TryGetValue(pid, out var c) || c.IsProtected;

    public bool Contains(ProcessIdentity identity) => Members.Any(m => m.Identity == identity);

    public override string ToString() => $"{DisplayName} ({ProcessCount} processes, root {Root.Identity})";
}
