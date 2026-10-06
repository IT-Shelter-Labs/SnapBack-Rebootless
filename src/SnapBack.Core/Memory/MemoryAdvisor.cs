using SnapBack.Core.Localization;
using SnapBack.Core.Model;

namespace SnapBack.Core.Memory;

public enum MemoryPressure
{
    Unknown,
    Healthy,
    Moderate,
    Low,
}

public sealed record MemoryAdvice(MemoryPressure Pressure, string Title, string Text)
{
    /// <summary>
    /// Deep Memory Cleanup is never presented as necessary: purging the standby cache does not
    /// increase available memory, because that cache is already available.
    /// </summary>
    public bool DeepCleanupRecommended => false;

    /// <summary>Whether closing applications would meaningfully help.</summary>
    public bool SuggestClosingApps => Pressure is MemoryPressure.Moderate or MemoryPressure.Low;
}

/// <summary>Explains the memory situation in plain words, distinguishing real use from reusable cache.</summary>
public static class MemoryAdvisor
{
    private const long GiB = 1L << 30;

    public static MemoryPressure Assess(MemoryStatus memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (!memory.IsKnown)
        {
            return MemoryPressure.Unknown;
        }

        var available = memory.AvailablePhysicalBytes;
        var total = memory.TotalPhysicalBytes;
        if (available < Math.Max(1 * GiB, total / 10))
        {
            return MemoryPressure.Low;
        }

        if (available < Math.Max(2 * GiB, total / 5))
        {
            return MemoryPressure.Moderate;
        }

        return MemoryPressure.Healthy;
    }

    public static MemoryAdvice Advise(MemoryStatus memory)
    {
        var pressure = Assess(memory);
        var key = pressure.ToString();
        return new MemoryAdvice(pressure, Loc.Get($"Advice_{key}_Title"), Loc.Get($"Advice_{key}_Text"));
    }
}
