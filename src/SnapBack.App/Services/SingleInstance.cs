using System.Security.Principal;

namespace SnapBack.App.Services;

/// <summary>
/// One SnapBack per user session. A second launch asks the first to show its window
/// (<c>SnapBack.exe</c>) or to exit (<c>SnapBack.exe --quit</c>) through named events, which cost
/// nothing while waiting.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly EventWaitHandle _exit;
    private readonly bool _owned;
    private readonly List<RegisteredWaitHandle> _registrations = [];

    public SingleInstance()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? "user";
        _mutex = new Mutex(initiallyOwned: true, @"Local\SnapBack.Instance." + sid, out _owned);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SnapBack.Activate." + sid);
        _exit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SnapBack.Exit." + sid);
    }

    public bool IsFirstInstance => _owned;

    public void SignalFirstInstance() => _activate.Set();

    public void SignalExit() => _exit.Set();

    public void ListenForActivation(Action onActivate) => Listen(_activate, onActivate);

    public void ListenForExit(Action onExit) => Listen(_exit, onExit);

    private void Listen(WaitHandle handle, Action action) =>
        _registrations.Add(ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => action(), null, Timeout.Infinite, executeOnlyOnce: false));

    public void Dispose()
    {
        foreach (var r in _registrations)
        {
            r.Unregister(null);
        }

        _activate.Dispose();
        _exit.Dispose();
        if (_owned)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from a different thread than the one that created it; harmless at exit.
            }
        }

        _mutex.Dispose();
    }
}
