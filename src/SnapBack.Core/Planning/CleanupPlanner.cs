using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Grouping;
using SnapBack.Core.Rules;

namespace SnapBack.Core.Planning;

public sealed record PlannerOptions
{
    /// <summary>App keys the user always wants to keep (same as an <see cref="AppRuleKind.AlwaysKeep"/> rule).</summary>
    public IReadOnlySet<string> KeepAppKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The user's standing rules by app key.</summary>
    public IReadOnlyDictionary<string, AppRuleKind> Rules { get; init; } = new Dictionary<string, AppRuleKind>(StringComparer.Ordinal);

    /// <summary>Pre-select <see cref="SafetyLevel.Caution"/> items as well (off by default).</summary>
    public bool PreselectCaution { get; init; }

    public AppRuleKind? RuleFor(string appKey) =>
        Rules.TryGetValue(appKey, out var kind) ? kind : KeepAppKeys.Contains(appKey) ? AppRuleKind.AlwaysKeep : null;
}

/// <summary>Builds the list of applications that could be closed to return to the baseline.</summary>
/// <remarks>
/// Precedence: protection always wins and no rule can change it. Then an "always close" rule beats
/// membership in the profile, membership in the profile (or an "always keep" rule) keeps the app,
/// and everything else is a candidate. Rules only decide what is offered and pre-selected; how an
/// app is closed and every safety check stay exactly the same.
/// </remarks>
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
            var rule = options.RuleFor(g.AppKey);

            if (g.IsProtected)
            {
                protectedItems.Add(Item(g, PlanCategory.Protected, SafetyLevel.Protected, g.Assessment.Reasons, cmp, KeepReason.None, false, rule));
                continue;
            }

            if (cmp.IsPartOfBaseline && rule != AppRuleKind.AlwaysClose)
            {
                var why = cmp.Status == GroupStatus.InBaseline ? KeepReason.InBaseline : KeepReason.RestartedSinceBaseline;
                kept.Add(Item(g, PlanCategory.Kept, g.Assessment.Level, g.Assessment.Reasons, cmp, why, false, rule));
                continue;
            }

            if (rule == AppRuleKind.AlwaysKeep)
            {
                kept.Add(Item(g, PlanCategory.Kept, g.Assessment.Level, g.Assessment.Reasons, cmp, KeepReason.UserKeepList, false, rule));
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
                protectedItems.Add(Item(g, PlanCategory.Protected, SafetyLevel.Protected, assessment.Reasons, cmp, KeepReason.None, false, rule));
                continue;
            }

            var preselect = rule switch
            {
                AppRuleKind.AlwaysClose => true,
                AppRuleKind.AskEveryTime => false,
                _ => assessment.Level == SafetyLevel.Safe || (assessment.Level == SafetyLevel.Caution && options.PreselectCaution),
            };

            candidates.Add(Item(g, PlanCategory.Candidate, assessment.Level, assessment.Reasons, cmp, KeepReason.None, preselect, rule));
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
        GroupComparison cmp, KeepReason keep, bool preselect, AppRuleKind? rule) => new()
        {
            Rule = rule,
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
