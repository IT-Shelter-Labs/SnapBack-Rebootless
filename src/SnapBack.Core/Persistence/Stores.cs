using Microsoft.Extensions.Logging;
using SnapBack.Core.Baselines;
using SnapBack.Core.Profiles;

namespace SnapBack.Core.Persistence;

public sealed class SettingsStore(JsonFileStore<Settings> store)
{
    public Settings Load() => (store.Load() ?? new Settings()).Normalize();

    /// <summary>The saved settings, or <c>null</c> when there is no readable file (never defaults).</summary>
    public Settings? LoadSaved() => store.Load()?.Normalize();

    public DateTime? LastWriteUtc => store.LastWriteUtc;

    public void Save(Settings settings) => store.Save(settings.Normalize());
}

public sealed class BaselineStore(JsonFileStore<Baseline> store)
{
    public Baseline? Load() => store.Load();

    public void Save(Baseline baseline) => store.Save(baseline);

    public void Delete() => store.Delete();
}

/// <summary>
/// Stores all profiles in one file. On first use it migrates the single <c>baseline.json</c> of
/// version 0.2 into a profile (the old file is left untouched).
/// </summary>
public sealed class ProfileStore(JsonFileStore<ProfileSet> store, BaselineStore legacy, Func<string> defaultProfileName)
{
    public ProfileSet Load()
    {
        // Migrate only when there has never been a profiles file: an unreadable or corrupt file must
        // never be replaced by the old baseline (the corrupt file is kept aside by JsonFileStore).
        if (store.Exists)
        {
            return store.Load()?.Normalize() ?? ProfileSet.Empty;
        }

        if (legacy.Load() is not { } baseline)
        {
            return ProfileSet.Empty;
        }

        var profile = new Profile
        {
            Name = defaultProfileName(),
            CreatedAtUtc = baseline.CreatedAtUtc,
            UpdatedAtUtc = baseline.CreatedAtUtc,
            Baseline = baseline,
        };
        var migrated = new ProfileSet { Profiles = [profile], DefaultProfileId = profile.Id, CurrentProfileId = profile.Id }.Normalize();
        store.Save(migrated);
        return migrated;
    }

    /// <summary>The saved profiles, or <c>null</c> when there is no readable file.</summary>
    public ProfileSet? LoadSaved() => store.Exists ? store.Load()?.Normalize() : null;

    public DateTime? LastWriteUtc => store.LastWriteUtc;

    public void Save(ProfileSet set) => store.Save(set.Normalize());
}

public sealed class HistoryStore(JsonFileStore<List<OperationRecord>> store)
{
    public IReadOnlyList<OperationRecord> Load() => store.Load() ?? [];

    /// <summary>Adds a record (newest first) and trims the history to <paramref name="limit"/> entries.</summary>
    public void Append(OperationRecord record, int limit) => Upsert(record.OperationId, _ => record, limit);

    /// <summary>
    /// Updates the record of an operation in place (it keeps its position), or adds it at the top
    /// when there is none yet. <paramref name="update"/> receives the existing record or <c>null</c>.
    /// </summary>
    public void Upsert(Guid operationId, Func<OperationRecord?, OperationRecord> update, int limit)
    {
        var list = store.Load() ?? [];
        var index = list.FindIndex(r => r.OperationId == operationId);
        if (index >= 0)
        {
            list[index] = update(list[index]);
        }
        else
        {
            list.Insert(0, update(null));
        }

        if (list.Count > limit)
        {
            list.RemoveRange(limit, list.Count - limit);
        }

        store.Save(list);
    }

    public void Clear() => store.Save([]);
}

public static class StoreFactory
{
    public static (SettingsStore Settings, ProfileStore Profiles, HistoryStore History) Create(AppPaths paths, ILoggerFactory loggers) =>
    (
        new SettingsStore(new JsonFileStore<Settings>(paths.Settings, loggers.CreateLogger("Settings"))),
        new ProfileStore(
            new JsonFileStore<ProfileSet>(paths.Profiles, loggers.CreateLogger("Profiles")),
            new BaselineStore(new JsonFileStore<Baseline>(paths.Baseline, loggers.CreateLogger("Baseline"))),
            () => Localization.Loc.Get("Profile_DefaultName")),
        new HistoryStore(new JsonFileStore<List<OperationRecord>>(paths.History, loggers.CreateLogger("History")))
    );
}
