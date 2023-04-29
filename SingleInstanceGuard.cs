namespace TheCloser;

internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexId = "TheCloser_SingleInstanceMutex";

    private readonly Mutex _mutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static SingleInstanceGuard? Create()
    {
        if (!Mutex.TryOpenExisting(MutexId, out _))
        {
            return new SingleInstanceGuard(new Mutex(false, MutexId));
        }

        return null;
    }

    public void Dispose()
    {
        _mutex.Close();
    }
}
