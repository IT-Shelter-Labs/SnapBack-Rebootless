using Microsoft.Extensions.Logging.Abstractions;
using SnapBack.Core.Environments;
using SnapBack.Windows.Environments;

namespace SnapBack.Windows.Tests;

/// <summary>Windows pieces behind the tray, autostart, background mode and WSL/Docker commands.</summary>
public class TrayAndStartupTests
{
    [WindowsOnlyFact]
    public void Autostart_entry_is_written_fixed_and_removed_in_the_per_user_run_key()
    {
        var original = AutostartRegistration.RegisteredCommand();
        try
        {
            var exe = @"C:\Temp\SnapBack test\SnapBack.exe";
            Assert.True(AutostartRegistration.Sync(true, exe));
            Assert.Equal($"\"{exe}\" --autostart", AutostartRegistration.RegisteredCommand());
            Assert.True(AutostartRegistration.IsEnabled(exe));
            Assert.False(AutostartRegistration.Sync(true, exe)); // already correct

            // A moved executable (or the 0.2 "--auto-baseline" entry) is corrected.
            Assert.True(AutostartRegistration.Sync(true, @"C:\Elsewhere\SnapBack.exe"));
            Assert.False(AutostartRegistration.IsEnabled(exe));

            Assert.True(AutostartRegistration.Sync(false, exe));
            Assert.Null(AutostartRegistration.RegisteredCommand());
            Assert.False(AutostartRegistration.Sync(false, exe));
        }
        finally
        {
            AutostartRegistration.Disable();
            if (original is not null)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                key.SetValue("SnapBack", original);
            }
        }
    }

    [WindowsOnlyFact]
    public void Startup_approved_state_can_be_read_without_admin_rights()
    {
        // Must not throw whatever the state is.
        _ = AutostartRegistration.IsDisabledInWindowsSettings();
    }

    [WindowsOnlyFact]
    public async Task Commands_run_without_a_shell_and_report_timeouts_without_killing()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var runner = new ProcessCommandRunner();
        var ok = await runner.RunAsync(cmd, ["/d", "/c", "echo snapback & echo %SNAPBACK_TEST_VAR%"],
            new Dictionary<string, string> { ["SNAPBACK_TEST_VAR"] = "value" }, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(ok.Succeeded);
        Assert.Contains("snapback", ok.Output, StringComparison.Ordinal);
        Assert.Contains("value", ok.Output, StringComparison.Ordinal);

        var failed = await runner.RunAsync(cmd, ["/d", "/c", "exit 3"], null, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(3, failed.ExitCode);

        var missing = await runner.RunAsync(@"C:\does\not\exist.exe", [], null, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(missing.NotFound);

        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var slow = await runner.RunAsync(ping, ["-n", "10", "127.0.0.1"], null, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(slow.TimedOut);
    }

    [WindowsOnlyFact]
    public async Task Environment_inspection_on_this_machine_never_throws_and_never_starts_anything()
    {
        using var probe = new ProbeHolder();
        var service = new EnvironmentService(new ProcessCommandRunner(), EnvironmentToolLocator.Find(), probe.Probe.CaptureLight,
            TimeProvider.System, NullLogger.Instance);
        foreach (var kind in Enum.GetValues<EnvironmentKind>())
        {
            var presence = service.Detect().Single(e => e.Kind == kind);
            var details = await service.InspectAsync(kind);
            Assert.Equal(presence.IsRunning, details.IsRunning);
        }
    }

    [WindowsOnlyFact]
    public void Light_capture_lists_processes_without_opening_them()
    {
        using var probe = new ProbeHolder();
        var snap = probe.Probe.CaptureLight();
        Assert.Contains(snap.Processes.Values, p => p.Pid == Environment.ProcessId);
        Assert.All(snap.Processes.Values, p => Assert.False(p.CouldOpen));
    }

    [WindowsOnlyFact]
    public void Background_modes_and_cpu_meter_work()
    {
        var meter = new CpuUsageMeter();
        Assert.Null(meter.Sample());
        Thread.Sleep(200);
        var busy = meter.Sample();
        Assert.NotNull(busy);
        Assert.InRange(busy!.Value, 0, 1);

        using (ResourceModes.BackgroundThread())
        {
            Thread.Sleep(10);
        }

        // Efficiency mode may be unsupported on older Windows; it must never throw.
        ResourceModes.SetEfficiencyMode(true);
        ResourceModes.SetEfficiencyMode(false);
    }

    private sealed class ProbeHolder : IDisposable
    {
        public WindowsSystemProbe Probe { get; } = new(NullLogger<WindowsSystemProbe>.Instance);

        public void Dispose()
        {
        }
    }
}
