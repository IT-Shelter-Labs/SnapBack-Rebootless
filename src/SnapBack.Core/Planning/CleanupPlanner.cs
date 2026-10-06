using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Grouping;

namespace SnapBack.Core.Planning;

public sealed record PlannerOptions
{
    /// <summary>App keys the user always wants to keep (their whitelist).</summary>
    public IReadOnlySet<string> KeepAppKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Pre-select <see cref="SafetyLevel.Caution"/> items as well (off by default).</summary>
    public bool PreselectCaution { get; init; }
}

/// <summary>Builds the list of applications that could be closed to return to the baseline.</summary>
public sealed class CleanupPlanner
{
    public CleanupPlan Build(IReadOnlyList<AppGroup> groups, ComparisonResult comparison, PlannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(options);

        var candidates = new List<PlanItem>();
        var kept = new List<PlanItem>();
        var protectedItems = new List<PlanItem>();

        foreach (var g in groups)
        {
            var cmp = comparison.For(g);

            if (g.IsProtected)
            {
                protectedItems.Add(Item(g, PlanCategory.Protected, SafetyLevel.Protected, g.Assessment.Reasons, cmp, KeepReason.None, false));
                continue;
            }

            if (cmp.IsPartOfBaseline)
            {
                var why = cmp.Status == GroupStatus.InBaseline ? KeepReason.InBaseline : KeepReason.RestartedSinceBaseline;
                kept.Add(Item(g, PlanCategory.Kept, g.Assessment.Level, g.Assessment.Reasons, cmp, why, false));
                continue;
            }

            if (options.KeepAppKeys.Contains(g.AppKey))
            {
                kept.Add(Item(g, PlanCategory.Kept, g.Assessment.Level, g.Assessment.Reasons, cmp, KeepReason.UserKeepList, false));
                continue;
            }

            var b = new AssessmentBuilder().AddRange(g.Assessment.Reasons);
            if (cmp.Status == GroupStatus.NewInstanceOfBaselineApp)
            {
                b.Add(RiskReason.NewInstanceOfBaselineApp);
            }

            if (cmp.StartedByBaselineApp is not null)
            {
                b.Add(RiskReason.StartedByBaselineApp);
            }

            var assessment = b.Build();
            if (assessment.IsProtected || !g.ActionableMembers.Any())
            {
                // Defensive: should not happen for a non-protected group, but never offer it.
                protectedItems.Add(Item(g, PlanCategory.Protected, SafetyLevel.Protected, assessment.Reasons, cmp, KeepReason.None, false));
                continue;
            }

            var preselect = assessment.Level == SafetyLevel.Safe
                || (assessment.Level == SafetyLevel.Caution && options.PreselectCaution);

            candidates.Add(Item(g, PlanCategory.Candidate, assessment.Level, assessment.Reasons, cmp, KeepReason.None, preselect));
        }

        return new CleanupPlan
        {
            Candidates = candidates
                .OrderBy(i => i.Level)
                .ThenByDescending(i => i.Group.ActionablePrivateWorkingSet)
                .ToList(),
            Kept = kept.OrderByDescending(i => i.Group.PrivateWorkingSet).ToList(),
            Protected = protectedItems.OrderByDescending(i => i.Group.PrivateWorkingSet).ToList(),
        };
    }

    private static PlanItem Item(AppGroup g, PlanCategory category, SafetyLevel level, IReadOnlyList<RiskReason> reasons,
        GroupComparison cmp, KeepReason keep, bool preselect) => new()
        {
            Group = g,
            Category = category,
            Level = level,
            Reasons = reasons,
            Comparison = cmp,
            KeepReason = keep,
            DefaultSelected = preselect,
            Steps = category == PlanCategory.Candidate ? CloseStrategy.Plan(g) : [],
            Notes = category == PlanCategory.Candidate ? CloseStrategy.Notes(g) : [],
        };
}
