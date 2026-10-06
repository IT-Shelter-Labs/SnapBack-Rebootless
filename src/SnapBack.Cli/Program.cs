using System.Text.Json;
using Microsoft.Extensions.Logging;
using SnapBack.Core;
using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Environments;
using SnapBack.Core.Execution;
using SnapBack.Core.Insights;
using SnapBack.Core.Logging;
using SnapBack.Core.Memory;
using SnapBack.Core.Persistence;
using SnapBack.Core.Planning;
using SnapBack.Core.Profiles;
using SnapBack.Core.Rules;
using SnapBack.Core.Text;
using SnapBack.Windows;
using SnapBack.Windows.Environments;

namespace SnapBack.Cli;

/// <summary>
/// Command-line interface. It shares the engine and every safety check with the GUI, and it
/// deliberately has no way to force-terminate anything: that decision belongs in the GUI, where
/// the user sees what is still running.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitUsage = 2;
    private const int ExitStillRunning = 3;

    private static readonly JsonSerializerOptions Json = JsonFileStore<Settings>.SerializerOptions;

    private static async Task<int> Main(string[] args)
    {
        var a = new Args(args);
        if (a.Command is null or "help" or "-h" or "--help" or "/?")
        {
            PrintHelp();
            return a.Command is null ? ExitUsage : ExitOk;
        }

        if (a.Command is "--version" or "version")
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version);
            return ExitOk;
        }

        var paths = AppPaths.Default();
        using var fileLog = new FileLoggerProvider(paths.Logs);
        using var loggers = LoggerFactory.Create(b => b.AddProvider(fileLog).SetMinimumLevel(LogLevel.Information));
        var log = loggers.CreateLogger("Cli");

        try
        {
            var probe = new WindowsSystemProbe(loggers.CreateLogger<WindowsSystemProbe>());
            var controller = new WindowsProcessController(probe.BaseContext, loggers.CreateLogger<WindowsProcessController>());
            var (settings, profiles, history) = StoreFactory.Create(paths, loggers);
            var engine = new SnapBackEngine(probe, controller, settings, profiles, history, loggers);

            log.LogInformation("CLI: {Args}", string.Join(' ', args));
            if (a.Value("--profile") is { } profileName && engine.Profiles.FindByName(profileName) is null)
            {
                return Usage($"No profile named '{profileName}'. Run 'snapback-cli profiles'.");
            }

            return a.Command switch
            {
                "status" => Status(engine, a),
                "baseline" => Baseline(engine, a),
                "list" => List(engine, a),
                "preview" => Preview(engine, a),
                "restore" => await Restore(engine, a),
                "history" => History(engine, a),
                "memory" => Memory(engine, a),
                "profiles" => Profiles(engine, a),
                "profile" => Profile(engine, a),
                "rules" => Rules(engine, a),
                "rule" => Rule(engine, a),
                "env" => await Env(probe, loggers, a),
                _ => Usage($"Unknown command '{a.Command}'."),
            };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "CLI command failed");
            Console.Error.WriteLine("Error: " + ex.Message);
            return ExitError;
        }
    }

    /// <summary>Analysis against --profile, or the selected profile.</summary>
    private static Analysis Analyze(SnapBackEngine engine, Args a) =>
        a.Value("--profile") is { } name ? engine.AnalyzeFor(engine.Profiles.FindByName(name)!.Id) : engine.Analyze();

    private static int Status(SnapBackEngine engine, Args a)
    {
        var analysis = Analyze(engine, a);
        var drift = analysis.Drift(DateTime.UtcNow);
        var impact = analysis.DefaultImpact();
        if (a.Has("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                profile = analysis.Profile?.Name,
                baselineCreatedUtc = analysis.Baseline?.CreatedAtUtc,
                sessionUptimeMinutes = drift.SessionUptime is { } up ? (int)up.TotalMinutes : (int?)null,
                apps = analysis.RunningAppCount,
                processes = analysis.Snapshot.Processes.Count,
                memory = new { inUseBytes = analysis.Memory.UsedPhysicalBytes, availableBytes = analysis.Memory.AvailablePhysicalBytes, cacheBytes = analysis.Memory.CachedBytes },
                drift = new { level = drift.Level, drift.Score, drift.NewApps, drift.ProcessDelta, drift.NewAppMemoryBytes, drift.MainFactor },
                impact = new { level = impact.Level, impact.EstimatedReclaimBytes, impact.AppsToClose, breakdown = impact.Breakdown },
                environments = EnvironmentDetector.Detect(analysis.Snapshot).Where(e => e.IsRunning).Select(e => new { e.Name, e.MemoryBytes }),
            }, Json));
            return ExitOk;
        }

        Console.WriteLine($"Profile:            {analysis.Profile?.Name ?? "none"}  ({DescribeBaseline(analysis.Baseline)})");
        if (analysis.BaselineProblem is { } problem)
        {
            Console.WriteLine($"                    ! {problem}");
        }

        if (drift.SessionUptime is { } uptime)
        {
            Console.WriteLine($"Session uptime:     {(int)uptime.TotalHours}h {uptime.Minutes:00}m");
        }

        Console.WriteLine($"Apps running:       {analysis.RunningAppCount} ({analysis.Snapshot.Processes.Count} processes)");
        Console.WriteLine($"RAM in use:         {ByteSize.Format(analysis.Memory.UsedPhysicalBytes)} of {ByteSize.Format(analysis.Memory.TotalPhysicalBytes)}, available {ByteSize.Format(analysis.Memory.AvailablePhysicalBytes)}");
        if (analysis.HasUsableBaseline)
        {
            Console.WriteLine($"New since baseline: {analysis.NewAppCount} app(s), about {ByteSize.Format(analysis.FreeableBytes)}");
            Console.WriteLine($"Session drift:      {drift.Level} (score {drift.Score}; {drift.NewApps:+0;-0;0} apps, {drift.ProcessDelta:+0;-0;0} processes, +{ByteSize.Format(drift.NewAppMemoryBytes)})");
            Console.WriteLine($"Expected impact:    {impact.Level} (~{ByteSize.Format(impact.EstimatedReclaimBytes)} from {impact.AppsToClose} pre-selected app(s); an estimate)");
            var b = impact.Breakdown;
            Console.WriteLine($"  In use: new apps {ByteSize.Format(b.NewAppsBytes)}, profile apps {ByteSize.Format(b.ProfileAppsBytes)}, system/other {ByteSize.Format(b.SystemAndOtherBytes)}; cache (available) {(b.CacheBytes is { } c ? ByteSize.Format(c) : "n/a")}");
            if (analysis.Comparison is { IsSameBoot: false })
            {
                Console.WriteLine("                    (baseline is from a previous boot; matched by application)");
            }
        }

        foreach (var e in EnvironmentDetector.Detect(analysis.Snapshot).Where(e => e.IsRunning))
        {
            Console.WriteLine($"{e.Name + ":",-20}running, {ByteSize.Format(e.MemoryBytes)} (stop it from the SnapBack window or with its own command)");
        }

        return ExitOk;
    }

    private static int Profiles(SnapBackEngine engine, Args a)
    {
        var set = engine.Profiles;
        if (a.Has("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(set.Profiles.Select(p => new
            {
                p.Id,
                p.Name,
                isDefault = p.Id == set.DefaultProfileId,
                isCurrent = p.Id == set.Current?.Id,
                apps = p.AppCount,
                p.UpdatedAtUtc,
                wsl = p.Wsl,
                docker = p.Docker,
            }), Json));
            return ExitOk;
        }

        if (set.Profiles.Count == 0)
        {
            Console.WriteLine("No profiles. Run 'snapback-cli profile create <name>' (or 'baseline create').");
        }

        foreach (var p in set.Profiles)
        {
            var marks = (p.Id == set.DefaultProfileId ? " default" : "") + (p.Id == set.Current?.Id ? " current" : "");
            Console.WriteLine($"{p.Name,-24} {p.AppCount,3} apps  updated {p.UpdatedAtUtc.ToLocalTime():g}{marks}");
        }

        return ExitOk;
    }

    private static int Profile(SnapBackEngine engine, Args a)
    {
        var name = a.Positional(2);
        Profile? Find(string? n) => n is null ? null : engine.Profiles.FindByName(n);
        switch (a.Sub)
        {
            case "create" when name is not null:
                var created = engine.CreateProfile(name, BaselineOrigin.Manual);
                Console.WriteLine($"Profile '{created.Name}' created: {created.AppCount} apps.");
                return ExitOk;
            case "update":
                var target = name is null ? engine.CurrentProfile : Find(name);
                if (target is null)
                {
                    return Usage("No such profile.");
                }

                var updated = engine.UpdateProfile(target.Id, BaselineOrigin.Manual);
                Console.WriteLine($"Profile '{updated.Name}' updated: {updated.AppCount} apps.");
                return ExitOk;
            case "rename" when Find(name) is { } p && a.Positional(3) is { } newName:
                Console.WriteLine($"Renamed to '{engine.RenameProfile(p.Id, newName).Name}'.");
                return ExitOk;
            case "delete" when Find(name) is { } p:
                if (!a.Has("--yes") && !Ask($"Delete profile '{p.Name}'?"))
                {
                    return ExitOk;
                }

                engine.DeleteProfile(p.Id);
                Console.WriteLine("Profile deleted.");
                return ExitOk;
            case "default" when Find(name) is { } p:
                engine.SetDefaultProfile(p.Id);
                Console.WriteLine($"'{p.Name}' is the default profile.");
                return ExitOk;
            case "use" when Find(name) is { } p:
                engine.SelectProfile(p.Id);
                Console.WriteLine($"Comparing with '{p.Name}'. Nothing was closed.");
                return ExitOk;
            default:
                return Usage("Usage: snapback-cli profile create|update|rename|delete|default|use <name> [<new name>]");
        }
    }

    private static int Rules(SnapBackEngine engine, Args a)
    {
        var rules = engine.Settings.Rules;
        if (a.Has("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(rules, Json));
            return ExitOk;
        }

        if (rules.Count == 0)
        {
            Console.WriteLine("No rules.");
        }

        foreach (var r in rules.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Console.WriteLine($"{r.DisplayName,-32} {r.Kind,-14} {r.AppKey}");
        }

        return ExitOk;
    }

    /// <summary>snapback-cli rule keep|close|ask|clear &lt;app&gt; — the app must be running (or have a rule, to clear it).</summary>
    private static int Rule(SnapBackEngine engine, Args a)
    {
        var name = a.Positional(2);
        AppRuleKind? kind = a.Sub switch
        {
            "keep" => AppRuleKind.AlwaysKeep,
            "close" => AppRuleKind.AlwaysClose,
            "ask" => AppRuleKind.AskEveryTime,
            _ => null,
        };
        if (name is null || (kind is null && a.Sub != "clear"))
        {
            return Usage("Usage: snapback-cli rule keep|close|ask|clear <app name or executable>");
        }

        if (a.Sub == "clear")
        {
            var rule = engine.Settings.Rules.FirstOrDefault(r => r.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase) || r.AppKey == name);
            if (rule is null)
            {
                return Usage($"No rule for '{name}'.");
            }

            engine.UpdateSettings(s => s.WithRule(rule.AppKey, rule.DisplayName, null, DateTime.UtcNow));
            Console.WriteLine($"Rule for {rule.DisplayName} removed.");
            return ExitOk;
        }

        var groups = engine.Analyze().Groups.Where(g => !g.IsProtected && Matches(g, name)).DistinctBy(g => g.AppKey).ToList();
        if (groups.Count != 1)
        {
            return Usage(groups.Count == 0 ? $"No running app matches '{name}'." : $"'{name}' matches several apps: {string.Join(", ", groups.Select(g => g.DisplayName))}");
        }

        var g = groups[0];
        engine.UpdateSettings(s => s.WithRule(g.AppKey, g.DisplayName, kind, DateTime.UtcNow));
        Console.WriteLine($"{g.DisplayName}: {kind}. Rules never override protection; closing stays polite.");
        return ExitOk;
    }

    /// <summary>Read-only: shows WSL / Docker and what stopping them would end. Stopping happens in the window.</summary>
    private static async Task<int> Env(WindowsSystemProbe probe, ILoggerFactory loggers, Args a)
    {
        var service = new EnvironmentService(new ProcessCommandRunner(), EnvironmentToolLocator.Find(), probe.CaptureLight,
            TimeProvider.System, loggers.CreateLogger<EnvironmentService>());
        var details = new List<EnvironmentDetails>();
        foreach (var kind in Enum.GetValues<EnvironmentKind>())
        {
            details.Add(await service.InspectAsync(kind));
        }

        if (a.Has("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(details.Select(d => new
            {
                d.Name, d.IsRunning, d.MemoryBytes, d.RunningItems, d.ItemsKnown, d.CanStop, d.CannotStopReason,
            }), Json));
            return ExitOk;
        }

        foreach (var d in details)
        {
            if (!d.IsRunning)
            {
                Console.WriteLine($"{d.Name}: not running");
                continue;
            }

            Console.WriteLine($"{d.Name}: running, {ByteSize.Format(d.MemoryBytes)}");
            Console.WriteLine($"  {(d.Kind == EnvironmentKind.Wsl ? "Distributions" : "Containers")}: {(d.ItemsKnown ? (d.RunningItems.Count > 0 ? string.Join(", ", d.RunningItems) : "none") : "unknown")}");
            Console.WriteLine(d.CanStop
                ? $"  Can be stopped with {(d.Kind == EnvironmentKind.Wsl ? "'wsl --shutdown'" : "'docker desktop stop'")} (use the SnapBack window, which shows a warning first)."
                : $"  {d.CannotStopReason}");
        }

        return ExitOk;
    }

    private static bool Matches(Core.Grouping.AppGroup g, string name) =>
        g.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)
        || g.Root.ImageName.Equals(name, StringComparison.OrdinalIgnoreCase)
        || Path.GetFileNameWithoutExtension(g.Root.ImageName).Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool Ask(string question)
    {
        Console.Write(question + " [y/N] ");
        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int Baseline(SnapBackEngine engine, Args a)
    {
        switch (a.Sub)
        {
            case "create":
                if (engine.Baseline is not null && !a.Has("--force"))
                {
                    return Usage("A baseline already exists. Use 'snapback-cli baseline create --force' to replace it.");
                }

                var b = engine.CreateBaseline(BaselineOrigin.Manual);
                Console.WriteLine($"Baseline created: {b.Apps.Count(x => !x.WasProtected)} apps, {b.ProcessCount} processes.");
                return ExitOk;

            case "show" or null:
                if (engine.Baseline is not { } baseline)
                {
                    Console.WriteLine("No baseline.");
                    return ExitOk;
                }

                if (a.Has("--json"))
                {
                    Console.WriteLine(JsonSerializer.Serialize(baseline, Json));
                    return ExitOk;
                }

                Console.WriteLine(DescribeBaseline(baseline));
                foreach (var app in baseline.Apps.Where(x => !x.WasProtected || a.Has("--all")))
                {
                    Console.WriteLine($"  {app.DisplayName,-40} {app.Instances.Count} instance(s), {app.ProcessCount,3} process(es)  {app.ExecutablePath}");
                }

                return ExitOk;

            case "delete":
                engine.DeleteBaseline();
                Console.WriteLine("Baseline deleted.");
                return ExitOk;

            default:
                return Usage($"Unknown baseline command '{a.Sub}'.");
        }
    }

    private static int List(SnapBackEngine engine, Args a)
    {
        var analysis = Analyze(engine, a);
        if (!RequirePlan(analysis))
        {
            return ExitError;
        }

        var plan = analysis.Plan!;
        if (a.Has("--json"))
        {
            var items = (a.Has("--all") ? plan.All : plan.Candidates).Select(ToJson);
            Console.WriteLine(JsonSerializer.Serialize(items, Json));
            return ExitOk;
        }

        PrintItems("New since baseline", plan.Candidates);
        if (a.Has("--all"))
        {
            PrintItems("Kept", plan.Kept);
            PrintItems("Protected", plan.Protected);
        }

        return ExitOk;
    }

    private static int Preview(SnapBackEngine engine, Args a)
    {
        var analysis = Analyze(engine, a);
        if (!RequirePlan(analysis))
        {
            return ExitError;
        }

        var selected = Select(analysis.Plan!, a);
        if (selected.Count == 0)
        {
            Console.WriteLine("Nothing selected.");
            return ExitOk;
        }

        var report = engine.DryRun(analysis, selected);
        if (a.Has("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(report.Items.Select(i => new
            {
                app = i.Item.Group.DisplayName,
                allowed = i.ItemDecision.Allowed,
                reason = i.ItemDecision.Message,
                steps = i.Steps.Select(s => new { s.Step.Kind, s.Step.Description, s.Decision.Allowed, s.Decision.Message }),
            }), Json));
            return ExitOk;
        }

        Console.WriteLine("Preview (dry run) - nothing is changed:");
        foreach (var item in report.Items)
        {
            Console.WriteLine();
            Console.WriteLine($"{item.Item.Group.DisplayName}  [{Badge(item.Item)}]  {ByteSize.Format(item.Item.Group.ActionablePrivateWorkingSet)}");
            if (!item.ItemDecision.Allowed)
            {
                Console.WriteLine("  would be skipped: " + item.ItemDecision.Message);
            }

            foreach (var step in item.Steps)
            {
                var mark = step.Step.Kind == StepKind.LeftAlone ? "-" : step.Decision.Allowed ? "+" : "x";
                Console.WriteLine($"  {mark} {step.Step.Description}");
                if (!step.Decision.Allowed && step.Step.Kind != StepKind.LeftAlone)
                {
                    Console.WriteLine("      " + step.Decision.Message);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{report.WouldActOn} of {report.Items.Count} app(s) would be asked to close. Nothing is ever force-terminated by the CLI.");
        return ExitOk;
    }

    private static async Task<int> Restore(SnapBackEngine engine, Args a)
    {
        var analysis = Analyze(engine, a);
        if (!RequirePlan(analysis))
        {
            return ExitError;
        }

        var selected = Select(analysis.Plan!, a);
        if (selected.Count == 0)
        {
            Console.WriteLine("Nothing selected.");
            return ExitOk;
        }

        Console.WriteLine("These apps will be asked to close (nothing is forced):");
        foreach (var item in selected)
        {
            Console.WriteLine($"  {item.Group.DisplayName,-40} {ByteSize.Format(item.Group.ActionablePrivateWorkingSet),8}  [{Badge(item)}]");
        }

        var impact = ImpactEstimate.Compute(analysis, selected);
        Console.WriteLine($"Expected impact: {impact.Level} (~{ByteSize.Format(impact.EstimatedReclaimBytes)}; an estimate, not a promise)");

        if (!a.Has("--yes") && !Ask("Continue?"))
        {
            Console.WriteLine("Cancelled.");
            return ExitOk;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var before = SessionStatistics.From(analysis);
        var operation = engine.StartOperation(analysis, selected);
        var report = await operation.RunGracefulAsync(cancellationToken: cts.Token);
        engine.RecordHistory(report, analysis.Profile);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var after = SessionStatistics.From(engine.Analyze());
        engine.UpdateHistory(report.OperationId, r => (r ?? OperationRecord.From(report)).WithStatistics(before, after));

        foreach (var r in report.Items)
        {
            Console.WriteLine($"  {r.Item.Group.DisplayName,-40} {r.Message}");
            foreach (var note in r.Notes)
            {
                Console.WriteLine("      " + note);
            }
        }

        Console.WriteLine(report.Summary + ".");
        if (report.StillRunningCount > 0)
        {
            Console.WriteLine("Apps that are still running were not forced. Use the SnapBack window if you want to force-terminate them.");
            return ExitStillRunning;
        }

        return ExitOk;
    }

    private static int History(SnapBackEngine engine, Args a)
    {
        var records = engine.LoadHistory();
        if (a.Has("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(records, Json));
            return ExitOk;
        }

        if (records.Count == 0)
        {
            Console.WriteLine("No operations yet.");
        }

        foreach (var r in records)
        {
            Console.WriteLine($"{r.StartedAtUtc.ToLocalTime():g}  {r.ProfileName ?? "-",-12}  {r.Closed}/{r.Requested} closed, {ByteSize.Format(r.MemoryFreedBytes)} released by apps"
                + (r.StillRunning > 0 ? $", {r.StillRunning} still running" : "")
                + (r.ForceTerminations > 0 ? $", {r.ForceTerminations} process(es) forced" : "")
                + (r.DeepCleanupUsed ? $", deep cleanup {ByteSize.Format(r.DeepCleanupReclaimedBytes ?? 0)} cache" : "")
                + (r.EnvironmentsStopped.Count > 0 ? $", stopped {string.Join("/", r.EnvironmentsStopped)}" : ""));
            if (r.AppsBefore is { } ab && r.AppsAfter is { } aa)
            {
                Console.WriteLine($"                   apps {ab} -> {aa}, processes {r.ProcessesBefore} -> {r.ProcessesAfter}, "
                    + $"in use {Opt(r.InUseBeforeBytes)} -> {Opt(r.InUseAfterBytes)}, available {ByteSize.Format(r.AvailableBeforeBytes)} -> {ByteSize.Format(r.AvailableAfterBytes)}, {r.Duration.TotalSeconds:0} s");
            }
        }

        return ExitOk;
    }

    private static string Opt(long? v) => v is { } x ? ByteSize.Format(x) : "n/a";

    /// <summary>Read-only memory report. The CLI never runs Deep Memory Cleanup (it needs a UAC prompt; use the window).</summary>
    private static int Memory(SnapBackEngine engine, Args a)
    {
        var m = engine.ReadMemory();
        var advice = MemoryAdvisor.Advise(m);
        if (a.Has("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                totalBytes = m.TotalPhysicalBytes,
                inUseBytes = m.UsedPhysicalBytes,
                availableBytes = m.AvailablePhysicalBytes,
                standbyBytes = m.StandbyBytes,
                modifiedBytes = m.ModifiedBytes,
                freeBytes = m.FreeBytes,
                commitBytes = m.CommitTotalBytes,
                commitLimitBytes = m.CommitLimitBytes,
                pressure = advice.Pressure,
            }, Json));
            return ExitOk;
        }

        Console.WriteLine($"Installed:           {ByteSize.Format(m.TotalPhysicalBytes)}");
        Console.WriteLine($"In use:              {ByteSize.Format(m.UsedPhysicalBytes)} ({m.UsedPercent:0}%)");
        Console.WriteLine($"Available:           {ByteSize.Format(m.AvailablePhysicalBytes)} ({m.AvailablePercent:0}%)");
        Console.WriteLine($"  Standby (cache):   {Opt(m.StandbyBytes)}");
        Console.WriteLine($"  Free:              {Opt(m.FreeBytes)}");
        Console.WriteLine($"Modified:            {Opt(m.ModifiedBytes)}");
        Console.WriteLine($"Committed:           {ByteSize.Format(m.CommitTotalBytes)} of {ByteSize.Format(m.CommitLimitBytes)}");
        Console.WriteLine();
        Console.WriteLine(advice.Title + ". " + advice.Text);
        return ExitOk;
    }

    /// <summary>Default selection = what the GUI pre-selects; --app filters by name; --include-* widen it.</summary>
    private static List<PlanItem> Select(CleanupPlan plan, Args a)
    {
        var names = a.Values("--app");
        IEnumerable<PlanItem> items = plan.Candidates;
        if (names.Count > 0)
        {
            items = items.Where(i => names.Any(n =>
                i.Group.DisplayName.Contains(n, StringComparison.OrdinalIgnoreCase)
                || i.Group.Root.ImageName.Equals(n, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(i.Group.Root.ImageName).Equals(n, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            items = items.Where(i => i.DefaultSelected
                || (a.Has("--include-caution") && i.Level == SafetyLevel.Caution)
                || (a.Has("--include-review") && i.Level == SafetyLevel.ManualReview));
        }

        return items.ToList();
    }

    private static bool RequirePlan(Analysis analysis)
    {
        if (analysis.HasUsableBaseline)
        {
            return true;
        }

        Console.Error.WriteLine(analysis.BaselineProblem ?? "No baseline yet. Run 'snapback-cli baseline create' first.");
        return false;
    }

    private static void PrintItems(string title, IReadOnlyList<PlanItem> items)
    {
        Console.WriteLine($"{title} ({items.Count}):");
        foreach (var i in items)
        {
            var detail = i.Group.Detail is { } d ? $" ({d})" : "";
            Console.WriteLine($"  {i.Group.DisplayName + detail,-44} {ByteSize.Format(i.Group.PrivateWorkingSet),8} {i.Group.ProcessCount,4} proc  [{Badge(i)}] {i.Summary}");
        }

        Console.WriteLine();
    }

    private static object ToJson(PlanItem i) => new
    {
        app = i.Group.DisplayName,
        appKey = i.Group.AppKey,
        rootPid = i.Group.Root.Pid,
        category = i.Category,
        level = i.Level,
        reasons = i.Reasons,
        summary = i.Summary,
        privateWorkingSet = i.Group.PrivateWorkingSet,
        processes = i.Group.ProcessCount,
        preselected = i.DefaultSelected,
    };

    private static string Badge(PlanItem item) => item.Category switch
    {
        PlanCategory.Kept => "kept",
        PlanCategory.Protected => "protected",
        _ => item.Level switch
        {
            SafetyLevel.Safe => "safe",
            SafetyLevel.Caution => "caution",
            _ => "manual review",
        },
    };

    private static string DescribeBaseline(Baseline? b) => b is null
        ? "none"
        : $"{b.CreatedAtUtc.ToLocalTime():g} ({b.Origin.ToString().ToLowerInvariant()}), {b.Apps.Count(x => !x.WasProtected)} apps";

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run 'snapback-cli help' for usage.");
        return ExitUsage;
    }

    private static void PrintHelp() => Console.WriteLine("""
        SnapBack - return your Windows session to its baseline without rebooting.

        Usage:
          snapback-cli status [--json]             Profile, drift, memory, expected impact, WSL/Docker
          snapback-cli baseline create [--force]   Make the current state the baseline of the selected profile
          snapback-cli baseline show [--all] [--json]
          snapback-cli baseline delete
          snapback-cli list [--all] [--json]       Apps started after the baseline (--all: also kept/protected)
          snapback-cli preview [selection] [--json]
                                                   Dry run: show every step and the live safety verdict
          snapback-cli restore [selection] [--yes]
                                                   Ask the selected apps to close (never forced)
          snapback-cli history [--json]
          snapback-cli memory [--json]             Memory in use vs. reusable cache, with advice
          snapback-cli profiles [--json]           List profiles
          snapback-cli profile create <name>       New profile from the current state
          snapback-cli profile update [<name>]     Replace a profile's apps with the current state
          snapback-cli profile rename <name> <new name>
          snapback-cli profile delete <name> [--yes]
          snapback-cli profile default <name>      Profile used at startup and by the automatic baseline
          snapback-cli profile use <name>          Compare with this profile (closes nothing)
          snapback-cli rules [--json]              Always keep / always close / ask every time
          snapback-cli rule keep|close|ask|clear <app>
          snapback-cli env [--json]                WSL / Docker status (read-only; stop them from the window)

        Options:
          --profile <name>      status/list/preview/restore against this profile instead of the selected one

        Selection (default: the apps the GUI pre-selects, i.e. those marked safe):
          --app <name>          Only apps whose name or executable matches (repeatable)
          --include-caution     Also apps marked with a caution
          --include-review      Also apps that need manual review

        Exit codes: 0 ok, 1 error, 2 usage, 3 some apps are still running.
        The CLI never force-terminates anything and never needs administrator rights.
        """);

    private sealed class Args
    {
        private readonly List<string> _args;

        public Args(string[] args)
        {
            _args = args.ToList();
            Command = _args.Count > 0 ? _args[0].ToLowerInvariant() : null;
            Sub = _args.Count > 1 && !_args[1].StartsWith('-') ? _args[1].ToLowerInvariant() : null;
        }

        public string? Command { get; }

        public string? Sub { get; }

        public bool Has(string flag) => _args.Contains(flag, StringComparer.OrdinalIgnoreCase);

        /// <summary>The n-th argument (0 = command) unless it is an option.</summary>
        public string? Positional(int index) => index < _args.Count && !_args[index].StartsWith("--", StringComparison.Ordinal) ? _args[index] : null;

        public string? Value(string option) => Values(option).FirstOrDefault();

        public IReadOnlyList<string> Values(string option)
        {
            var result = new List<string>();
            for (var i = 0; i < _args.Count - 1; i++)
            {
                if (string.Equals(_args[i], option, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(_args[i + 1]);
                }
            }

            return result;
        }
    }
}
