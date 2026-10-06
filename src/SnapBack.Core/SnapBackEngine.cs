using Microsoft.Extensions.Logging;
using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Execution;
using SnapBack.Core.Grouping;
using SnapBack.Core.Insights;
using SnapBack.Core.Model;
using SnapBack.Core.Persistence;
using SnapBack.Core.Planning;
using SnapBack.Core.Profiles;
using SnapBack.Core.Safety;

namespace SnapBack.Core;

/// <summary>Everything the UI needs to show the current state.</summary>
public sealed class Analysis
{
    public required SystemSnapshot Snapshot { get; init; }

    public required IReadOnlyList<AppGroup> Groups { get; init; }

    /// <summary>The profile the session was compared with.</summary>
    public Profile? Profile { get; init; }

    public Baseline? Baseline => Profile?.Baseline;

    /// <summary>Why the profile cannot be used, if that is the case.</summary>
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

    public SessionDrift Drift(DateTime nowUtc) => SessionDrift.Compute(this, nowUtc);

    /// <summary>Impact of closing the apps that are pre-selected by default.</summary>
    public ImpactEstimate DefaultImpact() => ImpactEstimate.Compute(this, Plan?.Candidates.Where(c => c.DefaultSelected) ?? []);
}

/// <summary>
/// Facade that wires the pieces together for the GUI and the CLI.
/// </summary>
public sealed class SnapBackEngine
{
    private readonly ISystemProbe _probe;
    private readonly IProcessController _controller;
    private readonly SettingsStore _settingsStore;
    private readonly ProfileStore _profileStore;
    private readonly HistoryStore _historyStore;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly ProcessClassifier _classifier = new();
    private readonly object _gate = new();
    private Settings _settings;
    private ProfileSet _profiles;

    public SnapBackEngine(
        ISystemProbe probe,
        IProcessController controller,
        SettingsStore settingsStore,
        ProfileStore profileStore,
        HistoryStore historyStore,
        ILoggerFactory loggers,
        TimeProvider? time = null)
    {
        _probe = probe;
        _controller = controller;
        _settingsStore = settingsStore;
        _profileStore = profileStore;
        _historyStore = historyStore;
        _loggers = loggers;
        _log = loggers.CreateLogger<SnapBackEngine>();
        _time = time ?? TimeProvider.System;
        _settings = settingsStore.Load();
        _profiles = profileStore.Load();
    }

    /// <summary>Raised after every analysis (on the analyzing thread).</summary>
    public event EventHandler<Analysis>? Analyzed;

    /// <summary>Raised when profiles are created, changed, deleted or selected.</summary>
    public event EventHandler? ProfilesChanged;

    /// <summary>Raised when settings change.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Memory history per application instance (fed by analyses; nothing is persisted).</summary>
    public MemoryTracker Tracker { get; } = new();

    public TimeProvider Time => _time;

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

    public ProfileSet Profiles
    {
        get
        {
            lock (_gate)
            {
                return _profiles;
            }
        }
    }

    /// <summary>The selected profile (falls back to the default profile).</summary>
    public Profile? CurrentProfile => Profiles.Current;

    /// <summary>Baseline of the selected profile.</summary>
    public Baseline? Baseline => CurrentProfile?.Baseline;

    public SafetyGuard Guard => new(_classifier);

    public void UpdateSettings(Func<Settings, Settings> update)
    {
        lock (_gate)
        {
            _settings = update(_settings).Normalize();
            _settingsStore.Save(_settings);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public Analysis Analyze() => Analyze(_probe.Capture());

    /// <summary>Analyzes the current state against a specific profile (without selecting it).</summary>
    public Analysis AnalyzeFor(Guid? profileId) => Analyze(_probe.Capture(), profileId);

    /// <summary>Analyzes the snapshot against the selected profile (or <paramref name="profileId"/>).</summary>
    public Analysis Analyze(SystemSnapshot snapshot, Guid? profileId = null)
    {
        var groups = new AppGrouper(_classifier).Group(snapshot);
        var profiles = Profiles;
        var profile = profileId is { } id ? profiles.Find(id) : profiles.Current;
        var settings = Settings;
        var analysis = Build(snapshot, groups, profile, settings);

        Tracker.Record(groups, _time.GetUtcNow().UtcDateTime);
        try
        {
            Analyzed?.Invoke(this, analysis);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "An analysis listener failed");
        }

        return analysis;
    }

    private Analysis Build(SystemSnapshot snapshot, IReadOnlyList<AppGroup> groups, Profile? profile, Settings settings)
    {
        if (profile is null)
        {
            return new Analysis { Snapshot = snapshot, Groups = groups };
        }

        var problem = BaselineFactory.Validate(profile.Baseline, snapshot.Context);
        if (problem is not null)
        {
            _log.LogWarning("Profile {Profile} is not usable: {Problem}", profile.Name, problem);
            return new Analysis { Snapshot = snapshot, Groups = groups, Profile = profile, BaselineProblem = problem };
        }

        var comparison = BaselineComparer.Compare(profile.Baseline, groups, snapshot.Context);
        var plan = new CleanupPlanner().Build(groups, comparison, new PlannerOptions
        {
            Rules = settings.RuleLookup,
            PreselectCaution = settings.PreselectCautionItems,
        });

        return new Analysis
        {
            Snapshot = snapshot,
            Groups = groups,
            Profile = profile,
            Comparison = comparison,
            Plan = plan,
        };
    }

    // ---- Profiles ------------------------------------------------------------------------------

    /// <summary>
    /// Makes the current state the baseline of the selected profile (creating the first profile
    /// when there is none).
    /// </summary>
    public Baseline CreateBaseline(BaselineOrigin origin, SystemSnapshot? snapshot = null)
    {
        var current = CurrentProfile;
        return current is null
            ? CreateProfile(Localization.Loc.Get("Profile_DefaultName"), origin, snapshot).Baseline
            : UpdateProfile(current.Id, origin, snapshot).Baseline;
    }

    /// <summary>Creates a profile from the current state. The first profile becomes the default.</summary>
    public Profile CreateProfile(string name, BaselineOrigin origin, SystemSnapshot? snapshot = null)
    {
        var baseline = Capture(origin, snapshot);
        Profile profile;
        lock (_gate)
        {
            if (_profiles.Profiles.Count >= ProfileSet.MaxProfiles)
            {
                throw new InvalidOperationException(Localization.Loc.Format("Profile_TooMany", ProfileSet.MaxProfiles));
            }

            var now = _time.GetUtcNow().UtcDateTime;
            var taken = _profiles.Profiles.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            profile = new Profile
            {
                Name = ProfileSet.UniqueName(ProfileSet.CleanName(name), taken),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Baseline = baseline,
            };
            var first = _profiles.Profiles.Count == 0;
            SaveProfiles(_profiles with
            {
                Profiles = [.. _profiles.Profiles, profile],
                DefaultProfileId = first ? profile.Id : _profiles.DefaultProfileId,
                CurrentProfileId = first ? profile.Id : _profiles.CurrentProfileId,
            });
        }

        _log.LogInformation("Profile created ({Origin}): {Apps} apps, {Processes} processes", origin, baseline.Apps.Count, baseline.ProcessCount);
        RaiseProfilesChanged();
        return profile;
    }

    /// <summary>Replaces the profile's applications with the current state (name and options stay).</summary>
    public Profile UpdateProfile(Guid id, BaselineOrigin origin, SystemSnapshot? snapshot = null)
    {
        var baseline = Capture(origin, snapshot);
        var updated = Change(id, p => p with { Baseline = baseline });
        _log.LogInformation("Profile updated ({Origin}): {Apps} apps, {Processes} processes", origin, baseline.Apps.Count, baseline.ProcessCount);
        return updated;
    }

    /// <summary>Renames a profile; a name that is already taken gets a number appended.</summary>
    public Profile RenameProfile(Guid id, string name) => Change(id, p => p with { Name = ProfileSet.CleanName(name) }, unique: true);

    public Profile SetEnvironmentPolicy(Guid id, EnvironmentPolicy wsl, EnvironmentPolicy docker) =>
        Change(id, p => p with { Wsl = wsl, Docker = docker });

    /// <summary>Removes an application from a profile, so returning to the profile offers to close it.</summary>
    public Profile RemoveAppFromProfile(Guid id, string appKey) =>
        Change(id, p => p with { Baseline = p.Baseline with { Apps = p.Baseline.Apps.Where(a => a.AppKey != appKey).ToList() } });

    /// <summary>Adds running applications to a profile, so returning to the profile keeps them.</summary>
    public Profile AddAppsToProfile(Guid id, IEnumerable<AppGroup> groups)
    {
        var added = BaselineFactory.AppsFrom(groups.ToList());
        return Change(id, p => p with
        {
            Baseline = p.Baseline with
            {
                Apps = p.Baseline.Apps
                    .Where(a => added.All(x => x.AppKey != a.AppKey))
                    .Concat(added)
                    .OrderBy(a => a.WasProtected)
                    .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
            },
        });
    }

    public void DeleteProfile(Guid id)
    {
        lock (_gate)
        {
            if (_profiles.Find(id) is null)
            {
                return;
            }

            var rest = _profiles.Profiles.Where(p => p.Id != id).ToList();
            SaveProfiles(_profiles with
            {
                Profiles = rest,
                DefaultProfileId = _profiles.DefaultProfileId == id ? rest.FirstOrDefault()?.Id : _profiles.DefaultProfileId,
                CurrentProfileId = _profiles.CurrentProfileId == id ? null : _profiles.CurrentProfileId,
            });
        }

        _log.LogInformation("Profile deleted");
        RaiseProfilesChanged();
    }

    public void SetDefaultProfile(Guid id) => ChangeSet(s => s.Find(id) is null ? s : s with { DefaultProfileId = id });

    /// <summary>Selects the profile the session is compared with. Nothing is closed.</summary>
    public void SelectProfile(Guid id) => ChangeSet(s => s.Find(id) is null ? s : s with { CurrentProfileId = id });

    /// <summary>Compatibility with version 0.2: deletes the selected profile.</summary>
    public void DeleteBaseline()
    {
        if (CurrentProfile is { } p)
        {
            DeleteProfile(p.Id);
        }
    }

    private Baseline Capture(BaselineOrigin origin, SystemSnapshot? snapshot)
    {
        snapshot ??= _probe.Capture();
        var groups = new AppGrouper(_classifier).Group(snapshot);
        return BaselineFactory.Create(snapshot, groups, origin, Environment.MachineName);
    }

    private Profile Change(Guid id, Func<Profile, Profile> change, bool unique = false)
    {
        Profile updated;
        lock (_gate)
        {
            var existing = _profiles.Find(id) ?? throw new InvalidOperationException(Localization.Loc.Get("Profile_NotFound"));
            updated = change(existing) with { UpdatedAtUtc = _time.GetUtcNow().UtcDateTime };
            if (unique)
            {
                var taken = _profiles.Profiles.Where(p => p.Id != id).Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
                updated = updated with { Name = ProfileSet.UniqueName(updated.Name, taken) };
            }

            SaveProfiles(_profiles with { Profiles = _profiles.Profiles.Select(p => p.Id == id ? updated : p).ToList() });
            updated = _profiles.Find(id)!;
        }

        RaiseProfilesChanged();
        return updated;
    }

    private void ChangeSet(Func<ProfileSet, ProfileSet> change)
    {
        lock (_gate)
        {
            SaveProfiles(change(_profiles));
        }

        RaiseProfilesChanged();
    }

    private void SaveProfiles(ProfileSet set)
    {
        var normalized = set.Normalize();
        _profileStore.Save(normalized);
        _profiles = normalized;
    }

    private void RaiseProfilesChanged()
    {
        try
        {
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A profile listener failed");
        }
    }

    // ---- Operations ----------------------------------------------------------------------------

    /// <summary>Creates an operation for the selected items. Non-selectable items are rejected by the operation itself.</summary>
    public CleanupOperation StartOperation(Analysis analysis, IEnumerable<PlanItem> selected) =>
        new(selected, analysis.Snapshot.Context, _probe, _controller, Guard, CleanupOptionsFor(Settings), _time,
            _loggers.CreateLogger<CleanupOperation>());

    public DryRunReport DryRun(Analysis analysis, IEnumerable<PlanItem> selected) =>
        CleanupOperation.DryRun(selected, analysis.Snapshot.Context, _probe, Guard, _time.GetUtcNow().UtcDateTime);

    /// <summary>Current physical memory statistics.</summary>
    public MemoryStatus ReadMemory() => _probe.GetMemoryStatus();

    /// <summary>Records (or extends) the history entry of an operation.</summary>
    public void RecordHistory(OperationReport report, Profile? profile = null) =>
        UpdateHistory(report.OperationId, existing =>
        {
            var record = OperationRecord.Merge(existing, report);
            return existing is null && profile is not null ? record with { ProfileId = profile.Id, ProfileName = profile.Name } : record;
        });

    /// <summary>Changes the history entry of an operation (e.g. to add measured results).</summary>
    public void UpdateHistory(Guid operationId, Func<OperationRecord?, OperationRecord> update)
    {
        try
        {
            _historyStore.Upsert(operationId, update, Settings.HistoryLimit);
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
