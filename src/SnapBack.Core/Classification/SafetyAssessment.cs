namespace SnapBack.Core.Classification;

/// <summary>Safety assessment of a process or application.</summary>
public sealed record SafetyAssessment(SafetyLevel Level, IReadOnlyList<RiskReason> Reasons)
{
    public static SafetyAssessment Safe { get; } = new(SafetyLevel.Safe, []);

    public bool IsProtected => Level == SafetyLevel.Protected;

    /// <summary>The most severe reason (first one with the highest level), if any.</summary>
    public RiskReason? PrimaryReason =>
        Reasons.Count == 0 ? null : Reasons.OrderByDescending(RiskReasons.LevelOf).First();
}

/// <summary>Accumulates reasons and derives the resulting level.</summary>
public sealed class AssessmentBuilder
{
    private readonly List<RiskReason> _reasons = [];

    public SafetyLevel Level { get; private set; } = SafetyLevel.Safe;

    public AssessmentBuilder Add(RiskReason reason) => Add(reason, SafetyLevel.Protected);

    /// <summary>Adds a reason but lets it raise the level to at most <paramref name="cap"/>.</summary>
    public AssessmentBuilder Add(RiskReason reason, SafetyLevel cap)
    {
        if (!_reasons.Contains(reason))
        {
            _reasons.Add(reason);
        }

        var level = RiskReasons.LevelOf(reason);
        if (level > cap)
        {
            level = cap;
        }

        if (level > Level)
        {
            Level = level;
        }

        return this;
    }

    public AssessmentBuilder AddRange(IEnumerable<RiskReason> reasons)
    {
        foreach (var r in reasons)
        {
            Add(r);
        }

        return this;
    }

    public SafetyAssessment Build() => new(Level, _reasons.ToArray());
}
