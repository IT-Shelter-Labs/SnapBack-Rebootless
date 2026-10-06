using System.Text.Json;
using Microsoft.Extensions.Logging;
using SnapBack.Core;
using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Execution;
using SnapBack.Core.Logging;
using SnapBack.Core.Persistence;
using SnapBack.Core.Planning;
using SnapBack.Core.Text;
using SnapBack.Windows;

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
            var (settings, baseline, history) = StoreFactory.Create(paths, loggers);
            var engine = new SnapBackEngine(probe, controller, settings, baseline, history, loggers);

            log.LogInformation("CLI: {Args}", string.Join(' ', args));
            return a.Command switch
            {
                "status" => Status(engine),
                "baseline" => Baseline(engine, a),
                "list" => List(engine, a),
                "preview" => Preview(engine, a),
                "restore" => await Restore(engine, a),
                "history" => History(engine, a),
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

    private static int Status(SnapBackEngine engine)
    {
        var analysis = engine.Analyze();
        Console.WriteLine($"Baseline:           {DescribeBaseline(engine.Baseline)}");
        if (analysis.BaselineProblem is { } problem)
        {
            Console.WriteLine($"                    ! {problem}");
        }

        Console.WriteLine($"Apps running:       {analysis.RunningAppCount} ({analysis.Snapshot.Processes.Count} processes)");
        Console.WriteLine($"Memory in use:      {analysis.Memory.UsedPercent:0}% ({ByteSize.Format(analysis.Memory.UsedPhysicalBytes)} of {ByteSize.Format(analysis.Memory.TotalPhysicalBytes)})");
        if (analysis.HasUsableBaseline)
        {
            Console.WriteLine($"New since baseline: {analysis.NewAppCount} app(s), about {ByteSize.Format(analysis.FreeableBytes)}");
            if (analysis.Comparison is { IsSameBoot: false })
            {
                Console.WriteLine("                    (baseline is from a previous boot; matched by application)");
            }
        }

        return ExitOk;
    }

    private static int Baseline(SnapBackEngine engine, Args a)
    {
        switch (a.Sub)
        {
            case "create":
                if (engine.Baseline is not null && !a.Has("--force"))
                {
                    return Usage("A baseline already exists. Use 'snapback baseline create --force' to replace it.");
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
        var analysis = engine.Analyze();
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
        var analysis = engine.Analyze();
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
        var analysis = engine.Analyze();
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

        if (!a.Has("--yes"))
        {
            Console.Write("Continue? [y/N] ");
            var answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Cancelled.");
                return ExitOk;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var operation = engine.StartOperation(analysis, selected);
        var report = await operation.RunGracefulAsync(cancellationToken: cts.Token);
        engine.RecordHistory(report);

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
            Console.WriteLine($"{r.StartedAtUtc.ToLocalTime():g}  {r.Kind,-8}  {r.Closed}/{r.Requested} closed, {ByteSize.Format(r.MemoryFreedBytes)} released"
                + (r.StillRunning > 0 ? $", {r.StillRunning} still running" : ""));
        }

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

        Console.Error.WriteLine(analysis.BaselineProblem ?? "No baseline yet. Run 'snapback baseline create' first.");
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
        Console.Error.WriteLine("Run 'snapback help' for usage.");
        return ExitUsage;
    }

    private static void PrintHelp() => Console.WriteLine("""
        SnapBack - return your Windows session to its baseline without rebooting.

        Usage:
          snapback status                     Baseline, running apps, memory, what is new
          snapback baseline create [--force]  Make the current state the baseline
          snapback baseline show [--all] [--json]
          snapback baseline delete
          snapback list [--all] [--json]      Apps started after the baseline (--all: also kept/protected)
          snapback preview [selection] [--json]
                                              Dry run: show every step and the live safety verdict
          snapback restore [selection] [--yes]
                                              Ask the selected apps to close (never forced)
          snapback history [--json]

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
