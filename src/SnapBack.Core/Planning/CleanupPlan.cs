using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Grouping;

namespace SnapBack.Core.Planning;

public enum PlanCategory
{
    /// <summary>Appeared after the baseline; may be closed.</summary>
    Candidate,

    /// <summary>Part of the baseline or of the user's keep list; never offered for closing.</summary>
    Kept,

    /// <summary>Protected; never offered and never acted on.</summary>
    Protected,
}

public enum KeepReason
{
    None,
    InBaseline,
    RestartedSinceBaseline,
    UserKeepList,
}

public sealed class PlanItem
{
    public required AppGroup Group { get; init; }

    public required PlanCategory Category { get; init; }

    public required SafetyLevel Level { get; init; }

    public required IReadOnlyList<RiskReason> Reasons { get; init; }

    public GroupComparison? Comparison { get; init; }

    public KeepReason KeepReason { get; init; }

    /// <summary>Whether the item is ticked when the list is first shown.</summary>
    public bool DefaultSelected { get; init; }

    public required IReadOnlyList<PlannedStep> Steps { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Whether this item may be acted on at all (and therefore shown with a checkbox).</summary>
    public bool IsSelectable => Category == PlanCategory.Candidate && Level != SafetyLevel.Protected;

    /// <summary>Short explanation for the "why" column.</summary>
    public string Summary
    {
        get
        {
            switch (Category)
            {
                case PlanCategory.Kept:
                    return KeepReason switch
                    {
                        KeepReason.InBaseline => "Was present in baseline",
                        KeepReason.RestartedSinceBaseline => "Was present in baseline (restarted since)",
                        KeepReason.UserKeepList => "In your keep list",
                        _ => "Kept",
                    };
                case PlanCategory.Protected:
                    return Reasons.Count > 0 ? RiskReasons.Label(Reasons.OrderByDescending(RiskReasons.LevelOf).First()) : "Protected";
            }

            var start = Comparison?.Status switch
            {
                GroupStatus.NewInstanceOfBaselineApp => "Extra instance since baseline",
                _ when Comparison?.StartedByBaselineApp is { } p => $"Started by {p} after baseline",
                _ => "Started after baseline",
            };

            var concern = Reasons
                .Where(r => RiskReasons.LevelOf(r) > SafetyLevel.Safe
                    && r is not RiskReason.NewInstanceOfBaselineApp and not RiskReason.StartedByBaselineApp)
                .OrderByDescending(RiskReasons.LevelOf)
                .Select(RiskReasons.Label)
                .FirstOrDefault();

            return concern is null ? start : $"{start} · {concern}";
        }
    }

    public override string ToString() => $"{Group.DisplayName} [{Category}/{Level}]";
}

public sealed class CleanupPlan
{
    public required IReadOnlyList<PlanItem> Candidates { get; init; }

    public required IReadOnlyList<PlanItem> Kept { get; init; }

    public required IReadOnlyList<PlanItem> Protected { get; init; }

    public IEnumerable<PlanItem> All => Candidates.Concat(Kept).Concat(Protected);

    public PlanItem? Find(Model.ProcessIdentity groupId) => All.FirstOrDefault(i => i.Group.Id == groupId);
}
