namespace com.lifepixer.mangapixer.Tray.Startup;

/// <summary>
/// Named-mutex single-instance guard. A second launch sees
/// <see cref="IsFirstInstance"/> false and should notify + exit rather than
/// starting a second copy of the server.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;

    public bool IsFirstInstance { get; }

    public SingleInstanceGuard(string name)
    {
        _mutex = new Mutex(initiallyOwned: true, name: $@"Global\{name}", out var createdNew);
        IsFirstInstance = createdNew;
    }

    public void Dispose()
    {
        if (IsFirstInstance)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
