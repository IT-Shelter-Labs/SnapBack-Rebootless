using System.Security.Principal;

namespace SnapBack.App.Services;

/// <summary>One SnapBack window per user session; a second launch activates the first.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly bool _owned;
    private RegisteredWaitHandle? _registration;

    public SingleInstance()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? "user";
        _mutex = new Mutex(initiallyOwned: true, @"Local\SnapBack.Instance." + sid, out _owned);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SnapBack.Activate." + sid);
    }

    public bool IsFirstInstance => _owned;

    public void SignalFirstInstance() => _activate.Set();

    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        if (_owned)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
