using Microsoft.Extensions.Logging;
using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Execution;
using SnapBack.Core.Grouping;
using SnapBack.Core.Model;
using SnapBack.Core.Persistence;
using SnapBack.Core.Planning;
using SnapBack.Core.Safety;

namespace SnapBack.Core;

/// <summary>Everything the UI needs to show the current state.</summary>
public sealed class Analysis
{
    public required SystemSnapshot Snapshot { get; init; }

    public required IReadOnlyList<AppGroup> Groups { get; init; }

    public Baseline? Baseline { get; init; }

    /// <summary>Why the stored baseline cannot be used, if that is the case.</summary>
    public string? BaselineProblem { get; init; }

    public ComparisonResult? Comparison { get; init; }

    public CleanupPlan? Plan { get; init; }

    public bool HasUsableBaseline => Plan is not null;

    /// <summary>User applications currently running (protected system components excluded).</summary>
    public int RunningAppCount => Groups.Count(g => !g.IsProtected);

    public int NewAppCount => Plan?.Candidates.Count ?? 0;

    /// <summary>Memory used by everything that appeared after the baseline (upper bound of what can be freed).</summary>
    public long FreeableBytes => Plan?.Candidates.Sum(c => c.Group.ActionablePrivateWorkingSet) ?? 0;

    public MemoryStatus Memory => Snapshot.Memory;
}

/// <summary>
/// Facade that wires the pieces together for the GUI and the CLI.
/// </summary>
public sealed class SnapBackEngine
{
    private readonly ISystemProbe _probe;
    private readonly IProcessController _controller;
    private readonly SettingsStore _settingsStore;
    private readonly BaselineStore _baselineStore;
    private readonly HistoryStore _historyStore;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly ProcessClassifier _classifier = new();
    private readonly object _gate = new();
    private Settings _settings;
    private Baseline? _baseline;

    public SnapBackEngine(
        ISystemProbe probe,
        IProcessController controller,
        SettingsStore settingsStore,
        BaselineStore baselineStore,
        HistoryStore historyStore,
        ILoggerFactory loggers,
        TimeProvider? time = null)
    {
        _probe = probe;
        _controller = controller;
        _settingsStore = settingsStore;
        _baselineStore = baselineStore;
        _historyStore = historyStore;
        _loggers = loggers;
        _log = loggers.CreateLogger<SnapBackEngine>();
        _time = time ?? TimeProvider.System;
        _settings = settingsStore.Load();
        _baseline = baselineStore.Load();
    }

    public Settings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public Baseline? Baseline
    {
        get
        {
            lock (_gate)
            {
                return _baseline;
            }
        }
    }

    public SafetyGuard Guard => new(_classifier);

    public void UpdateSettings(Func<Settings, Settings> update)
    {
        lock (_gate)
        {
            _settings = update(_settings).Normalize();
            _settingsStore.Save(_settings);
        }
    }

    public Analysis Analyze()
    {
        var snapshot = _probe.Capture();
        return Analyze(snapshot);
    }

    public Analysis Analyze(SystemSnapshot snapshot)
    {
        var groups = new AppGrouper(_classifier).Group(snapshot);
        var baseline = Baseline;
        var settings = Settings;

        if (baseline is null)
        {
            return new Analysis { Snapshot = snapshot, Groups = groups };
        }

        var problem = BaselineFactory.Validate(baseline, snapshot.Context);
        if (problem is not null)
        {
            _log.LogWarning("Stored baseline is not usable: {Problem}", problem);
            return new Analysis { Snapshot = snapshot, Groups = groups, Baseline = baseline, BaselineProblem = problem };
        }

        var comparison = BaselineComparer.Compare(baseline, groups, snapshot.Context);
        var plan = new CleanupPlanner().Build(groups, comparison, new PlannerOptions
        {
            KeepAppKeys = settings.KeepAppKeys,
            PreselectCaution = settings.PreselectCautionItems,
        });

        return new Analysis
        {
            Snapshot = snapshot,
            Groups = groups,
            Baseline = baseline,
            Comparison = comparison,
            Plan = plan,
        };
    }

    /// <summary>Makes the current state the baseline.</summary>
    public Baseline CreateBaseline(BaselineOrigin origin, SystemSnapshot? snapshot = null)
    {
        snapshot ??= _probe.Capture();
        var groups = new AppGrouper(_classifier).Group(snapshot);
        var baseline = BaselineFactory.Create(snapshot, groups, origin, Environment.MachineName);
        lock (_gate)
        {
            _baselineStore.Save(baseline);
            _baseline = baseline;
        }

        _log.LogInformation("Baseline created ({Origin}): {Apps} apps, {Processes} processes",
            origin, baseline.Apps.Count, baseline.ProcessCount);
        return baseline;
    }

    public void DeleteBaseline()
    {
        lock (_gate)
        {
            _baselineStore.Delete();
            _baseline = null;
        }

        _log.LogInformation("Baseline deleted");
    }

    /// <summary>Creates an operation for the selected items. Non-selectable items are rejected by the operation itself.</summary>
    public CleanupOperation StartOperation(Analysis analysis, IEnumerable<PlanItem> selected) =>
        new(selected, analysis.Snapshot.Context, _probe, _controller, Guard, CleanupOptionsFor(Settings), _time,
            _loggers.CreateLogger<CleanupOperation>());

    public DryRunReport DryRun(Analysis analysis, IEnumerable<PlanItem> selected) =>
        CleanupOperation.DryRun(selected, analysis.Snapshot.Context, _probe, Guard, _time.GetUtcNow().UtcDateTime);

    public void RecordHistory(OperationReport report)
    {
        try
        {
            _historyStore.Append(OperationRecord.From(report), Settings.HistoryLimit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save history");
        }
    }

    public IReadOnlyList<OperationRecord> LoadHistory() => _historyStore.Load();

    public void ClearHistory() => _historyStore.Clear();

    public static CleanupOptions CleanupOptionsFor(Settings settings) => new()
    {
        GracefulTimeout = TimeSpan.FromSeconds(settings.GracefulTimeoutSeconds),
    };
}
