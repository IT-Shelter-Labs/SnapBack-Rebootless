using Microsoft.Extensions.Logging;
using SnapBack.Core.Localization;
using SnapBack.Core.Model;

namespace SnapBack.Core.Memory;

/// <summary>What the optional Deep Memory Cleanup purges.</summary>
public enum DeepCleanupMode
{
    /// <summary>Only the lowest-priority standby pages (the least valuable cache).</summary>
    LowPriorityStandby,

    /// <summary>The whole standby list (all cached data that is not in use).</summary>
    Standby,
}

/// <summary>
/// The <c>SYSTEM_MEMORY_LIST_COMMAND</c> values SnapBack may send. Only the two standby purges are
/// allowed. <c>MemoryEmptyWorkingSets</c> (2) and <c>MemoryFlushModifiedList</c> (3) are
/// deliberately unreachable: SnapBack never trims applications' working sets and never forces
/// pending writes.
/// </summary>
public static class MemoryListCommand
{
    public const int PurgeStandbyList = 4;
    public const int PurgeLowPriorityStandbyList = 5;

    public static int For(DeepCleanupMode mode) => mode switch
    {
        DeepCleanupMode.Standby => PurgeStandbyList,
        DeepCleanupMode.LowPriorityStandby => PurgeLowPriorityStandbyList,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static bool IsAllowed(int command) => command is PurgeStandbyList or PurgeLowPriorityStandbyList;
}

public enum DeepCleanupStatus
{
    Completed,
    CancelledByUser,
    NotElevated,
    PrivilegeMissing,
    Failed,
    BadRequest,
}

public sealed record DeepCleanupOutcome(DeepCleanupStatus Status, string Message, bool UsedElevationPrompt = false)
{
    public bool Succeeded => Status == DeepCleanupStatus.Completed;

    public static DeepCleanupOutcome For(DeepCleanupStatus status, int? errorCode = null, bool prompted = false) => new(status, status switch
    {
        DeepCleanupStatus.Completed => Loc.Get("Deep_Completed"),
        DeepCleanupStatus.CancelledByUser => Loc.Get("Deep_CancelledByUser"),
        DeepCleanupStatus.NotElevated => Loc.Get("Deep_NotElevated"),
        DeepCleanupStatus.PrivilegeMissing => Loc.Get("Deep_PrivilegeMissing"),
        DeepCleanupStatus.BadRequest => Loc.Get("Deep_BadRequest"),
        _ => Loc.Format("Deep_Failed", errorCode is { } c ? "0x" + c.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) : "?"),
    }, prompted);
}

/// <summary>Performs the purge inside an elevated process (Windows implementation).</summary>
public interface IStandbyPurger
{
    DeepCleanupOutcome Purge(DeepCleanupMode mode);
}

/// <summary>Starts SnapBack's elevated helper (UAC prompt). Returns <c>null</c> when the user declines.</summary>
public interface IElevatedRunner
{
    Task<int?> RunElevatedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public interface IMemoryCleaner
{
    /// <summary>Whether running a cleanup will show a UAC prompt (SnapBack itself is not elevated).</summary>
    bool PromptsForElevation { get; }

    Task<DeepCleanupOutcome> CleanAsync(DeepCleanupMode mode, CancellationToken cancellationToken);
}

/// <summary>
/// The command-line protocol between SnapBack and its short-lived elevated helper. The helper
/// accepts exactly one fixed command with one of two fixed values, reads no files and writes no
/// files; its whole answer is its exit code.
/// </summary>
public static class DeepCleanupHelper
{
    public const string Switch = "--deep-cleanup";

    private const string StandbyToken = "standby";
    private const string LowPriorityToken = "low-priority";

    public const int ExitCompleted = 0;
    public const int ExitNotElevated = 20;
    public const int ExitPrivilegeMissing = 21;
    public const int ExitFailed = 22;
    public const int ExitBadRequest = 23;

    public static bool IsHelperInvocation(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Switch, StringComparison.Ordinal);

    public static IReadOnlyList<string> BuildArguments(DeepCleanupMode mode) =>
        [Switch, mode == DeepCleanupMode.Standby ? StandbyToken : LowPriorityToken];

    /// <summary>Strict parser: exactly <c>--deep-cleanup standby|low-priority</c>, nothing else.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out DeepCleanupMode mode)
    {
        mode = default;
        if (args.Count != 2 || !string.Equals(args[0], Switch, StringComparison.Ordinal))
        {
            return false;
        }

        switch (args[1])
        {
            case StandbyToken:
                mode = DeepCleanupMode.Standby;
                return true;
            case LowPriorityToken:
                mode = DeepCleanupMode.LowPriorityStandby;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Entry point of the elevated helper. Returns the process exit code.</summary>
    public static int Run(IReadOnlyList<string> args, bool processIsElevated, IStandbyPurger purger, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(purger);
        if (!TryParse(args, out var mode))
        {
            log.LogWarning("Deep cleanup helper: rejected arguments");
            return ExitBadRequest;
        }

        if (!processIsElevated)
        {
            log.LogWarning("Deep cleanup helper: not elevated");
            return ExitNotElevated;
        }

        var outcome = purger.Purge(mode);
        log.LogInformation("Deep cleanup helper: {Mode} -> {Status}", mode, outcome.Status);
        return ExitCodeFor(outcome.Status);
    }

    public static int ExitCodeFor(DeepCleanupStatus status) => status switch
    {
        DeepCleanupStatus.Completed => ExitCompleted,
        DeepCleanupStatus.NotElevated => ExitNotElevated,
        DeepCleanupStatus.PrivilegeMissing => ExitPrivilegeMissing,
        DeepCleanupStatus.BadRequest => ExitBadRequest,
        _ => ExitFailed,
    };

    public static DeepCleanupOutcome OutcomeFromExitCode(int exitCode) => exitCode switch
    {
        ExitCompleted => DeepCleanupOutcome.For(DeepCleanupStatus.Completed, prompted: true),
        ExitNotElevated => DeepCleanupOutcome.For(DeepCleanupStatus.NotElevated, prompted: true),
        ExitPrivilegeMissing => DeepCleanupOutcome.For(DeepCleanupStatus.PrivilegeMissing, prompted: true),
        ExitBadRequest => DeepCleanupOutcome.For(DeepCleanupStatus.BadRequest, prompted: true),
        ExitFailed => DeepCleanupOutcome.For(DeepCleanupStatus.Failed, prompted: true),
        _ => new DeepCleanupOutcome(DeepCleanupStatus.Failed, Loc.Format("Deep_UnknownExit", exitCode), true),
    };
}

/// <summary>
/// Runs the purge in-process when SnapBack is already elevated; otherwise asks for elevation for
/// this single operation by starting the elevated helper (one UAC prompt, then the helper exits).
/// </summary>
public sealed class MemoryCleaner : IMemoryCleaner
{
    private readonly bool _processIsElevated;
    private readonly IStandbyPurger _purger;
    private readonly IElevatedRunner _runner;

    public MemoryCleaner(bool processIsElevated, IStandbyPurger purger, IElevatedRunner runner)
    {
        _processIsElevated = processIsElevated;
        _purger = purger;
        _runner = runner;
    }

    public bool PromptsForElevation => !_processIsElevated;

    public async Task<DeepCleanupOutcome> CleanAsync(DeepCleanupMode mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode))
        {
            return DeepCleanupOutcome.For(DeepCleanupStatus.BadRequest);
        }

        if (_processIsElevated)
        {
            return _purger.Purge(mode);
        }

        int? exitCode;
        try
        {
            exitCode = await _runner.RunElevatedAsync(DeepCleanupHelper.BuildArguments(mode), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DeepCleanupOutcome(DeepCleanupStatus.Failed, Loc.Format("Deep_LaunchFailed", ex.Message));
        }

        return exitCode is { } code
            ? DeepCleanupHelper.OutcomeFromExitCode(code)
            : DeepCleanupOutcome.For(DeepCleanupStatus.CancelledByUser);
    }
}

/// <summary>Memory before and after a Deep Memory Cleanup.</summary>
public sealed record DeepCleanupReport(DeepCleanupMode Mode, DeepCleanupOutcome Outcome, MemoryStatus Before, MemoryStatus After, DateTime FinishedAtUtc)
{
    /// <summary>
    /// Cache that was dropped: the decrease of the standby list (or, if list sizes are unknown, the
    /// increase of free memory). This memory was already available before the cleanup.
    /// </summary>
    public long CacheReclaimedBytes
    {
        get
        {
            if (!Outcome.Succeeded)
            {
                return 0;
            }

            if (Before.StandbyBytes is { } before && After.StandbyBytes is { } after)
            {
                return Math.Max(0, before - after);
            }

            if (Before.FreeBytes is { } freeBefore && After.FreeBytes is { } freeAfter)
            {
                return Math.Max(0, freeAfter - freeBefore);
            }

            return 0;
        }
    }

    public long AvailableChangeBytes => After.AvailablePhysicalBytes - Before.AvailablePhysicalBytes;

    public IReadOnlyList<ComparisonRow> Rows =>
    [
        new("Cmp_Cached", ComparisonValueKind.Bytes, Before.CachedBytes, After.CachedBytes),
        new("Cmp_Free", ComparisonValueKind.Bytes, Before.FreeBytes, After.FreeBytes),
        new("Cmp_Available", ComparisonValueKind.Bytes, Before.IsKnown ? Before.AvailablePhysicalBytes : null, After.IsKnown ? After.AvailablePhysicalBytes : null),
    ];
}

/// <summary>Measures memory around a cleanup. Never part of "Return to baseline": it only runs when asked.</summary>
public sealed class DeepCleanupService
{
    private readonly Func<MemoryStatus> _readMemory;
    private readonly IMemoryCleaner _cleaner;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public DeepCleanupService(Func<MemoryStatus> readMemory, IMemoryCleaner cleaner, TimeProvider time, ILogger log)
    {
        _readMemory = readMemory;
        _cleaner = cleaner;
        _time = time;
        _log = log;
    }

    public bool PromptsForElevation => _cleaner.PromptsForElevation;

    /// <summary>How long to wait before measuring again (list sizes settle quickly).</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(750);

    public async Task<DeepCleanupReport> RunAsync(DeepCleanupMode mode, CancellationToken cancellationToken = default)
    {
        var before = SafeRead();
        _log.LogInformation("Deep Memory Cleanup requested by the user ({Mode})", mode);
        var outcome = await _cleaner.CleanAsync(mode, cancellationToken).ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            await Task.Delay(SettleDelay, _time, cancellationToken).ConfigureAwait(false);
        }

        var after = outcome.Succeeded ? SafeRead() : before;
        var report = new DeepCleanupReport(mode, outcome, before, after, _time.GetUtcNow().UtcDateTime);
        _log.LogInformation("Deep Memory Cleanup {Status}: cache reclaimed {Bytes} bytes", outcome.Status, report.CacheReclaimedBytes);
        return report;
    }

    private MemoryStatus SafeRead()
    {
        try
        {
            return _readMemory();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read memory status");
            return MemoryStatus.Unknown;
        }
    }
}
