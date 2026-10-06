using System.Text;
using System.Windows.Media;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.App.Services;
using SnapBack.Core.Classification;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
using SnapBack.Core.Presentation;
using SnapBack.Core.Text;

namespace SnapBack.App.ViewModels;

public enum BadgeKind
{
    Safe,
    Caution,
    Review,
    Protected,
    Kept,
}

/// <summary>One application row. Its checkbox state lives in the shared <see cref="SelectionState"/>.</summary>
public sealed class AppRowViewModel : ObservableObject
{
    private readonly SelectionState _selection;
    private readonly Action? _selectionChanged;
    private ImageSource? _icon;
    private bool _iconLoaded;

    public AppRowViewModel(PlanItem item, SelectionState selection, Action? selectionChanged)
    {
        Item = item;
        _selection = selection;
        _selectionChanged = selectionChanged;
    }

    public PlanItem Item { get; }

    public ProcessIdentity Id => Item.Group.Id;

    public string Name => Item.Group.DisplayName;

    public string Subtitle
    {
        get
        {
            var detail = Item.Group.Detail ?? Item.Group.WindowTitles.FirstOrDefault();
            return string.IsNullOrWhiteSpace(detail) || detail == Name ? Item.Summary : $"{Item.Summary}  ·  {detail}";
        }
    }

    public long MemoryBytes => AppListOrdering.MemoryOf(Item);

    public string MemoryText => ByteSize.Format(MemoryBytes);

    public int ProcessCount => Item.Group.ProcessCount;

    public BadgeKind Badge => BadgeFor(Item).Kind;

    public string BadgeText => BadgeFor(Item).Text;

    public bool IsSelectable => Item.IsSelectable;

    public bool IsSelected
    {
        get => _selection.IsSelected(Item);
        set
        {
            if (Item.IsSelectable && value != _selection.IsSelected(Item))
            {
                _selection.Set(Item, value);
                OnPropertyChanged();
                _selectionChanged?.Invoke();
            }
        }
    }

    /// <summary>Re-reads the selection (after "select suggested" / "clear").</summary>
    public void RefreshSelection() => OnPropertyChanged(nameof(IsSelected));

    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = IconCache.Get(Item.Group.ExecutablePath);
            }

            return _icon;
        }
    }

    public string ToolTipText
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine(Name);
            if (Item.Group.ExecutablePath is { } path)
            {
                sb.AppendLine(path);
            }

            if (Item.Group.LaunchedByName is { } launcher)
            {
                sb.AppendLine(Tr.Format("Row_StartedBy", launcher));
            }

            sb.AppendLine(Tr.Format("Row_ProcessesMemory", ProcessCount, MemoryText));
            foreach (var reason in Item.Reasons.Distinct())
            {
                sb.AppendLine("• " + RiskReasons.Label(reason) + ": " + RiskReasons.Explain(reason));
            }

            foreach (var title in Item.Group.WindowTitles.Take(5))
            {
                sb.AppendLine(Tr.Format("Row_Window", title));
            }

            return sb.ToString().TrimEnd();
        }
    }

    internal static (BadgeKind Kind, string Text) BadgeFor(PlanItem item)
    {
        if (item.Category == PlanCategory.Kept)
        {
            return (BadgeKind.Kept, item.Summary);
        }

        if (item.Category == PlanCategory.Protected || item.Level == SafetyLevel.Protected)
        {
            return (BadgeKind.Protected, Tr.Get("Badge_Protected"));
        }

        return item.Level switch
        {
            SafetyLevel.Safe => (BadgeKind.Safe, Tr.Get("Badge_Safe")),
            SafetyLevel.Caution => (BadgeKind.Caution, item.Reasons
                .Where(r => RiskReasons.LevelOf(r) == SafetyLevel.Caution)
                .Select(RiskReasons.Label)
                .FirstOrDefault() ?? Tr.Get("Badge_Caution")),
            _ => (BadgeKind.Review, Tr.Get("Badge_ManualReview")),
        };
    }
}
