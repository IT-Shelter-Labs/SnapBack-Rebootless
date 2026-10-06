using SnapBack.Core.Model;

namespace SnapBack.Core.Baselines;

public enum BaselineOrigin
{
    Manual,
    Automatic,
}

/// <summary>
/// The "clean" state of the user session: which applications were running, not which PIDs.
/// </summary>
public sealed record Baseline
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTime CreatedAtUtc { get; init; }

    public BaselineOrigin Origin { get; init; }

    public required string UserSid { get; init; }

    public int SessionId { get; init; }

    /// <summary>Boot time when the baseline was taken; used to tell whether PIDs are still comparable.</summary>
    public DateTime BootTimeUtc { get; init; }

    public string MachineName { get; init; } = "";

    public List<BaselineApp> Apps { get; init; } = [];

    public int ProcessCount { get; init; }

    public long PrivateWorkingSet { get; init; }

    public long AvailableMemoryBytes { get; init; }
}

/// <summary>One application of the baseline.</summary>
public sealed record BaselineApp
{
    public required string AppKey { get; init; }

    public required string DisplayName { get; init; }

    public string? ExecutablePath { get; init; }

    /// <summary>Root processes of each running instance at baseline time.</summary>
    public List<ProcessIdentity> Instances { get; init; } = [];

    public int ProcessCount { get; init; }

    public long PrivateWorkingSet { get; init; }

    public bool WasProtected { get; init; }
}
