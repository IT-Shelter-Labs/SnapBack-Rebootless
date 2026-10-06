using System.Diagnostics;
using SnapBack.Core.Model;

namespace SnapBack.Windows.Tests;

/// <summary>Launches the test target app and cleans it up afterwards.</summary>
internal sealed class TestTargetProcess : IDisposable
{
    private readonly Process _process;

    private TestTargetProcess(Process process)
    {
        _process = process;
    }

    public int Pid => _process.Id;

    public bool HasExited
    {
        get
        {
            _process.Refresh();
            return _process.HasExited;
        }
    }

    public static TestTargetProcess Start(string mode)
    {
        var exe = Locate();
        var p = Process.Start(new ProcessStartInfo(exe, mode) { UseShellExecute = false })
            ?? throw new InvalidOperationException("Could not start the test target.");
        return new TestTargetProcess(p);
    }

    /// <summary>Waits until the probe sees a visible main window for the target.</summary>
    public ProcessInfo WaitForWindow(WindowsSystemProbe probe)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = probe.Capture();
            if (snapshot.Processes.TryGetValue(Pid, out var info) && info.Windows.VisibleMainWindows > 0)
            {
                return info;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException("The test target did not show its window.");
    }

    public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
    }

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SnapBack.TestTarget")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new FileNotFoundException("tests/SnapBack.TestTarget not found.");
        }

        var exe = Directory.EnumerateFiles(Path.Combine(dir.FullName, "SnapBack.TestTarget", "bin"), "SnapBack.TestTarget.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return exe ?? throw new FileNotFoundException("SnapBack.TestTarget.exe has not been built.");
    }
}
