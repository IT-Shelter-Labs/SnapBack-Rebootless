using SnapBack.Windows.Native;

namespace SnapBack.Windows;

/// <summary>Ways for SnapBack to stay out of the way while it runs in the background.</summary>
public static unsafe class ResourceModes
{
    /// <summary>
    /// Opts the process into (or out of) EcoQoS: while SnapBack only sits in the notification
    /// area, Windows may run it on efficient cores at low clock speed. Ignored on systems without
    /// power throttling support.
    /// </summary>
    public static bool SetEfficiencyMode(bool enabled)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = Kernel32Power.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = Kernel32Power.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = enabled ? Kernel32Power.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };
        return Kernel32Power.SetProcessInformation(Kernel32Power.GetCurrentProcess(), Kernel32Power.ProcessPowerThrottling,
            &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));
    }

    /// <summary>
    /// Lowers the CPU, I/O and memory priority of the current thread until disposed (background
    /// analysis must not compete with the user's applications).
    /// </summary>
    public static IDisposable BackgroundThread()
    {
        var entered = Kernel32Power.SetThreadPriority(Kernel32Power.GetCurrentThread(), Kernel32Power.THREAD_MODE_BACKGROUND_BEGIN);
        return new Restore(entered);
    }

    private sealed class Restore(bool entered) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (entered && !_done)
            {
                _done = true;
                Kernel32Power.SetThreadPriority(Kernel32Power.GetCurrentThread(), Kernel32Power.THREAD_MODE_BACKGROUND_END);
            }
        }
    }
}

/// <summary>System-wide CPU usage between two calls (GetSystemTimes).</summary>
public sealed class CpuUsageMeter
{
    private long _idle;
    private long _total;

    /// <summary>Fraction of CPU time spent busy since the previous call (0–1), or <c>null</c> on the first call.</summary>
    public double? Sample()
    {
        if (!Kernel32Power.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        // Kernel time includes idle time.
        var total = kernel + user;
        var previousTotal = _total;
        var previousIdle = _idle;
        _total = total;
        _idle = idle;
        if (previousTotal == 0 || total <= previousTotal)
        {
            return null;
        }

        var busy = 1.0 - (double)(idle - previousIdle) / (total - previousTotal);
        return Math.Clamp(busy, 0, 1);
    }
}
