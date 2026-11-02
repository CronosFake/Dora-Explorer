namespace DoraExplorer.App.Infrastructure;

public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "Global\\DoraExplorer.App.SingleInstance";

    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static bool TryAcquire(out SingleInstanceGuard? guard)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex, ownsMutex: true);
        return true;
    }

    public void Dispose()
    {
        if (!_ownsMutex)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex já libertado.
        }

        _mutex.Dispose();
    }
}
