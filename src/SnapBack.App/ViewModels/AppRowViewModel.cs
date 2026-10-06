using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.App.Services;
using SnapBack.Core.Classification;
using SnapBack.Core.Insights;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
using SnapBack.Core.Presentation;
using SnapBack.Core.Rules;
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

    public AppRowViewModel(PlanItem item, SelectionState selection, Action? selectionChanged,
        Action<AppRowViewModel, AppRuleKind?>? setRule = null, GrowthAssessment? growth = null)
    {
        Item = item;
        _selection = selection;
        _selectionChanged = selectionChanged;
        Growth = growth;
        var canRule = setRule is not null && item.Category != PlanCategory.Protected;
        RuleKeepCommand = new RelayCommand(() => setRule!(this, AppRuleKind.AlwaysKeep), () => canRule && item.Rule != AppRuleKind.AlwaysKeep);
        RuleCloseCommand = new RelayCommand(() => setRule!(this, AppRuleKind.AlwaysClose), () => canRule && item.Rule != AppRuleKind.AlwaysClose);
        RuleAskCommand = new RelayCommand(() => setRule!(this, AppRuleKind.AskEveryTime), () => canRule && item.Rule != AppRuleKind.AskEveryTime);
        RuleClearCommand = new RelayCommand(() => setRule!(this, null), () => canRule && item.Rule is not null);
    }

    public ICommand RuleKeepCommand { get; }

    public ICommand RuleCloseCommand { get; }

    public ICommand RuleAskCommand { get; }

    public ICommand RuleClearCommand { get; }

    /// <summary>Whether rules can be set for this row (not for protected components).</summary>
    public bool CanHaveRule => Item.Category != PlanCategory.Protected;

    public string? RuleText => Item.Rule is { } r ? Tr.Format("Row_Rule", Tr.Get("Rule_" + r)) : null;

    public GrowthAssessment? Growth { get; }

    /// <summary>"Unusual memory growth: 420 MB → 1.4 GB in 9 h" (never called a leak).</summary>
    public string? GrowthText => Growth is { Level: not GrowthLevel.None } g
        ? Tr.Format(g.Level == GrowthLevel.Unusual ? "Growth_Unusual_Row" : "Growth_Growing_Row",
            ByteSize.Format(g.FromBytes), ByteSize.Format(g.ToBytes), Math.Max(1, (int)Math.Round(g.Span.TotalHours)))
        : null;

    public PlanItem Item { get; }

    public ProcessIdentity Id => Item.Group.Id;

    public string Name => Item.Group.DisplayName;

    public string Subtitle
    {
        get
        {
            var detail = Item.Group.Detail ?? Item.Group.WindowTitles.FirstOrDefault();
            var summary = Item.Rule == AppRuleKind.AskEveryTime && Item.Category == PlanCategory.Candidate
                ? $"{RuleText}  ·  {Item.Summary}"
                : Item.Summary;
            return string.IsNullOrWhiteSpace(detail) || detail == Name ? summary : $"{summary}  ·  {detail}";
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
            if (RuleText is { } rule)
            {
                sb.AppendLine(rule);
            }

            if (GrowthText is { } growth)
            {
                sb.AppendLine(growth);
                sb.AppendLine(Tr.Get("Growth_Explain"));
            }

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
