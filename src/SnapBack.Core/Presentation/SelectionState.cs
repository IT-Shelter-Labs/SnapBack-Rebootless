using SnapBack.Core.Model;
using SnapBack.Core.Planning;

namespace SnapBack.Core.Presentation;

/// <summary>
/// Remembers which applications the user ticked or unticked, keyed by the app instance's root
/// identity (PID + creation time), so sorting, searching and refreshing never lose a choice.
/// Items the user never touched follow the planner's default. Non-selectable items are never selected.
/// </summary>
public sealed class SelectionState
{
    private readonly Dictionary<ProcessIdentity, bool> _choices = new();

    public bool IsSelected(PlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsSelectable)
        {
            return false;
        }

        return _choices.TryGetValue(item.Group.Id, out var chosen) ? chosen : item.DefaultSelected;
    }

    public void Set(PlanItem item, bool selected)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsSelectable)
        {
            _choices[item.Group.Id] = selected;
        }
    }

    public bool HasExplicitChoice(ProcessIdentity id) => _choices.ContainsKey(id);

    /// <summary>Forgets choices for app instances that are no longer running.</summary>
    public void Prune(IEnumerable<ProcessIdentity> live)
    {
        var keep = live.ToHashSet();
        foreach (var gone in _choices.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _choices.Remove(gone);
        }
    }

    public void Clear() => _choices.Clear();

    public IReadOnlyList<PlanItem> Selected(IEnumerable<PlanItem> items) => items.Where(IsSelected).ToList();
}
