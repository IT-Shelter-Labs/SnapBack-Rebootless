using Microsoft.Extensions.Logging;
using SnapBack.Core.Baselines;

namespace SnapBack.Core.Persistence;

public sealed class SettingsStore(JsonFileStore<Settings> store)
{
    public Settings Load() => (store.Load() ?? new Settings()).Normalize();

    public void Save(Settings settings) => store.Save(settings.Normalize());
}

public sealed class BaselineStore(JsonFileStore<Baseline> store)
{
    public Baseline? Load() => store.Load();

    public void Save(Baseline baseline) => store.Save(baseline);

    public void Delete() => store.Delete();
}

public sealed class HistoryStore(JsonFileStore<List<OperationRecord>> store)
{
    public IReadOnlyList<OperationRecord> Load() => store.Load() ?? [];

    /// <summary>Adds a record (newest first) and trims the history to <paramref name="limit"/> entries.</summary>
    public void Append(OperationRecord record, int limit)
    {
        var list = store.Load() ?? [];
        list.Insert(0, record);
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
    public static (SettingsStore Settings, BaselineStore Baseline, HistoryStore History) Create(AppPaths paths, ILoggerFactory loggers) =>
    (
        new SettingsStore(new JsonFileStore<Settings>(paths.Settings, loggers.CreateLogger("Settings"))),
        new BaselineStore(new JsonFileStore<Baseline>(paths.Baseline, loggers.CreateLogger("Baseline"))),
        new HistoryStore(new JsonFileStore<List<OperationRecord>>(paths.History, loggers.CreateLogger("History")))
    );
}
