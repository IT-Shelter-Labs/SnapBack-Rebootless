using Microsoft.Extensions.Logging;
using SnapBack.Core.Classification;
using SnapBack.Core.Localization;
using SnapBack.Core.Model;

namespace SnapBack.Core.Environments;

/// <summary>Developer environments that run a virtual machine and are stopped with their own commands.</summary>
public enum EnvironmentKind
{
    Wsl,
    Docker,
}

/// <summary>What the process list says about an environment. Computed without running any command.</summary>
public sealed record EnvironmentPresence(EnvironmentKind Kind, bool IsRunning, long MemoryBytes, int ProcessCount)
{
    public string Name => EnvironmentNames.Of(Kind);
}

public static class EnvironmentNames
{
    public static string Of(EnvironmentKind kind) => kind == EnvironmentKind.Wsl ? "WSL" : "Docker";
}

/// <summary>Recognizes WSL and Docker Desktop in a snapshot.</summary>
public static class EnvironmentDetector
{
    private static readonly string[] WslHelpers = ["wslservice.exe", "wslhost.exe", "wslrelay.exe"];

    public static IReadOnlyList<EnvironmentPresence> Detect(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var all = snapshot.Processes.Values.ToList();
        return [DetectWsl(all), DetectDocker(all, snapshot.Context)];
    }

    public static EnvironmentPresence For(SystemSnapshot snapshot, EnvironmentKind kind) =>
        Detect(snapshot).First(e => e.Kind == kind);

    private static EnvironmentPresence DetectWsl(List<ProcessInfo> all)
    {
        // Windows 11 names the WSL 2 virtual machine "vmmemWSL"; older builds use "vmmem", which
        // other Hyper-V VMs use too, so it only counts together with a WSL helper process.
        var vm = all.Where(p => Is(p, "vmmemWSL")).ToList();
        var helpers = all.Where(p => WslHelpers.Any(h => Is(p, h))).ToList();
        if (vm.Count == 0 && helpers.Count > 0)
        {
            vm = all.Where(p => Is(p, "vmmem")).ToList();
        }

        var running = vm.Count > 0;
        return new EnvironmentPresence(EnvironmentKind.Wsl, running, vm.Sum(p => p.WorkingSet), vm.Count + helpers.Count);
    }

    private static EnvironmentPresence DetectDocker(List<ProcessInfo> all, SessionContext ctx)
    {
        var docker = all
            .Where(p => p.SessionId == ctx.SessionId && KnownProcesses.IsManagedEnvironment(p.ImageName) && IsDocker(p.ImageName))
            .ToList();
        var running = docker.Any(p => Is(p, "com.docker.backend.exe") || Is(p, "Docker Desktop.exe"));
        return new EnvironmentPresence(EnvironmentKind.Docker, running, docker.Sum(p => p.PrivateWorkingSet), docker.Count);
    }

    public static bool IsDocker(string imageName) =>
        imageName.StartsWith("com.docker.", StringComparison.OrdinalIgnoreCase)
        || imageName.Contains("docker", StringComparison.OrdinalIgnoreCase)
        || imageName.StartsWith("vpnkit", StringComparison.OrdinalIgnoreCase);

    private static bool Is(ProcessInfo p, string name) => string.Equals(p.ImageName, name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Live details, obtained with the environment's own read-only commands.</summary>
public sealed record EnvironmentDetails
{
    public required EnvironmentKind Kind { get; init; }

    public bool IsRunning { get; init; }

    public long MemoryBytes { get; init; }

    /// <summary>Running WSL distributions, or running Docker containers.</summary>
    public IReadOnlyList<string> RunningItems { get; init; } = [];

    /// <summary>False when the running items could not be listed (the user is warned about that).</summary>
    public bool ItemsKnown { get; init; } = true;

    public bool CanStop { get; init; }

    public string? CannotStopReason { get; init; }

    /// <summary>WSL: Docker Desktop runs inside WSL and would lose its engine.</summary>
    public bool DockerDependsOnIt { get; init; }

    public string Name => EnvironmentNames.Of(Kind);

    /// <summary>The warning shown before stopping (current UI language).</summary>
    public string Warning
    {
        get
        {
            var lines = new List<string>();
            if (Kind == EnvironmentKind.Wsl)
            {
                lines.Add(RunningItems.Count > 0
                    ? Loc.Format("Env_Wsl_Warning_Distros", string.Join(", ", RunningItems))
                    : Loc.Get("Env_Wsl_Warning"));
                if (DockerDependsOnIt)
                {
                    lines.Add(Loc.Get("Env_Wsl_Warning_Docker"));
                }
            }
            else
            {
                lines.Add(!ItemsKnown
                    ? Loc.Get("Env_Docker_Warning_Unknown")
                    : RunningItems.Count > 0
                        ? Loc.Format("Env_Docker_Warning_Containers", RunningItems.Count, string.Join(", ", RunningItems.Take(10)))
                        : Loc.Get("Env_Docker_Warning_None"));
            }

            lines.Add(Loc.Get("Env_Warning_Unsaved"));
            return string.Join("\n", lines);
        }
    }

    public static EnvironmentDetails NotRunning(EnvironmentKind kind) => new() { Kind = kind, IsRunning = false };
}

/// <summary>
/// The user's confirmation to stop an environment with exactly the running items they were shown.
/// It is only created from a confirmation dialog and expires after five minutes.
/// </summary>
public sealed record EnvironmentStopConfirmation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private EnvironmentStopConfirmation(EnvironmentKind kind, IReadOnlySet<string> items, bool itemsKnown, DateTime confirmedAtUtc)
    {
        Kind = kind;
        ConfirmedItems = items;
        ItemsWereKnown = itemsKnown;
        ConfirmedAtUtc = confirmedAtUtc;
    }

    public EnvironmentKind Kind { get; }

    public IReadOnlySet<string> ConfirmedItems { get; }

    public bool ItemsWereKnown { get; }

    public DateTime ConfirmedAtUtc { get; }

    public static EnvironmentStopConfirmation FromUserConfirmation(EnvironmentDetails shown, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(shown);
        return new EnvironmentStopConfirmation(shown.Kind, shown.RunningItems.ToHashSet(StringComparer.OrdinalIgnoreCase), shown.ItemsKnown, nowUtc);
    }

    /// <summary>
    /// The order to stop environments in: Docker before WSL, because Docker Desktop runs inside WSL
    /// and would otherwise lose its engine before it could shut down cleanly.
    /// </summary>
    public static IReadOnlyList<EnvironmentStopConfirmation> InStopOrder(IEnumerable<EnvironmentStopConfirmation> confirmations) =>
        confirmations.OrderBy(c => c.Kind == EnvironmentKind.Wsl ? 1 : 0).ToList();

    public bool IsExpired(DateTime nowUtc) => nowUtc - ConfirmedAtUtc > Lifetime || nowUtc < ConfirmedAtUtc - TimeSpan.FromMinutes(1);

    /// <summary>Whether the live state is still what the user confirmed (nothing new would be stopped).</summary>
    public bool Covers(EnvironmentDetails live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Kind != Kind)
        {
            return false;
        }

        if (!live.ItemsKnown)
        {
            return !ItemsWereKnown;
        }

        return live.RunningItems.All(ConfirmedItems.Contains);
    }
}

public enum EnvironmentStopStatus
{
    Stopped,
    NotRunning,

    /// <summary>Something new started after the confirmation; nothing was done.</summary>
    Changed,

    /// <summary>The confirmation expired or does not match; nothing was done.</summary>
    NotConfirmed,

    NotSupported,
    Failed,
}

public sealed record EnvironmentStopResult(EnvironmentKind Kind, EnvironmentStopStatus Status, string Message)
{
    public bool Succeeded => Status is EnvironmentStopStatus.Stopped or EnvironmentStopStatus.NotRunning;
}

/// <summary>Result of an external command.</summary>
public sealed record CommandResult(int ExitCode, string Output, bool TimedOut = false, bool NotFound = false)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !NotFound;
}

/// <summary>Runs a fixed executable with fixed arguments (no shell) and captures its output.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Where the environments' own command-line tools are; <c>null</c> when not installed.</summary>
public sealed record EnvironmentTools(string? WslExecutable, string? DockerExecutable);

/// <summary>
/// Inspects and stops WSL and Docker Desktop with their own, documented commands. It never touches
/// their processes directly.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Inspecting runs read-only commands (<c>wsl --list --running</c>, <c>docker ps</c>, <c>docker
/// desktop status</c>) and only when the process list shows the environment running, so it never
/// starts WSL or Docker.</item>
/// <item>Stopping requires an <see cref="EnvironmentStopConfirmation"/>. Right before stopping, the
/// environment is inspected again; if something started that the user was not shown, nothing is
/// done and the user is asked again.</item>
/// <item>WSL is stopped with <c>wsl --shutdown</c>; Docker Desktop with <c>docker desktop stop</c>
/// (Docker Desktop 4.37 or newer). Older Docker Desktop versions are reported as not supported.</item>
/// </list>
/// </remarks>
public sealed class EnvironmentService
{
    public static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan WslStopTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan DockerStopTimeout = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlyDictionary<string, string> WslEnvironment = new Dictionary<string, string> { ["WSL_UTF8"] = "1" };

    private readonly ICommandRunner _runner;
    private readonly EnvironmentTools _tools;
    private readonly Func<SystemSnapshot> _capture;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public EnvironmentService(ICommandRunner runner, EnvironmentTools tools, Func<SystemSnapshot> capture, TimeProvider time, ILogger log)
    {
        _runner = runner;
        _tools = tools;
        _capture = capture;
        _time = time;
        _log = log;
    }

    /// <summary>How long to wait between checks that the environment has exited (tests use zero).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public IReadOnlyList<EnvironmentPresence> Detect() => EnvironmentDetector.Detect(_capture());

    public async Task<EnvironmentDetails> InspectAsync(EnvironmentKind kind, CancellationToken cancellationToken = default)
    {
        var all = EnvironmentDetector.Detect(_capture());
        var presence = all.First(e => e.Kind == kind);
        if (!presence.IsRunning)
        {
            return EnvironmentDetails.NotRunning(kind);
        }

        return kind == EnvironmentKind.Wsl
            ? await InspectWslAsync(presence, all.First(e => e.Kind == EnvironmentKind.Docker).IsRunning, cancellationToken).ConfigureAwait(false)
            : await InspectDockerAsync(presence, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnvironmentDetails> InspectWslAsync(EnvironmentPresence presence, bool dockerRunning, CancellationToken ct)
    {
        if (_tools.WslExecutable is not { } wsl)
        {
            return new EnvironmentDetails
            {
                Kind = EnvironmentKind.Wsl, IsRunning = true, MemoryBytes = presence.MemoryBytes, ItemsKnown = false,
                CanStop = false, CannotStopReason = Loc.Get("Env_Wsl_NotInstalled"),
            };
        }

        var list = await Run(wsl, ["--list", "--running", "--quiet"], WslEnvironment, InspectTimeout, ct).ConfigureAwait(false);
        var distros = list.Succeeded ? ParseLines(list.Output) : [];
        return new EnvironmentDetails
        {
            Kind = EnvironmentKind.Wsl,
            IsRunning = true,
            MemoryBytes = presence.MemoryBytes,
            RunningItems = distros,
            ItemsKnown = list.Succeeded,
            CanStop = true,
            DockerDependsOnIt = dockerRunning || distros.Any(d => d.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private async Task<EnvironmentDetails> InspectDockerAsync(EnvironmentPresence presence, CancellationToken ct)
    {
        if (_tools.DockerExecutable is not { } docker)
        {
            return new EnvironmentDetails
            {
                Kind = EnvironmentKind.Docker, IsRunning = true, MemoryBytes = presence.MemoryBytes, ItemsKnown = false,
                CanStop = false, CannotStopReason = Loc.Get("Env_Docker_CliMissing"),
            };
        }

        var status = await Run(docker, ["desktop", "status"], null, InspectTimeout, ct).ConfigureAwait(false);
        var containers = await Run(docker, ["ps", "--format", "{{.Names}}"], null, InspectTimeout, ct).ConfigureAwait(false);
        var supported = status.Succeeded;
        return new EnvironmentDetails
        {
            Kind = EnvironmentKind.Docker,
            IsRunning = true,
            MemoryBytes = presence.MemoryBytes,
            RunningItems = containers.Succeeded ? ParseLines(containers.Output) : [],
            ItemsKnown = containers.Succeeded,
            CanStop = supported,
            CannotStopReason = supported ? null : Loc.Get("Env_Docker_StopUnsupported"),
        };
    }

    /// <summary>Stops the environment the user confirmed, after checking that nothing new is running.</summary>
    public async Task<EnvironmentStopResult> StopAsync(EnvironmentStopConfirmation confirmation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var kind = confirmation.Kind;
        if (confirmation.IsExpired(_time.GetUtcNow().UtcDateTime))
        {
            return new(kind, EnvironmentStopStatus.NotConfirmed, Loc.Get("Env_Result_Expired"));
        }

        var live = await InspectAsync(kind, cancellationToken).ConfigureAwait(false);
        if (!live.IsRunning)
        {
            return new(kind, EnvironmentStopStatus.NotRunning, Loc.Format("Env_Result_NotRunning", live.Name));
        }

        if (!live.CanStop)
        {
            return new(kind, EnvironmentStopStatus.NotSupported, live.CannotStopReason ?? Loc.Get("Env_Docker_StopUnsupported"));
        }

        if (!confirmation.Covers(live))
        {
            _log.LogWarning("{Environment}: running items changed since the confirmation; not stopping", live.Name);
            return new(kind, EnvironmentStopStatus.Changed, Loc.Format("Env_Result_Changed", live.Name));
        }

        _log.LogInformation("Stopping {Environment} with its own command ({Count} running item(s))", live.Name, live.RunningItems.Count);
        var result = kind == EnvironmentKind.Wsl
            ? await Run(_tools.WslExecutable!, ["--shutdown"], WslEnvironment, WslStopTimeout, cancellationToken).ConfigureAwait(false)
            : await Run(_tools.DockerExecutable!, ["desktop", "stop"], null, DockerStopTimeout, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            _log.LogWarning("{Environment} stop command failed: exit {Code}, timed out {TimedOut}", live.Name, result.ExitCode, result.TimedOut);
            return new(kind, EnvironmentStopStatus.Failed, result.TimedOut
                ? Loc.Format("Env_Result_TimedOut", live.Name)
                : Loc.Format("Env_Result_Failed", live.Name, FirstLine(result.Output)));
        }

        var deadline = _time.GetUtcNow() + ExitWait;
        while (EnvironmentDetector.For(_capture(), kind).IsRunning)
        {
            if (_time.GetUtcNow() >= deadline)
            {
                return new(kind, EnvironmentStopStatus.Failed, Loc.Format("Env_Result_StillRunning", live.Name));
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        _log.LogInformation("{Environment} stopped", live.Name);
        return new(kind, EnvironmentStopStatus.Stopped, Loc.Format("Env_Result_Stopped", live.Name));
    }

    private async Task<CommandResult> Run(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            return await _runner.RunAsync(exe, args, env, timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not run {Executable}", Path.GetFileName(exe));
            return new CommandResult(-1, "", NotFound: true);
        }
    }

    /// <summary>Splits command output into trimmed, non-empty lines (tolerates UTF-16 output and BOMs).</summary>
    public static IReadOnlyList<string> ParseLines(string output) =>
        (output ?? "")
            .Replace("\0", "", StringComparison.Ordinal)
            .Replace("﻿", "", StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0 && l.Length <= 200)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FirstLine(string output) => ParseLines(output).FirstOrDefault() ?? "";
}
