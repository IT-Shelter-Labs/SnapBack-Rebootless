namespace SnapBack.Core.Classification;

/// <summary>How safe it is to close something. Ordered from least to most restrictive.</summary>
public enum SafetyLevel
{
    /// <summary>A normal user application that can be asked to close. Pre-selected for cleanup.</summary>
    Safe = 0,

    /// <summary>Closing is possible but may cost something (unsaved work, downloads, ...). Not pre-selected by default.</summary>
    Caution = 1,

    /// <summary>The user should look at it first (scripts, consoles, background processes). Never pre-selected.</summary>
    ManualReview = 2,

    /// <summary>SnapBack never acts on it, whatever the user or the UI asks for.</summary>
    Protected = 3,
}
