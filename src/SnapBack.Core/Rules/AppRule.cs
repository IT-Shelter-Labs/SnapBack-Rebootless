namespace SnapBack.Core.Rules;

/// <summary>A standing decision of the user about one application.</summary>
public enum AppRuleKind
{
    /// <summary>Never offered for closing, in every profile.</summary>
    AlwaysKeep,

    /// <summary>
    /// Pre-selected for closing whenever the session is returned to a profile, even if the app was
    /// running when the profile was captured. Closing still goes through every safety check, is
    /// polite first, and forced termination still needs its own confirmation.
    /// </summary>
    AlwaysClose,

    /// <summary>Offered for closing but never pre-selected: the user decides each time.</summary>
    AskEveryTime,
}

/// <summary>A rule for the application identified by <paramref name="AppKey"/>.</summary>
public sealed record AppRule(string AppKey, string DisplayName, AppRuleKind Kind, DateTime UpdatedAtUtc);

public static class AppRules
{
    /// <summary>Indexes rules by app key (the last rule for a key wins).</summary>
    public static IReadOnlyDictionary<string, AppRuleKind> ToLookup(IEnumerable<AppRule> rules)
    {
        var map = new Dictionary<string, AppRuleKind>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            map[r.AppKey] = r.Kind;
        }

        return map;
    }

    /// <summary>Returns <paramref name="rules"/> with the rule for <paramref name="appKey"/> set (or removed when <paramref name="kind"/> is null).</summary>
    public static List<AppRule> With(IEnumerable<AppRule> rules, string appKey, string displayName, AppRuleKind? kind, DateTime nowUtc)
    {
        var list = rules.Where(r => r.AppKey != appKey).ToList();
        if (kind is { } k)
        {
            list.Add(new AppRule(appKey, displayName, k, nowUtc));
        }

        return list;
    }
}
