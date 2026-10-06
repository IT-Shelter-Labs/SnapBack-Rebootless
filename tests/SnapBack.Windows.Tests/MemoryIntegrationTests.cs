using Microsoft.Extensions.Logging.Abstractions;
using SnapBack.Core.Memory;

namespace SnapBack.Windows.Tests;

public class MemoryIntegrationTests
{
    [Fact]
    public async Task Elevated_runner_only_accepts_simple_tokens()
    {
        var runner = new ShellElevatedRunner("SnapBack.exe");
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunElevatedAsync(["--deep-cleanup", "standby & calc"], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunElevatedAsync(["\"--deep-cleanup\""], CancellationToken.None));
    }

    [WindowsOnlyFact]
    public void Memory_reader_reports_physical_memory_and_lists()
    {
        using var reader = new MemoryInfoReader(NullLogger.Instance);
        var m = reader.Read();
        Assert.True(m.IsKnown);
        Assert.InRange(m.AvailablePhysicalBytes, 1, m.TotalPhysicalBytes);
        Assert.True(m.CommitLimitBytes > 0);
        Assert.NotNull(m.StandbyBytes);
        Assert.NotNull(m.FreeBytes);
        Assert.NotNull(m.CachedBytes);

        // Standby and free pages are part of "available" (allow for movement between reads).
        Assert.True(m.StandbyBytes + m.FreeBytes <= m.AvailablePhysicalBytes + (512L << 20));
    }

    [WindowsOnlyFact]
    public void Purge_without_elevation_is_refused_by_Windows()
    {
        if (WindowsSessionInfo.Create().SelfElevated)
        {
            return; // covered by the elevated test below
        }

        var outcome = new StandbyListPurger(NullLogger.Instance).Purge(DeepCleanupMode.LowPriorityStandby);
        Assert.Equal(DeepCleanupStatus.PrivilegeMissing, outcome.Status);
    }

    /// <summary>Really purges the standby list; only runs where explicitly allowed (CI sets SNAPBACK_TEST_PURGE=1).</summary>
    [WindowsOnlyFact]
    public async Task Elevated_purge_clears_the_standby_cache()
    {
        if (Environment.GetEnvironmentVariable("SNAPBACK_TEST_PURGE") != "1" || !WindowsSessionInfo.Create().SelfElevated)
        {
            return;
        }

        using var reader = new MemoryInfoReader(NullLogger.Instance);
        var cleaner = new MemoryCleaner(true, new StandbyListPurger(NullLogger.Instance), new ShellElevatedRunner("unused"));
        var service = new DeepCleanupService(reader.Read, cleaner, TimeProvider.System, NullLogger.Instance);

        var low = await service.RunAsync(DeepCleanupMode.LowPriorityStandby);
        Assert.Equal(DeepCleanupStatus.Completed, low.Outcome.Status);

        var full = await service.RunAsync(DeepCleanupMode.Standby);
        Assert.Equal(DeepCleanupStatus.Completed, full.Outcome.Status);
        Assert.True(full.After.StandbyBytes <= full.Before.StandbyBytes);
        Assert.True(full.After.TotalPhysicalBytes == full.Before.TotalPhysicalBytes);
    }
}
