using System.Text;
using System.Windows.Media;
using SnapBack.App.Mvvm;
using SnapBack.App.Services;
using SnapBack.Core.Classification;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
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

/// <summary>One application row in the main list.</summary>
public sealed class AppRowViewModel : ObservableObject
{
    private readonly Action? _selectionChanged;
    private bool _isSelected;
    private ImageSource? _icon;
    private bool _iconLoaded;

    public AppRowViewModel(PlanItem item, bool isSelected, Action? selectionChanged)
    {
        Item = item;
        _isSelected = item.IsSelectable && isSelected;
        _selectionChanged = selectionChanged;
        (Badge, BadgeText) = BadgeFor(item);
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

    public long MemoryBytes => Item.IsSelectable ? Item.Group.ActionablePrivateWorkingSet : Item.Group.PrivateWorkingSet;

    public string MemoryText => ByteSize.Format(MemoryBytes);

    public int ProcessCount => Item.Group.ProcessCount;

    public string ProcessCountText => ProcessCount == 1 ? "1 process" : $"{ProcessCount} processes";

    public BadgeKind Badge { get; }

    public string BadgeText { get; }

    public bool IsSelectable => Item.IsSelectable;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Item.IsSelectable && Set(ref _isSelected, value))
            {
                _selectionChanged?.Invoke();
            }
        }
    }

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
                sb.AppendLine("Started by: " + launcher);
            }

            sb.AppendLine($"{ProcessCountText}, {MemoryText}");
            foreach (var reason in Item.Reasons.Distinct())
            {
                sb.AppendLine("• " + RiskReasons.Label(reason) + ": " + RiskReasons.Explain(reason));
            }

            foreach (var title in Item.Group.WindowTitles.Take(5))
            {
                sb.AppendLine("Window: " + title);
            }

            return sb.ToString().TrimEnd();
        }
    }

    internal static (BadgeKind, string) BadgeFor(PlanItem item)
    {
        if (item.Category == PlanCategory.Kept)
        {
            return (BadgeKind.Kept, item.Summary);
        }

        if (item.Category == PlanCategory.Protected || item.Level == SafetyLevel.Protected)
        {
            return (BadgeKind.Protected, "Protected");
        }

        return item.Level switch
        {
            SafetyLevel.Safe => (BadgeKind.Safe, "Safe"),
            SafetyLevel.Caution => (BadgeKind.Caution, item.Reasons
                .Where(r => RiskReasons.LevelOf(r) == SafetyLevel.Caution)
                .Select(RiskReasons.Label)
                .FirstOrDefault() ?? "Caution"),
            _ => (BadgeKind.Review, "Manual review"),
        };
    }
}
