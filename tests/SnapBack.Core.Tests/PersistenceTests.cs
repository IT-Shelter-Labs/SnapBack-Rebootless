using Microsoft.Extensions.Logging.Abstractions;
using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Execution;
using SnapBack.Core.Grouping;
using SnapBack.Core.Model;
using SnapBack.Core.Persistence;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snapback-tests-" + Guid.NewGuid().ToString("N"));

    public PersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JsonFileStore<T> Store<T>(string name)
        where T : class => new(Path.Combine(_dir, name), NullLogger.Instance);

    [Fact]
    public void Missing_file_loads_as_null_and_settings_fall_back_to_defaults()
    {
        Assert.Null(Store<Baseline>("baseline.json").Load());
        var settings = new SettingsStore(Store<Settings>("settings.json")).Load();
        Assert.Equal(20, settings.GracefulTimeoutSeconds);
        Assert.False(settings.PreselectCautionItems);
        Assert.False(settings.AutoBaselineOnSignIn);
    }

    [Fact]
    public void Corrupt_file_is_quarantined_instead_of_crashing()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        var settings = new SettingsStore(Store<Settings>("settings.json")).Load();
        Assert.Equal(20, settings.GracefulTimeoutSeconds);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Settings_are_clamped_to_safe_ranges()
    {
        var s = new Settings
        {
            GracefulTimeoutSeconds = 0,
            AutoBaselineQuietSeconds = 1,
            HistoryLimit = 100000,
            KeepList = [new("k", "A", DateTime.UtcNow), new("k", "A again", DateTime.UtcNow), new("", "bad", DateTime.UtcNow)],
        }.Normalize();

        Assert.Equal(5, s.GracefulTimeoutSeconds);
        Assert.Equal(10, s.AutoBaselineQuietSeconds);
        Assert.Equal(500, s.HistoryLimit);

        // The version 1 keep list is migrated into "always keep" rules (duplicates and empty keys dropped).
        Assert.Null(s.KeepList);
        var rule = Assert.Single(s.Rules);
        Assert.Equal(Core.Rules.AppRuleKind.AlwaysKeep, rule.Kind);
    }

    [Fact]
    public void Keep_list_add_and_remove()
    {
        var s = new Settings().WithKept("key", "App", DateTime.UtcNow).WithKept("key", "App", DateTime.UtcNow);
        Assert.Single(s.Rules);
        Assert.Contains("key", s.KeepAppKeys);
        Assert.Empty(s.WithoutKept("key").Rules);
    }

    [Fact]
    public void Baseline_round_trips_with_process_identities()
    {
        var snap = Snapshot([.. FreshSession(), App(4000, @"C:\Apps\Telegram\Telegram.exe", 5, parent: 1100, visibleWindows: 1)]);
        var groups = new AppGrouper(new ProcessClassifier()).Group(snap);
        var baseline = BaselineFactory.Create(snap, groups, BaselineOrigin.Automatic, "PC");

        var store = new BaselineStore(Store<Baseline>("baseline.json"));
        store.Save(baseline);
        var loaded = store.Load()!;

        Assert.Equal(baseline.Id, loaded.Id);
        Assert.Equal(BaselineOrigin.Automatic, loaded.Origin);
        Assert.Equal(baseline.Apps.Count, loaded.Apps.Count);
        Assert.Equal(new ProcessIdentity(4000, At(5)), loaded.Apps.Single(a => a.DisplayName == "Telegram").Instances.Single());
        Assert.Contains("\"Automatic\"", File.ReadAllText(Path.Combine(_dir, "baseline.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_leaves_no_temp_files_behind()
    {
        var store = Store<Settings>("settings.json");
        for (var i = 0; i < 5; i++)
        {
            store.Save(new Settings { GracefulTimeoutSeconds = 10 + i });
        }

        Assert.Equal(14, store.Load()!.GracefulTimeoutSeconds);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void History_is_newest_first_and_trimmed()
    {
        var history = new HistoryStore(Store<List<OperationRecord>>("history.json"));
        for (var i = 0; i < 5; i++)
        {
            history.Append(new OperationRecord { OperationId = Guid.NewGuid(), Closed = i, Kind = OperationKind.Graceful }, limit: 3);
        }

        var records = history.Load();
        Assert.Equal(3, records.Count);
        Assert.Equal(4, records[0].Closed);
    }
}

public class StartupAndTextTests
{
    [Fact]
    public void Startup_settles_after_min_delay_and_quiet_period()
    {
        var logon = Boot.AddMinutes(1);
        var start = logon.AddSeconds(5);
        var d = new StartupSettleDetectorAdapter(logon, start);

        Assert.Equal(Startup.SettleState.Waiting, d.At(0, "a"));
        Assert.Equal(Startup.SettleState.Waiting, d.At(60, "a", "b"));   // autostart still launching
        Assert.Equal(Startup.SettleState.Waiting, d.At(100, "a", "b"));  // quiet, but < 2 min since logon
        Assert.Equal(Startup.SettleState.Settled, d.At(130, "a", "b"));
    }

    [Fact]
    public void Startup_gives_up_after_max_wait()
    {
        var logon = Boot;
        var d = new StartupSettleDetectorAdapter(logon, logon);
        for (var t = 0; t < 600; t += 20)
        {
            Assert.Equal(Startup.SettleState.Waiting, d.At(t, "app" + t)); // keeps changing
        }

        Assert.Equal(Startup.SettleState.TimedOut, d.At(601, "final"));
    }

    [Fact]
    public void Logon_time_is_the_oldest_user_process()
    {
        var snap = Snapshot(FreshSession());
        Assert.Equal(Boot.AddMinutes(1), Startup.StartupSettleDetector.EstimateLogonUtc(snap));
    }

    private sealed class StartupSettleDetectorAdapter(DateTime logon, DateTime start)
    {
        private readonly Startup.StartupSettleDetector _d = new(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(10), logon, start);

        public Startup.SettleState At(int seconds, params string[] keys) => _d.Observe(start.AddSeconds(seconds), keys);
    }

    [Theory]
    [InlineData(@"""C:\Python312\python.exe"" train.py --epochs 5", "train.py")]
    [InlineData(@"python.exe -u ""C:\work\my scripts\job.py""", "job.py")]
    [InlineData(@"python -m http.server 8000", "module http.server")]
    [InlineData(@"python -c ""print(1)""", "inline code")]
    [InlineData(@"node --inspect server.js", "server.js")]
    [InlineData(@"java -jar C:\apps\tool.jar --secret=abc", "tool.jar")]
    [InlineData(@"pwsh -NoProfile -File C:\s\deploy.ps1", "deploy.ps1")]
    [InlineData(@"node", null)]
    [InlineData(@"", null)]
    public void Command_line_hint_extracts_only_the_script_name(string commandLine, string? expected) =>
        Assert.Equal(expected, Text.CommandLineHint.FromCommandLine(commandLine));

    [Fact]
    public void Command_line_split_follows_windows_quoting_rules() =>
        Assert.Equal(["a b", "c\"d", @"e\\", "f"], Text.CommandLineHint.Split(@"""a b"" c\""d ""e\\\\"" f"));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(650L * 1024 * 1024, "650 MB")]
    [InlineData(1503238553L, "1.4 GB")]
    [InlineData(3435973837L, "3.2 GB")]
    public void Byte_sizes_are_formatted_like_windows(long bytes, string expected) =>
        Assert.Equal(expected, Text.ByteSize.Format(bytes));
}
