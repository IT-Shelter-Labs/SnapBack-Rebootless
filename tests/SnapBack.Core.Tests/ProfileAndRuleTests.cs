using Microsoft.Extensions.Logging.Abstractions;
using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Execution;
using SnapBack.Core.Grouping;
using SnapBack.Core.Model;
using SnapBack.Core.Persistence;
using SnapBack.Core.Planning;
using SnapBack.Core.Profiles;
using SnapBack.Core.Rules;
using SnapBack.Core.Tests.Fakes;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public sealed class ProfileAndRuleTests : IDisposable
{
    private const string SteamPath = @"C:\Program Files (x86)\Steam\steam.exe";
    private const string DiscordPath = @"C:\Users\me\AppData\Local\Discord\app-1.0.9165\Discord.exe";
    private const string CodePath = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe";
    private const string TelegramPath = @"C:\Users\me\AppData\Roaming\Telegram Desktop\Telegram.exe";
    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snapback-prof-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private SnapBackEngine Engine(FakeSystem system)
    {
        var (settings, profiles, history) = StoreFactory.Create(new AppPaths(_dir), NullLoggerFactory.Instance);
        var engine = new SnapBackEngine(system, system, settings, profiles, history, NullLoggerFactory.Instance);
        engine.UpdateSettings(s => s with { GracefulTimeoutSeconds = 5 });
        return engine;
    }

    private static ProcessInfo Steam() => App(3000, SteamPath, 5, parent: 1100, visibleWindows: 1, memoryMb: 300);

    private static ProcessInfo Discord() => App(3100, DiscordPath, 5, parent: 1100, visibleWindows: 1, memoryMb: 500);

    private static ProcessInfo Code() => App(3200, CodePath, 6, parent: 1100, visibleWindows: 1, memoryMb: 900);

    private static ProcessInfo Telegram() => App(3300, TelegramPath, 6, parent: 1100, visibleWindows: 1, memoryMb: 200);

    private static PlanItem Item(Analysis a, string name) => a.Plan!.All.Single(i => i.Group.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));

    // ---- Profiles --------------------------------------------------------------------------

    [Fact]
    public void First_profile_becomes_default_and_current()
    {
        var system = new FakeSystem([.. FreshSession(), Steam()]);
        var engine = Engine(system);

        Assert.Null(engine.CurrentProfile);
        engine.CreateBaseline(BaselineOrigin.Manual);

        var p = Assert.Single(engine.Profiles.Profiles);
        Assert.Equal("Clean", p.Name);
        Assert.Equal(p.Id, engine.Profiles.DefaultProfileId);
        Assert.Equal(p.Id, engine.CurrentProfile!.Id);

        var gaming = engine.CreateProfile("Gaming", BaselineOrigin.Manual);
        Assert.Equal(p.Id, engine.Profiles.DefaultProfileId);
        Assert.Equal(p.Id, engine.CurrentProfile!.Id); // creating does not switch
        Assert.NotEqual(p.Id, gaming.Id);
    }

    [Fact]
    public void Switching_profiles_changes_what_is_offered_for_closing()
    {
        // Work: VS Code + Telegram. Gaming: Steam + Discord.
        var system = new FakeSystem([.. FreshSession(), Code(), Telegram()]);
        var engine = Engine(system);
        var work = engine.CreateProfile("Work", BaselineOrigin.Manual);

        system.Kill(3200);
        system.Kill(3300);
        system.Replace(Steam());
        system.Replace(Discord());
        var gaming = engine.CreateProfile("Gaming", BaselineOrigin.Manual);

        // Now everything is running.
        system.Replace(Code());
        system.Replace(Telegram());

        engine.SelectProfile(gaming.Id);
        var a = engine.Analyze();
        Assert.Equal(gaming.Id, a.Profile!.Id);
        Assert.Equal(PlanCategory.Candidate, Item(a, "Code").Category);
        Assert.Equal(PlanCategory.Candidate, Item(a, "Telegram").Category);
        Assert.Equal(PlanCategory.Kept, Item(a, "steam").Category);
        Assert.Equal(PlanCategory.Kept, Item(a, "Discord").Category);

        var w = engine.AnalyzeFor(work.Id);
        Assert.Equal(PlanCategory.Kept, Item(w, "Code").Category);
        Assert.Equal(PlanCategory.Candidate, Item(w, "steam").Category);
        Assert.Equal(gaming.Id, engine.CurrentProfile!.Id); // AnalyzeFor does not select
    }

    [Fact]
    public void Profiles_can_be_renamed_updated_edited_and_deleted()
    {
        var system = new FakeSystem([.. FreshSession(), Steam()]);
        var engine = Engine(system);
        var a = engine.CreateProfile("Gaming", BaselineOrigin.Manual);
        var b = engine.CreateProfile("Work", BaselineOrigin.Manual);

        // Names are unique, trimmed, without control characters and limited in length.
        Assert.Equal("gaming (2)", engine.RenameProfile(b.Id, "  gaming ").Name);
        Assert.Equal("Streaming", engine.RenameProfile(b.Id, "Stream\u0007ing").Name);
        Assert.Equal(ProfileSet.MaxNameLength, engine.RenameProfile(b.Id, new string('x', 100)).Name.Length);

        // Update keeps the name and options, replaces the apps.
        engine.SetEnvironmentPolicy(a.Id, EnvironmentPolicy.Stop, EnvironmentPolicy.Keep);
        system.Replace(Discord());
        var updated = engine.UpdateProfile(a.Id, BaselineOrigin.Manual);
        Assert.Equal("Gaming", updated.Name);
        Assert.Equal(EnvironmentPolicy.Stop, updated.Wsl);
        Assert.Contains(updated.Baseline.Apps, x => x.DisplayName == "Discord");

        // Removing an app from the profile makes it a candidate; adding it back keeps it.
        engine.SelectProfile(a.Id);
        var discordKey = updated.Baseline.Apps.Single(x => x.DisplayName == "Discord").AppKey;
        engine.RemoveAppFromProfile(a.Id, discordKey);
        Assert.Equal(PlanCategory.Candidate, Item(engine.Analyze(), "Discord").Category);
        var groups = engine.Analyze().Groups.Where(g => g.AppKey == discordKey).ToList();
        engine.AddAppsToProfile(a.Id, groups);
        Assert.Equal(PlanCategory.Kept, Item(engine.Analyze(), "Discord").Category);

        // Deleting the selected profile falls back to the default one.
        engine.SetDefaultProfile(b.Id);
        engine.DeleteProfile(a.Id);
        Assert.Equal(b.Id, engine.CurrentProfile!.Id);
        engine.DeleteProfile(b.Id);
        Assert.Null(engine.CurrentProfile);
        Assert.Null(engine.Analyze().Plan);
    }

    [Fact]
    public void Profiles_survive_a_restart_and_the_selection_is_remembered()
    {
        var system = new FakeSystem([.. FreshSession(), Steam()]);
        var engine = Engine(system);
        engine.CreateProfile("Clean", BaselineOrigin.Manual);
        var gaming = engine.CreateProfile("Gaming", BaselineOrigin.Manual);
        engine.SelectProfile(gaming.Id);

        var reloaded = Engine(system);
        Assert.Equal(2, reloaded.Profiles.Profiles.Count);
        Assert.Equal("Gaming", reloaded.CurrentProfile!.Name);
        Assert.Equal("Clean", reloaded.Profiles.Default!.Name);
    }

    [Fact]
    public void A_single_baseline_of_version_0_2_is_migrated_into_a_profile()
    {
        var snap = new FakeSystem([.. FreshSession(), Steam()]).Capture();
        var groups = new AppGrouper(new ProcessClassifier()).Group(snap);
        var legacy = BaselineFactory.Create(snap, groups, BaselineOrigin.Automatic, "PC");
        var paths = new AppPaths(_dir);
        new BaselineStore(new JsonFileStore<Baseline>(paths.Baseline, NullLogger.Instance)).Save(legacy);

        var engine = Engine(new FakeSystem([.. FreshSession(), Steam()]));
        var profile = Assert.Single(engine.Profiles.Profiles);
        Assert.Equal(legacy.Id, profile.Baseline.Id);
        Assert.Equal(profile.Id, engine.Profiles.DefaultProfileId);
        Assert.True(File.Exists(paths.Profiles));
        Assert.True(File.Exists(paths.Baseline)); // the old file is left untouched
    }

    [Fact]
    public void A_corrupt_profiles_file_is_set_aside_and_never_crashes()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(new AppPaths(_dir).Profiles, "{ not json");
        var engine = Engine(new FakeSystem(FreshSession()));
        Assert.Empty(engine.Profiles.Profiles);
        Assert.Single(Directory.GetFiles(_dir, "profiles.json.corrupt-*"));
    }

    [Fact]
    public void Profile_set_normalization_repairs_bad_data()
    {
        var baseline = new Baseline { UserSid = User, Apps = null! };
        var id = Guid.NewGuid();
        var set = new ProfileSet
        {
            Profiles =
            [
                new Profile { Id = id, Name = "A", Baseline = baseline },
                new Profile { Id = id, Name = "duplicate id", Baseline = baseline },
                new Profile { Id = Guid.NewGuid(), Name = "a", Baseline = baseline, Wsl = (EnvironmentPolicy)42 },
                new Profile { Id = Guid.NewGuid(), Name = "missing baseline", Baseline = null! },
            ],
            DefaultProfileId = Guid.NewGuid(),
            CurrentProfileId = Guid.NewGuid(),
        }.Normalize();

        Assert.Equal(["A", "a (2)"], set.Profiles.Select(p => p.Name));
        Assert.Equal(EnvironmentPolicy.Keep, set.Profiles[1].Wsl);
        Assert.Equal(id, set.DefaultProfileId);
        Assert.Equal(id, set.CurrentProfileId);
        Assert.NotNull(set.Profiles[0].Baseline.Apps);
    }

    // ---- Rules -----------------------------------------------------------------------------

    private SnapBackEngine CleanThen(FakeSystem system, params ProcessInfo[] started)
    {
        var engine = Engine(system);
        engine.CreateBaseline(BaselineOrigin.Manual);
        foreach (var p in started)
        {
            system.Replace(p);
        }

        return engine;
    }

    [Fact]
    public void Always_keep_keeps_the_app_in_every_profile()
    {
        var system = new FakeSystem(FreshSession());
        var engine = CleanThen(system, Telegram());
        engine.UpdateSettings(s => s.WithRule(AppKeys.For(Telegram()), "Telegram", AppRuleKind.AlwaysKeep, DateTime.UtcNow));

        var item = Item(engine.Analyze(), "Telegram");
        Assert.Equal(PlanCategory.Kept, item.Category);
        Assert.Equal(KeepReason.UserKeepList, item.KeepReason);
        Assert.False(item.IsSelectable);
    }

    [Fact]
    public void Ask_every_time_offers_the_app_but_never_preselects_it()
    {
        var system = new FakeSystem(FreshSession());
        var engine = CleanThen(system, Telegram());
        Assert.True(Item(engine.Analyze(), "Telegram").DefaultSelected); // a safe app is normally pre-selected

        engine.UpdateSettings(s => s.WithRule(AppKeys.For(Telegram()), "Telegram", AppRuleKind.AskEveryTime, DateTime.UtcNow));
        var item = Item(engine.Analyze(), "Telegram");
        Assert.True(item.IsSelectable);
        Assert.False(item.DefaultSelected);
        Assert.Equal(AppRuleKind.AskEveryTime, item.Rule);
    }

    [Fact]
    public void Always_close_preselects_even_caution_apps_and_apps_of_the_profile()
    {
        // Chrome is a "caution" app (browser) and was running when the profile was captured.
        var chrome = App(3400, ChromePath, 4, parent: 1100, visibleWindows: 1, memoryMb: 800);
        var system = new FakeSystem([.. FreshSession(), chrome]);
        var engine = CleanThen(system, Code());
        Assert.Equal(PlanCategory.Kept, Item(engine.Analyze(), "chrome").Category);
        Assert.False(Item(engine.Analyze(), "Code").DefaultSelected); // editor: possible unsaved work

        engine.UpdateSettings(s => s
            .WithRule(AppKeys.For(chrome), "Chrome", AppRuleKind.AlwaysClose, DateTime.UtcNow)
            .WithRule(AppKeys.For(Code()), "VS Code", AppRuleKind.AlwaysClose, DateTime.UtcNow));

        var a = engine.Analyze();
        var c = Item(a, "chrome");
        Assert.Equal(PlanCategory.Candidate, c.Category);
        Assert.True(c.DefaultSelected);
        Assert.Equal(SafetyLevel.Caution, c.Level); // the risk assessment itself is unchanged
        Assert.Contains("rule", c.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(Item(a, "Code").DefaultSelected);
    }

    [Fact]
    public void No_rule_can_make_a_protected_process_closable()
    {
        var shellLike = App(3500, @"C:\Windows\System32\svchost.exe", 5, parent: 700) with { IsServiceHost = true };
        var elevated = App(3600, @"C:\Tools\admin.exe", 6, parent: 1100, visibleWindows: 1) with { IsElevated = true };
        var system = new FakeSystem(FreshSession());
        var engine = CleanThen(system, shellLike, elevated);

        engine.UpdateSettings(s => s
            .WithRule(AppKeys.For(shellLike), "svchost", AppRuleKind.AlwaysClose, DateTime.UtcNow)
            .WithRule(AppKeys.For(elevated), "admin", AppRuleKind.AlwaysClose, DateTime.UtcNow)
            .WithRule("path:c:\\windows\\explorer.exe", "Explorer", AppRuleKind.AlwaysClose, DateTime.UtcNow));

        var a = engine.Analyze();
        foreach (var item in a.Plan!.All.Where(i => i.Group.IsProtected))
        {
            Assert.Equal(PlanCategory.Protected, item.Category);
            Assert.False(item.IsSelectable);
            Assert.False(item.DefaultSelected);
        }

        Assert.Contains(a.Plan.Protected, i => i.Group.Root.Pid == 3600);
        Assert.Contains(a.Plan.Protected, i => i.Group.Root.ImageName == "explorer.exe");
    }

    [Fact]
    public async Task Always_close_still_closes_politely_and_never_forces()
    {
        var system = new FakeSystem(FreshSession());
        var engine = CleanThen(system, Code());
        system[3200] = Behavior.PromptsUser; // "save changes?"
        engine.UpdateSettings(s => s.WithRule(AppKeys.For(Code()), "VS Code", AppRuleKind.AlwaysClose, DateTime.UtcNow));

        var analysis = engine.Analyze();
        var selected = analysis.Plan!.Candidates.Where(c => c.DefaultSelected).ToList();
        Assert.Single(selected);
        var report = await engine.StartOperation(analysis, selected).RunGracefulAsync();

        Assert.Equal(ItemOutcome.StillRunning, report.Items.Single().Outcome);
        Assert.Equal(StillRunningReason.WaitingForUser, report.Items.Single().Reason);
        Assert.Equal(0, system.TerminateCalls);
        Assert.True(system.IsAlive(3200));
    }

    [Fact]
    public async Task Always_close_does_not_skip_the_live_safety_check()
    {
        var system = new FakeSystem(FreshSession());
        var engine = CleanThen(system, Telegram());
        engine.UpdateSettings(s => s.WithRule(AppKeys.For(Telegram()), "Telegram", AppRuleKind.AlwaysClose, DateTime.UtcNow));
        var analysis = engine.Analyze();

        // Between analysis and action the app was restarted elevated (same PID reused is covered elsewhere).
        system.Replace(Telegram() with { IsElevated = true });
        var report = await engine.StartOperation(analysis, analysis.Plan!.Candidates).RunGracefulAsync();

        Assert.Equal(ItemOutcome.Skipped, report.Items.Single().Outcome);
        Assert.DoesNotContain(system.Calls, c => c.Contains("3300", StringComparison.Ordinal) && !c.StartsWith("state", StringComparison.Ordinal));
        Assert.True(system.IsAlive(3300));
    }

    [Fact]
    public void Rules_are_saved_and_the_old_keep_list_is_migrated()
    {
        var store = new SettingsStore(new JsonFileStore<Settings>(new AppPaths(_dir).Settings, NullLogger.Instance));
        store.Save(new Settings().WithRule("k1", "One", AppRuleKind.AlwaysClose, DateTime.UtcNow).WithRule("k2", "Two", AppRuleKind.AskEveryTime, DateTime.UtcNow));
        var loaded = store.Load();
        Assert.Equal(AppRuleKind.AlwaysClose, loaded.RuleFor("k1"));
        Assert.Equal(AppRuleKind.AskEveryTime, loaded.RuleFor("k2"));
        Assert.Null(loaded.WithRule("k1", "One", null, DateTime.UtcNow).RuleFor("k1"));

        // Version 1 file with a keep list and the autostart-for-baseline option.
        File.WriteAllText(new AppPaths(_dir).Settings, """
            { "schemaVersion": 1, "autoBaselineOnSignIn": true,
              "keepList": [ { "appKey": "path:c:\\apps\\a.exe", "displayName": "A", "addedAtUtc": "2026-01-01T00:00:00Z" } ] }
            """);
        var migrated = store.Load();
        Assert.Equal(AppRuleKind.AlwaysKeep, migrated.RuleFor("path:c:\\apps\\a.exe"));
        Assert.True(migrated.StartWithWindows);
        Assert.True(migrated.AutoBaselineOnSignIn);
        Assert.Null(migrated.KeepList);
        Assert.Equal(Settings.CurrentSchemaVersion, migrated.SchemaVersion);
    }

    [Fact]
    public void New_settings_have_safe_tray_and_startup_defaults()
    {
        var s = new Settings().Normalize();
        Assert.False(s.StartWithWindows);
        Assert.True(s.StartMinimizedToTray);
        Assert.False(s.AutoBaselineOnSignIn);
        Assert.Equal(CloseButtonAction.MinimizeToTray, s.CloseButton);
        Assert.True(s.ShowNotifications);
        Assert.Empty(s.Rules);

        var bad = (new Settings { CloseButton = (CloseButtonAction)9, Rules = [new AppRule("x", "X", (AppRuleKind)77, DateTime.UtcNow), new AppRule("", "empty", AppRuleKind.AlwaysKeep, DateTime.UtcNow)] }).Normalize();
        Assert.Equal(CloseButtonAction.MinimizeToTray, bad.CloseButton);
        Assert.Empty(bad.Rules);
    }

    [Fact]
    public void Settings_with_tray_and_startup_options_round_trip()
    {
        var store = new SettingsStore(new JsonFileStore<Settings>(new AppPaths(_dir).Settings, NullLogger.Instance));
        var profile = Guid.NewGuid();
        store.Save(new Settings
        {
            StartWithWindows = true,
            StartMinimizedToTray = false,
            AutoBaselineOnSignIn = true,
            AutoBaselineProfileId = profile,
            CloseButton = CloseButtonAction.Exit,
            ShowNotifications = false,
            TrayHintShown = true,
        });

        var s = store.Load();
        Assert.True(s.StartWithWindows);
        Assert.False(s.StartMinimizedToTray);
        Assert.True(s.AutoBaselineOnSignIn);
        Assert.Equal(profile, s.AutoBaselineProfileId);
        Assert.Equal(CloseButtonAction.Exit, s.CloseButton);
        Assert.False(s.ShowNotifications);
        Assert.True(s.TrayHintShown);
    }
}
