using Microsoft.Extensions.Logging.Abstractions;
using SnapBack.Core.Baselines;
using SnapBack.Core.Execution;
using SnapBack.Core.Memory;
using SnapBack.Core.Model;
using SnapBack.Core.Persistence;
using SnapBack.Core.Tests.Fakes;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public sealed class MemoryTests : IDisposable
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snapback-mem-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void Memory_status_distinguishes_use_from_cache()
    {
        var m = new MemoryStatus(32 * GiB, 14 * GiB) { StandbyBytes = 9 * GiB, ModifiedBytes = GiB / 2, FreeBytes = 5 * GiB };
        Assert.Equal(18 * GiB, m.UsedPhysicalBytes);
        Assert.Equal(9 * GiB + GiB / 2, m.CachedBytes);
        Assert.Equal(56.25, m.UsedPercent, 2);

        var fallback = new MemoryStatus(8 * GiB, 4 * GiB) { SystemCacheBytes = 3 * GiB };
        Assert.Equal(3 * GiB, fallback.CachedBytes);
        Assert.False(MemoryStatus.Unknown.IsKnown);
    }

    [Theory]
    [InlineData(32L, 14L, MemoryPressure.Healthy)]
    [InlineData(16L, 8L, MemoryPressure.Healthy)]
    [InlineData(16L, 2.5, MemoryPressure.Moderate)]
    [InlineData(32L, 5L, MemoryPressure.Moderate)]
    [InlineData(16L, 1.2, MemoryPressure.Low)]
    [InlineData(4L, 0.8, MemoryPressure.Low)]
    public void Advisor_judges_available_memory_not_cache(double totalGb, double availableGb, MemoryPressure expected)
    {
        var m = new MemoryStatus((long)(totalGb * GiB), (long)(availableGb * GiB)) { StandbyBytes = (long)(availableGb * GiB * 0.8) };
        Assert.Equal(expected, MemoryAdvisor.Assess(m));
    }

    [Fact]
    public void Deep_cleanup_is_never_recommended_as_necessary()
    {
        foreach (var available in new[] { 0.2, 1, 3, 10 })
        {
            var advice = MemoryAdvisor.Advise(new MemoryStatus(16 * GiB, (long)(available * GiB)) { StandbyBytes = (long)(available * GiB) });
            Assert.False(advice.DeepCleanupRecommended);
        }

        Assert.Equal(MemoryPressure.Unknown, MemoryAdvisor.Advise(MemoryStatus.Unknown).Pressure);
        Assert.False(MemoryAdvisor.Advise(new MemoryStatus(16 * GiB, 12 * GiB)).SuggestClosingApps);
        Assert.True(MemoryAdvisor.Advise(new MemoryStatus(16 * GiB, GiB / 2)).SuggestClosingApps);
    }

    [Fact]
    public void Restore_comparison_reports_before_and_after_and_keeps_sources_apart()
    {
        var before = new SessionStatistics(28, 282, new MemoryStatus(32 * GiB, 14 * GiB) { StandbyBytes = 6 * GiB, ModifiedBytes = 0 }, Boot);
        var after = new SessionStatistics(17, 194, new MemoryStatus(32 * GiB, 22 * GiB) { StandbyBytes = 7 * GiB, ModifiedBytes = 0 }, Boot);
        var cmp = new RestoreComparison(before, after, ReleasedByAppsBytes: 7 * GiB);

        var rows = cmp.Rows.ToDictionary(r => r.LabelKey);
        Assert.Equal(-11, rows["Cmp_Apps"].Delta);
        Assert.Equal(-88, rows["Cmp_Processes"].Delta);
        Assert.Equal(18 * GiB, rows["Cmp_InUse"].Before);
        Assert.Equal(10 * GiB, rows["Cmp_InUse"].After);
        Assert.Equal(8 * GiB, rows["Cmp_Available"].Delta);
        Assert.Equal(ComparisonValueKind.Bytes, rows["Cmp_Cached"].Kind);
        Assert.Null(cmp.DeepCleanup);
        Assert.Equal(7 * GiB, cmp.ReleasedByAppsBytes);

        var cleanup = new DeepCleanupReport(DeepCleanupMode.Standby, DeepCleanupOutcome.For(DeepCleanupStatus.Completed),
            after.Memory, after.Memory with { StandbyBytes = GiB }, Boot);
        var withCleanup = cmp with { DeepCleanup = cleanup };
        Assert.Equal(7 * GiB, withCleanup.ReleasedByAppsBytes); // unchanged by the cleanup
        Assert.Equal(6 * GiB, withCleanup.DeepCleanup!.CacheReclaimedBytes);
        Assert.Equal(cmp.Rows.Select(r => r.After), withCleanup.Rows.Select(r => r.After));
    }

    [Fact]
    public void Unknown_memory_shows_as_missing_rather_than_zero()
    {
        var unknown = new SessionStatistics(3, 40, MemoryStatus.Unknown, Boot);
        var rows = new RestoreComparison(unknown, unknown, 0).Rows.ToDictionary(r => r.LabelKey);
        Assert.Null(rows["Cmp_InUse"].Before);
        Assert.Null(rows["Cmp_Available"].Delta);
        Assert.Equal(3, rows["Cmp_Apps"].Before);
    }

    [Fact]
    public async Task Memory_before_and_after_a_real_return_to_baseline()
    {
        var telegram = App(4000, @"C:\Apps\Telegram\Telegram.exe", 100, parent: 1100, visibleWindows: 1, memoryMb: 300);
        var discord = App(4100, @"C:\Apps\Discord\Discord.exe", 100, parent: 1100, visibleWindows: 1, memoryMb: 600);
        var discordHelper = App(4101, @"C:\Apps\Discord\Discord.exe", 100.1, parent: 4100, memoryMb: 200);
        var system = new FakeSystem([.. FreshSession(), telegram, discord, discordHelper]);
        var engine = CreateEngine(system);

        // Baseline before the apps existed.
        system.Kill(4000);
        system.Kill(4100);
        engine.CreateBaseline(BaselineOrigin.Manual);
        system.Replace(telegram);
        system.Replace(discord);
        system.Replace(discordHelper);

        var analysis = engine.Analyze();
        var before = SessionStatistics.From(analysis);
        var operation = engine.StartOperation(analysis, analysis.Plan!.Candidates);
        var report = await operation.RunGracefulAsync();
        var after = SessionStatistics.From(engine.Analyze());
        var cmp = new RestoreComparison(before, after, report.MemoryFreedBytes);

        Assert.Equal(before.Apps - 2, after.Apps);
        Assert.Equal(before.Processes - 3, after.Processes);
        Assert.Equal(1100 * MiB, cmp.ReleasedByAppsBytes);
        Assert.Equal(1100 * MiB, before.Memory.UsedPhysicalBytes - after.Memory.UsedPhysicalBytes);
        Assert.Equal(1100 * MiB, after.Memory.AvailablePhysicalBytes - before.Memory.AvailablePhysicalBytes);
    }

    private SnapBackEngine CreateEngine(FakeSystem system)
    {
        var (settings, profiles, history) = StoreFactory.Create(new AppPaths(_dir), NullLoggerFactory.Instance);
        return new SnapBackEngine(system, system, settings, profiles, history, NullLoggerFactory.Instance);
    }
}
