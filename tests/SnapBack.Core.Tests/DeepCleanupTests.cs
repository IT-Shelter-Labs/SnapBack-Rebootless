using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SnapBack.Core.Baselines;
using SnapBack.Core.Execution;
using SnapBack.Core.Memory;
using SnapBack.Core.Model;
using SnapBack.Core.Persistence;
using SnapBack.Core.Tests.Fakes;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public class DeepCleanupTests
{
    private const long GiB = 1L << 30;

    private sealed class FakePurger(DeepCleanupStatus result = DeepCleanupStatus.Completed) : IStandbyPurger
    {
        public List<DeepCleanupMode> Calls { get; } = [];

        public DeepCleanupOutcome Purge(DeepCleanupMode mode)
        {
            Calls.Add(mode);
            return DeepCleanupOutcome.For(result);
        }
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, int?> behavior) : IElevatedRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<int?> RunElevatedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            return Task.FromResult(behavior(arguments));
        }
    }

    private sealed class CountingCleaner : IMemoryCleaner
    {
        public int Calls { get; private set; }

        public bool PromptsForElevation => true;

        public Task<DeepCleanupOutcome> CleanAsync(DeepCleanupMode mode, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(DeepCleanupOutcome.For(DeepCleanupStatus.Completed));
        }
    }

    // ---- Command whitelist --------------------------------------------------------------------

    [Fact]
    public void Only_standby_purges_can_ever_be_sent()
    {
        Assert.Equal(4, MemoryListCommand.For(DeepCleanupMode.Standby));
        Assert.Equal(5, MemoryListCommand.For(DeepCleanupMode.LowPriorityStandby));
        foreach (var mode in Enum.GetValues<DeepCleanupMode>())
        {
            Assert.True(MemoryListCommand.IsAllowed(MemoryListCommand.For(mode)));
        }

        // 2 = MemoryEmptyWorkingSets, 3 = MemoryFlushModifiedList: never allowed.
        foreach (var forbidden in new[] { -1, 0, 1, 2, 3, 6, 7, 100 })
        {
            Assert.False(MemoryListCommand.IsAllowed(forbidden));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryListCommand.For((DeepCleanupMode)42));
    }

    // ---- Helper protocol ----------------------------------------------------------------------

    [Theory]
    [InlineData(DeepCleanupMode.Standby)]
    [InlineData(DeepCleanupMode.LowPriorityStandby)]
    public void Helper_arguments_round_trip(DeepCleanupMode mode)
    {
        var args = DeepCleanupHelper.BuildArguments(mode);
        Assert.True(DeepCleanupHelper.IsHelperInvocation(args));
        Assert.True(DeepCleanupHelper.TryParse(args, out var parsed));
        Assert.Equal(mode, parsed);
    }

    public static TheoryData<string[]> InvalidHelperArguments => new()
    {
        Array.Empty<string>(),
        new[] { "--deep-cleanup" },
        new[] { "--deep-cleanup", "Standby" },
        new[] { "--deep-cleanup", "empty-working-sets" },
        new[] { "--deep-cleanup", "flush-modified" },
        new[] { "--deep-cleanup", "standby", "extra" },
        new[] { "--deep-cleanup", "standby;calc.exe" },
        new[] { "--DEEP-CLEANUP", "standby" },
        new[] { "--auto-baseline" },
    };

    [Theory]
    [MemberData(nameof(InvalidHelperArguments))]
    public void Helper_rejects_anything_but_the_exact_protocol(string[] args) =>
        Assert.False(DeepCleanupHelper.TryParse(args, out _));

    [Fact]
    public void Helper_refuses_to_run_without_elevation()
    {
        var purger = new FakePurger();
        var code = DeepCleanupHelper.Run(DeepCleanupHelper.BuildArguments(DeepCleanupMode.Standby), processIsElevated: false, purger, NullLogger.Instance);
        Assert.Equal(DeepCleanupHelper.ExitNotElevated, code);
        Assert.Empty(purger.Calls);
    }

    [Fact]
    public void Helper_rejects_bad_arguments_before_doing_anything()
    {
        var purger = new FakePurger();
        Assert.Equal(DeepCleanupHelper.ExitBadRequest, DeepCleanupHelper.Run(["--deep-cleanup", "everything"], true, purger, NullLogger.Instance));
        Assert.Empty(purger.Calls);
    }

    [Theory]
    [InlineData(DeepCleanupStatus.Completed, DeepCleanupHelper.ExitCompleted)]
    [InlineData(DeepCleanupStatus.PrivilegeMissing, DeepCleanupHelper.ExitPrivilegeMissing)]
    [InlineData(DeepCleanupStatus.Failed, DeepCleanupHelper.ExitFailed)]
    public void Elevated_helper_purges_and_reports_through_its_exit_code(DeepCleanupStatus status, int expectedCode)
    {
        var purger = new FakePurger(status);
        var code = DeepCleanupHelper.Run(DeepCleanupHelper.BuildArguments(DeepCleanupMode.LowPriorityStandby), true, purger, NullLogger.Instance);
        Assert.Equal(expectedCode, code);
        Assert.Equal([DeepCleanupMode.LowPriorityStandby], purger.Calls);
        Assert.Equal(status, DeepCleanupHelper.OutcomeFromExitCode(code).Status);
    }

    [Fact]
    public void Unknown_helper_exit_codes_are_failures() =>
        Assert.Equal(DeepCleanupStatus.Failed, DeepCleanupHelper.OutcomeFromExitCode(-1073741819).Status);

    // ---- UAC / elevation path -----------------------------------------------------------------

    [Fact]
    public async Task Elevated_SnapBack_purges_in_process_without_a_prompt()
    {
        var purger = new FakePurger();
        var runner = new FakeRunner(_ => throw new InvalidOperationException("must not prompt"));
        var cleaner = new MemoryCleaner(processIsElevated: true, purger, runner);

        Assert.False(cleaner.PromptsForElevation);
        var outcome = await cleaner.CleanAsync(DeepCleanupMode.Standby, CancellationToken.None);
        Assert.True(outcome.Succeeded);
        Assert.False(outcome.UsedElevationPrompt);
        Assert.Equal([DeepCleanupMode.Standby], purger.Calls);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Standard_user_gets_one_elevated_helper_for_this_operation_only()
    {
        var purger = new FakePurger();
        var runner = new FakeRunner(_ => DeepCleanupHelper.ExitCompleted);
        var cleaner = new MemoryCleaner(processIsElevated: false, purger, runner);

        Assert.True(cleaner.PromptsForElevation);
        var outcome = await cleaner.CleanAsync(DeepCleanupMode.Standby, CancellationToken.None);
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.UsedElevationPrompt);
        Assert.Empty(purger.Calls); // the purge happens in the elevated helper, not in SnapBack
        var args = Assert.Single(runner.Calls);
        Assert.Equal(["--deep-cleanup", "standby"], args);
    }

    [Fact]
    public async Task Declining_the_UAC_prompt_changes_nothing()
    {
        var cleaner = new MemoryCleaner(false, new FakePurger(), new FakeRunner(_ => null));
        var outcome = await cleaner.CleanAsync(DeepCleanupMode.Standby, CancellationToken.None);
        Assert.Equal(DeepCleanupStatus.CancelledByUser, outcome.Status);
    }

    [Theory]
    [InlineData(DeepCleanupHelper.ExitPrivilegeMissing, DeepCleanupStatus.PrivilegeMissing)]
    [InlineData(DeepCleanupHelper.ExitNotElevated, DeepCleanupStatus.NotElevated)]
    [InlineData(DeepCleanupHelper.ExitFailed, DeepCleanupStatus.Failed)]
    [InlineData(99, DeepCleanupStatus.Failed)]
    public async Task Helper_failures_are_reported(int exitCode, DeepCleanupStatus expected)
    {
        var cleaner = new MemoryCleaner(false, new FakePurger(), new FakeRunner(_ => exitCode));
        Assert.Equal(expected, (await cleaner.CleanAsync(DeepCleanupMode.Standby, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Launch_errors_do_not_crash()
    {
        var cleaner = new MemoryCleaner(false, new FakePurger(), new FakeRunner(_ => throw new System.ComponentModel.Win32Exception(2)));
        var outcome = await cleaner.CleanAsync(DeepCleanupMode.Standby, CancellationToken.None);
        Assert.Equal(DeepCleanupStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task Undefined_modes_are_rejected()
    {
        var purger = new FakePurger();
        var cleaner = new MemoryCleaner(true, purger, new FakeRunner(_ => 0));
        Assert.Equal(DeepCleanupStatus.BadRequest, (await cleaner.CleanAsync((DeepCleanupMode)7, CancellationToken.None)).Status);
        Assert.Empty(purger.Calls);
    }

    // ---- Measurement --------------------------------------------------------------------------

    [Fact]
    public async Task Service_measures_the_cache_that_was_reclaimed()
    {
        var reads = new Queue<MemoryStatus>(
        [
            new MemoryStatus(16 * GiB, 9 * GiB) { StandbyBytes = 6 * GiB, FreeBytes = 3 * GiB },
            new MemoryStatus(16 * GiB, 9 * GiB) { StandbyBytes = GiB, FreeBytes = 8 * GiB },
        ]);
        var service = new DeepCleanupService(() => reads.Dequeue(), new MemoryCleaner(true, new FakePurger(), new FakeRunner(_ => 0)),
            TimeProvider.System, NullLogger.Instance) { SettleDelay = TimeSpan.Zero };

        var report = await service.RunAsync(DeepCleanupMode.Standby);
        Assert.True(report.Outcome.Succeeded);
        Assert.Equal(5 * GiB, report.CacheReclaimedBytes);
        Assert.Equal(0, report.AvailableChangeBytes); // standby was already available
        Assert.Equal(5 * GiB, report.Rows.Single(r => r.LabelKey == "Cmp_Free").Delta);
    }

    [Fact]
    public async Task Without_list_sizes_the_free_memory_gain_is_used()
    {
        var reads = new Queue<MemoryStatus>([new MemoryStatus(16 * GiB, 9 * GiB) { FreeBytes = GiB }, new MemoryStatus(16 * GiB, 9 * GiB) { FreeBytes = 3 * GiB }]);
        var service = new DeepCleanupService(() => reads.Dequeue(), new MemoryCleaner(true, new FakePurger(), new FakeRunner(_ => 0)),
            TimeProvider.System, NullLogger.Instance) { SettleDelay = TimeSpan.Zero };
        Assert.Equal(2 * GiB, (await service.RunAsync(DeepCleanupMode.Standby)).CacheReclaimedBytes);
    }

    [Fact]
    public async Task Failed_or_declined_cleanup_reports_nothing_reclaimed()
    {
        var reads = 0;
        var service = new DeepCleanupService(() =>
            {
                reads++;
                return new MemoryStatus(16 * GiB, 9 * GiB) { StandbyBytes = 6 * GiB };
            },
            new MemoryCleaner(false, new FakePurger(), new FakeRunner(_ => null)), TimeProvider.System, NullLogger.Instance);

        var report = await service.RunAsync(DeepCleanupMode.Standby);
        Assert.Equal(DeepCleanupStatus.CancelledByUser, report.Outcome.Status);
        Assert.Equal(0, report.CacheReclaimedBytes);
        Assert.Same(report.Before, report.After);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Unreadable_memory_does_not_crash_the_cleanup()
    {
        var service = new DeepCleanupService(() => throw new InvalidOperationException("no counters"),
            new MemoryCleaner(true, new FakePurger(), new FakeRunner(_ => 0)), TimeProvider.System, NullLogger.Instance) { SettleDelay = TimeSpan.Zero };
        var report = await service.RunAsync(DeepCleanupMode.Standby);
        Assert.True(report.Outcome.Succeeded);
        Assert.False(report.Before.IsKnown);
        Assert.Equal(0, report.CacheReclaimedBytes);
    }

    // ---- Never part of Return to baseline -----------------------------------------------------

    [Fact]
    public async Task Return_to_baseline_never_runs_a_deep_cleanup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "snapback-deep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = App(4000, @"C:\Apps\Telegram\Telegram.exe", 100, parent: 1100, visibleWindows: 1);
            var system = new FakeSystem([.. FreshSession()]);
            var (settings, baseline, history) = StoreFactory.Create(new AppPaths(dir), NullLoggerFactory.Instance);
            var engine = new SnapBackEngine(system, system, settings, baseline, history, NullLoggerFactory.Instance);
            var cleaner = new CountingCleaner();
            _ = new DeepCleanupService(system.GetMemoryStatus, cleaner, TimeProvider.System, NullLogger.Instance);

            engine.CreateBaseline(BaselineOrigin.Manual);
            system.Replace(app);
            var analysis = engine.Analyze();
            var report = await engine.StartOperation(analysis, analysis.Plan!.Candidates).RunGracefulAsync();

            Assert.Equal(1, report.ClosedCount);
            Assert.Equal(0, cleaner.Calls);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Cleanup_operation_and_engine_have_no_way_to_reach_the_memory_cleaner()
    {
        static bool Mentions(Type t) =>
            t.GetConstructors().SelectMany(c => c.GetParameters()).Any(p =>
                typeof(IMemoryCleaner).IsAssignableFrom(p.ParameterType) || p.ParameterType == typeof(DeepCleanupService))
            || t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Any(f =>
                typeof(IMemoryCleaner).IsAssignableFrom(f.FieldType) || f.FieldType == typeof(DeepCleanupService));

        Assert.False(Mentions(typeof(CleanupOperation)));
        Assert.False(Mentions(typeof(SnapBackEngine)));
    }
}
