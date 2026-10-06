using Microsoft.Extensions.Logging.Abstractions;
using SnapBack.Core.Baselines;
using SnapBack.Core.Classification;
using SnapBack.Core.Environments;
using SnapBack.Core.Grouping;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
using SnapBack.Core.Rules;
using static SnapBack.Core.Tests.Fakes.TestData;

namespace SnapBack.Core.Tests;

public class EnvironmentTests
{
    private const string Wsl = @"C:\Windows\System32\wsl.exe";
    private const string Docker = @"C:\Program Files\Docker\Docker\resources\bin\docker.exe";
    private const long MiB = 1L << 20;

    private static ProcessInfo VmmemWsl() => new()
    {
        Identity = new ProcessIdentity(7000, At(20)),
        ImageName = "vmmemWSL",
        SessionId = 0,
        WorkingSet = 3800 * MiB,
    };

    private static ProcessInfo WslHost() => App(7100, @"C:\Program Files\WSL\wslhost.exe", 20, parent: 1100);

    private static IEnumerable<ProcessInfo> DockerDesktop() =>
    [
        App(7200, @"C:\Program Files\Docker\Docker\Docker Desktop.exe", 25, parent: 1100, visibleWindows: 1, memoryMb: 250),
        App(7201, @"C:\Program Files\Docker\Docker\resources\com.docker.backend.exe", 25.1, parent: 7200, memoryMb: 120),
        App(7202, @"C:\Program Files\Docker\Docker\resources\com.docker.build.exe", 25.2, parent: 7201, memoryMb: 30),
    ];

    private static SystemSnapshot Snap(IEnumerable<ProcessInfo> extra) => Snapshot([.. FreshSession(), .. extra]);

    // ---- Detection and classification -------------------------------------------------------

    [Fact]
    public void Wsl_and_docker_are_recognized_from_the_process_list()
    {
        var all = EnvironmentDetector.Detect(Snap([VmmemWsl(), WslHost(), .. DockerDesktop()]));
        var wsl = all.Single(e => e.Kind == EnvironmentKind.Wsl);
        var docker = all.Single(e => e.Kind == EnvironmentKind.Docker);

        Assert.True(wsl.IsRunning);
        Assert.Equal(3800 * MiB, wsl.MemoryBytes);
        Assert.True(docker.IsRunning);
        Assert.Equal(400 * MiB, docker.MemoryBytes);

        Assert.All(EnvironmentDetector.Detect(Snap([])), e => Assert.False(e.IsRunning));
    }

    [Fact]
    public void A_plain_vmmem_without_wsl_helpers_is_not_taken_for_wsl()
    {
        var hyperV = VmmemWsl() with { ImageName = "vmmem" };
        Assert.False(EnvironmentDetector.For(Snap([hyperV]), EnvironmentKind.Wsl).IsRunning);
        Assert.True(EnvironmentDetector.For(Snap([hyperV, WslHost()]), EnvironmentKind.Wsl).IsRunning);
    }

    [Theory]
    [InlineData("Docker Desktop.exe")]
    [InlineData("com.docker.backend.exe")]
    [InlineData("com.docker.build.exe")]
    [InlineData("vpnkit.exe")]
    [InlineData("wslhost.exe")]
    [InlineData("wslrelay.exe")]
    [InlineData("vmmemWSL")]
    public void Wsl_and_docker_processes_are_protected(string name)
    {
        var p = App(7300, $@"C:\Program Files\Something\{name}", 30, parent: 1100, visibleWindows: 1) with { ImageName = name };
        var a = new ProcessClassifier().Classify(p, Context());
        Assert.Equal(SafetyLevel.Protected, a.Level);
        Assert.Contains(RiskReason.ManagedEnvironment, a.Reasons);
    }

    [Fact]
    public void Docker_desktop_is_never_offered_for_closing_even_with_an_always_close_rule()
    {
        var before = Snap([]);
        var grouper = new AppGrouper(new ProcessClassifier());
        var baseline = BaselineFactory.Create(before, grouper.Group(before), BaselineOrigin.Manual, "PC");
        var now = Snap([.. DockerDesktop(), VmmemWsl(), WslHost()]);
        var groups = grouper.Group(now);
        var rules = groups.ToDictionary(g => g.AppKey, _ => AppRuleKind.AlwaysClose);

        var plan = new CleanupPlanner().Build(groups, BaselineComparer.Compare(baseline, groups, now.Context), new PlannerOptions { Rules = rules });

        Assert.DoesNotContain(plan.Candidates, c => c.Group.Members.Any(m => EnvironmentDetector.IsDocker(m.ImageName) || m.ImageName.StartsWith("wsl", StringComparison.Ordinal)));
        Assert.Contains(plan.Protected, p => p.Group.Root.ImageName == "Docker Desktop.exe");
    }

    // ---- Inspecting and stopping ------------------------------------------------------------

    private sealed class FakeRunner : ICommandRunner
    {
        public List<string> Commands { get; } = [];

        public Dictionary<string, CommandResult> Results { get; } = new();

        public Action<string>? OnRun { get; set; }

        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            var key = executable.Split('\\')[^1] + " " + string.Join(' ', arguments);
            Commands.Add(key);
            OnRun?.Invoke(key);
            return Task.FromResult(Results.TryGetValue(key, out var r) ? r : new CommandResult(0, ""));
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(Boot.AddHours(2));

        /// <summary>Time that passes on every reading (simulates waiting).</summary>
        public TimeSpan Step { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            Now += Step;
            return Now;
        }
    }

    private sealed class World
    {
        public List<ProcessInfo> Extra { get; } = [];

        public SystemSnapshot Capture() => Snap(Extra);
    }

    private static (EnvironmentService Service, FakeRunner Runner, World World, Clock Clock) Setup(bool docker = true, bool wsl = true)
    {
        var world = new World();
        var runner = new FakeRunner();
        var clock = new Clock();
        var tools = new EnvironmentTools(wsl ? Wsl : null, docker ? Docker : null);
        var service = new EnvironmentService(runner, tools, world.Capture, clock, NullLogger.Instance) { PollInterval = TimeSpan.Zero };
        return (service, runner, world, clock);
    }

    [Fact]
    public async Task Nothing_is_run_when_the_environment_is_not_running()
    {
        var (service, runner, _, _) = Setup();
        Assert.False((await service.InspectAsync(EnvironmentKind.Wsl)).IsRunning);
        Assert.False((await service.InspectAsync(EnvironmentKind.Docker)).IsRunning);
        Assert.Empty(runner.Commands); // inspecting never starts WSL or Docker
    }

    [Fact]
    public async Task Wsl_details_list_running_distributions_and_tolerate_utf16_output()
    {
        var (service, runner, world, _) = Setup();
        world.Extra.AddRange([VmmemWsl(), WslHost()]);
        runner.Results["wsl.exe --list --running --quiet"] = new CommandResult(0, "\uFEFFU\0b\0u\0n\0t\0u\0\r\0\n\0docker-desktop\r\n");

        var d = await service.InspectAsync(EnvironmentKind.Wsl);
        Assert.True(d.IsRunning);
        Assert.Equal(["Ubuntu", "docker-desktop"], d.RunningItems);
        Assert.True(d.DockerDependsOnIt);
        Assert.True(d.CanStop);
        Assert.Contains("Ubuntu", d.Warning, StringComparison.Ordinal);
        Assert.Contains("Docker", d.Warning, StringComparison.Ordinal);
        Assert.Equal(["wsl.exe --list --running --quiet"], runner.Commands);
    }

    [Fact]
    public async Task Wsl_is_stopped_only_with_wsl_shutdown_after_confirmation()
    {
        var (service, runner, world, clock) = Setup();
        world.Extra.AddRange([VmmemWsl(), WslHost()]);
        runner.Results["wsl.exe --list --running --quiet"] = new CommandResult(0, "Ubuntu\n");
        runner.OnRun = cmd =>
        {
            if (cmd == "wsl.exe --shutdown")
            {
                world.Extra.Clear(); // the VM goes away
            }
        };

        var shown = await service.InspectAsync(EnvironmentKind.Wsl);
        var confirmation = EnvironmentStopConfirmation.FromUserConfirmation(shown, clock.Now.UtcDateTime);
        var result = await service.StopAsync(confirmation);

        Assert.Equal(EnvironmentStopStatus.Stopped, result.Status);
        Assert.Equal(["wsl.exe --list --running --quiet", "wsl.exe --list --running --quiet", "wsl.exe --shutdown"], runner.Commands);
    }

    [Fact]
    public async Task Nothing_is_stopped_when_something_new_started_after_the_confirmation()
    {
        var (service, runner, world, clock) = Setup();
        world.Extra.AddRange([VmmemWsl(), WslHost()]);
        runner.Results["wsl.exe --list --running --quiet"] = new CommandResult(0, "Ubuntu\n");
        var confirmation = EnvironmentStopConfirmation.FromUserConfirmation(await service.InspectAsync(EnvironmentKind.Wsl), clock.Now.UtcDateTime);

        runner.Results["wsl.exe --list --running --quiet"] = new CommandResult(0, "Ubuntu\nDebian\n");
        var result = await service.StopAsync(confirmation);

        Assert.Equal(EnvironmentStopStatus.Changed, result.Status);
        Assert.DoesNotContain("wsl.exe --shutdown", runner.Commands);
    }

    [Fact]
    public async Task An_old_confirmation_is_refused()
    {
        var (service, runner, world, clock) = Setup();
        world.Extra.AddRange([VmmemWsl(), WslHost()]);
        var confirmation = EnvironmentStopConfirmation.FromUserConfirmation(await service.InspectAsync(EnvironmentKind.Wsl), clock.Now.UtcDateTime);
        clock.Now += TimeSpan.FromMinutes(10);

        var result = await service.StopAsync(confirmation);
        Assert.Equal(EnvironmentStopStatus.NotConfirmed, result.Status);
        Assert.DoesNotContain("wsl.exe --shutdown", runner.Commands);
    }

    [Fact]
    public async Task Docker_lists_containers_and_stops_with_docker_desktop_stop()
    {
        var (service, runner, world, clock) = Setup();
        world.Extra.AddRange(DockerDesktop());
        runner.Results["docker.exe ps --format {{.Names}}"] = new CommandResult(0, "db\nweb\n");
        runner.OnRun = cmd =>
        {
            if (cmd == "docker.exe desktop stop")
            {
                world.Extra.Clear();
            }
        };

        var shown = await service.InspectAsync(EnvironmentKind.Docker);
        Assert.Equal(["db", "web"], shown.RunningItems);
        Assert.Contains("2", shown.Warning, StringComparison.Ordinal);
        Assert.Contains("db", shown.Warning, StringComparison.Ordinal);

        var result = await service.StopAsync(EnvironmentStopConfirmation.FromUserConfirmation(shown, clock.Now.UtcDateTime));
        Assert.Equal(EnvironmentStopStatus.Stopped, result.Status);
        Assert.Equal("docker.exe desktop stop", runner.Commands[^1]);
    }

    [Fact]
    public async Task Old_docker_desktop_without_a_stop_command_is_never_killed()
    {
        var (service, runner, world, clock) = Setup();
        world.Extra.AddRange(DockerDesktop());
        runner.Results["docker.exe desktop status"] = new CommandResult(1, "docker: 'desktop' is not a docker command.");

        var shown = await service.InspectAsync(EnvironmentKind.Docker);
        Assert.False(shown.CanStop);
        Assert.Contains("4.37", shown.CannotStopReason, StringComparison.Ordinal);

        var result = await service.StopAsync(EnvironmentStopConfirmation.FromUserConfirmation(shown, clock.Now.UtcDateTime));
        Assert.Equal(EnvironmentStopStatus.NotSupported, result.Status);
        Assert.DoesNotContain(runner.Commands, c => c.EndsWith("stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_containers_need_a_confirmation_that_was_given_for_unknown_containers()
    {
        var (service, runner, world, clock) = Setup();
        world.Extra.AddRange(DockerDesktop());
        runner.Results["docker.exe ps --format {{.Names}}"] = new CommandResult(0, "db\n");
        var known = EnvironmentStopConfirmation.FromUserConfirmation(await service.InspectAsync(EnvironmentKind.Docker), clock.Now.UtcDateTime);

        // The engine stopped answering: SnapBack can no longer verify what would stop.
        runner.Results["docker.exe ps --format {{.Names}}"] = new CommandResult(1, "error during connect");
        Assert.Equal(EnvironmentStopStatus.Changed, (await service.StopAsync(known)).Status);

        var shownUnknown = await service.InspectAsync(EnvironmentKind.Docker);
        Assert.False(shownUnknown.ItemsKnown);
        Assert.Contains("could not list", shownUnknown.Warning, StringComparison.OrdinalIgnoreCase);
        runner.OnRun = cmd =>
        {
            if (cmd == "docker.exe desktop stop")
            {
                world.Extra.Clear();
            }
        };
        Assert.Equal(EnvironmentStopStatus.Stopped,
            (await service.StopAsync(EnvironmentStopConfirmation.FromUserConfirmation(shownUnknown, clock.Now.UtcDateTime))).Status);
    }

    [Fact]
    public async Task A_failed_or_ineffective_stop_command_is_reported_and_nothing_else_is_tried()
    {
        var (service, runner, world, clock) = Setup();
        world.Extra.AddRange([VmmemWsl(), WslHost()]);
        runner.Results["wsl.exe --shutdown"] = new CommandResult(1, "Access is denied.");
        var shown = await service.InspectAsync(EnvironmentKind.Wsl);
        var failed = await service.StopAsync(EnvironmentStopConfirmation.FromUserConfirmation(shown, clock.Now.UtcDateTime));
        Assert.Equal(EnvironmentStopStatus.Failed, failed.Status);
        Assert.Contains("Access is denied", failed.Message, StringComparison.Ordinal);

        // The command "succeeds" but the VM keeps running: SnapBack reports it and does not kill vmmem.
        runner.Results.Remove("wsl.exe --shutdown");
        runner.OnRun = null;
        clock.Step = TimeSpan.FromSeconds(1);
        var still = await service.StopAsync(EnvironmentStopConfirmation.FromUserConfirmation(await service.InspectAsync(EnvironmentKind.Wsl), clock.Now.UtcDateTime));
        Assert.Equal(EnvironmentStopStatus.Failed, still.Status);
        Assert.All(runner.Commands, c => Assert.True(c.StartsWith("wsl.exe --list", StringComparison.Ordinal) || c == "wsl.exe --shutdown"));
    }

    [Fact]
    public async Task Missing_tools_mean_no_stop()
    {
        var (service, runner, world, clock) = Setup(docker: false, wsl: false);
        world.Extra.AddRange([VmmemWsl(), WslHost(), .. DockerDesktop()]);
        var wsl = await service.InspectAsync(EnvironmentKind.Wsl);
        var docker = await service.InspectAsync(EnvironmentKind.Docker);
        Assert.False(wsl.CanStop);
        Assert.False(docker.CanStop);
        Assert.Equal(EnvironmentStopStatus.NotSupported, (await service.StopAsync(EnvironmentStopConfirmation.FromUserConfirmation(wsl, clock.Now.UtcDateTime))).Status);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public void Command_output_parsing_is_robust()
    {
        Assert.Equal(["a", "b"], EnvironmentService.ParseLines("\uFEFFa\r\n\r\n  b  \n a \n"));
        Assert.Empty(EnvironmentService.ParseLines(""));
        Assert.Empty(EnvironmentService.ParseLines(new string('x', 500)));
    }
}
