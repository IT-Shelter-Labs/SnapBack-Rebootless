using SnapBack.Core.Classification;
using SnapBack.Core.Planning;
using SnapBack.Core.Text;

namespace SnapBack.Core.Presentation;

public enum AppSortKey
{
    /// <summary>The planner's order (risk first, then memory).</summary>
    Default,
    Name,
    Memory,
    Processes,
    Risk,
}

/// <summary>Sorting and searching of application lists. Pure functions, independent of the UI.</summary>
public static class AppListOrdering
{
    /// <summary>
    /// The memory value shown for an item: for candidates only what SnapBack could actually release
    /// (protected helper processes excluded), for everything else the whole app.
    /// </summary>
    public static long MemoryOf(PlanItem item) =>
        item.IsSelectable ? item.Group.ActionablePrivateWorkingSet : item.Group.PrivateWorkingSet;

    /// <summary>Ordering rank: kept and protected items rank above every candidate level.</summary>
    public static int RiskRank(PlanItem item) => item.Category switch
    {
        PlanCategory.Candidate => (int)item.Level,
        PlanCategory.Kept => 10,
        _ => 20,
    };

    /// <summary>Stable sort; ties are broken by name so the order never jumps around between refreshes.</summary>
    public static IReadOnlyList<PlanItem> Sort(IEnumerable<PlanItem> items, AppSortKey key, bool descending)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        if (key == AppSortKey.Default)
        {
            return descending ? Enumerable.Reverse(list).ToList() : list;
        }

        var name = StringComparer.Create(Localization.Loc.Culture, ignoreCase: true);
        IOrderedEnumerable<PlanItem> ordered = key switch
        {
            AppSortKey.Name => descending
                ? list.OrderByDescending(i => i.Group.DisplayName, name)
                : list.OrderBy(i => i.Group.DisplayName, name),
            AppSortKey.Memory => descending ? list.OrderByDescending(MemoryOf) : list.OrderBy(MemoryOf),
            AppSortKey.Processes => descending ? list.OrderByDescending(i => i.Group.ProcessCount) : list.OrderBy(i => i.Group.ProcessCount),
            AppSortKey.Risk => descending ? list.OrderByDescending(RiskRank) : list.OrderBy(RiskRank),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

        if (key != AppSortKey.Name)
        {
            ordered = ordered.ThenBy(i => i.Group.DisplayName, name);
        }

        return ordered.ThenBy(i => i.Group.Id.Pid).ToList();
    }

    /// <summary>
    /// Case-insensitive search over the app name, its executables (including helper processes),
    /// the install folder name and the detail hint. An empty query matches everything.
    /// </summary>
    public static bool Matches(PlanItem item, string? query)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term => Haystack(item).Any(h => h.Contains(term, StringComparison.CurrentCultureIgnoreCase)));
    }

    public static IReadOnlyList<PlanItem> Filter(IEnumerable<PlanItem> items, string? query) =>
        items.Where(i => Matches(i, query)).ToList();

    private static IEnumerable<string> Haystack(PlanItem item)
    {
        yield return item.Group.DisplayName;
        if (item.Group.Detail is { } detail)
        {
            yield return detail;
        }

        if (item.Group.ExecutablePath is { } path)
        {
            yield return PathUtil.GetFileName(path);
        }

        foreach (var member in item.Group.Members)
        {
            yield return member.ImageName;
        }

        foreach (var reason in item.Reasons)
        {
            yield return RiskReasons.Label(reason);
        }
    }
}
