namespace SnapBack.Core.Model;

/// <summary>
/// Identifies one specific process instance.
/// </summary>
/// <remarks>
/// Windows recycles process IDs, so a PID on its own is never trusted anywhere in SnapBack.
/// The pair (PID, creation time) is unique for the lifetime of a boot session: a recycled PID
/// always belongs to a process with a later creation time.
/// </remarks>
/// <param name="Pid">The process ID.</param>
/// <param name="CreationTime">Process creation time as a UTC FILETIME (100 ns ticks since 1601-01-01).</param>
public readonly record struct ProcessIdentity(int Pid, long CreationTime)
{
    /// <summary>Creation time as a <see cref="DateTime"/> in UTC (MinValue when unknown).</summary>
    public DateTime CreationTimeUtc =>
        CreationTime > 0 ? DateTime.FromFileTimeUtc(CreationTime) : DateTime.MinValue;

    public override string ToString() => $"PID {Pid} @ {CreationTime}";
}
