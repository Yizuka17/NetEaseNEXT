using System.Security.Principal;

namespace NetEaseNEXT.Services;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _wait;
    public bool IsPrimary { get; }
    public SingleInstance()
    {
        var key = @"Local\NetEaseNEXT." + WindowsIdentity.GetCurrent().User!.Value;
        _activate = new(false, EventResetMode.AutoReset, key + ".Activate");
        _mutex = new(true, key, out var primary);
        IsPrimary = primary;
        if (!primary) _activate.Set();
    }
    public void Listen(Action activate) => _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => activate(), null, Timeout.Infinite, false);
    public void Dispose()
    {
        _wait?.Unregister(null); _activate.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
