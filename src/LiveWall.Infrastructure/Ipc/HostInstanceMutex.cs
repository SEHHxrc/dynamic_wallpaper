using LiveWall.Contracts.Ipc;

namespace LiveWall.Infrastructure.Ipc;

public sealed class HostInstanceMutex : IDisposable
{
    private readonly Mutex mutex;
    private readonly int ownerThreadId;
    private bool disposed;

    private HostInstanceMutex(Mutex mutex, string name)
    {
        this.mutex = mutex;
        Name = name;
        ownerThreadId = Environment.CurrentManagedThreadId;
    }

    public string Name { get; }

    public static HostInstanceMutex? TryAcquire(string installationScope)
    {
        IpcEndpointNames.ValidateInstallationScope(installationScope);
        string name = $@"Local\LiveWall.{installationScope}.Host";
        NamedWaitHandleOptions options = new()
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = true,
        };
        Mutex candidate = new(
            initiallyOwned: true,
            name,
            options,
            out bool createdNew);
        if (!createdNew)
        {
            candidate.Dispose();
            return null;
        }

        return new HostInstanceMutex(candidate, name);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId != ownerThreadId)
        {
            throw new InvalidOperationException("The host instance mutex must be released by its owning thread.");
        }

        mutex.ReleaseMutex();
        mutex.Dispose();
        disposed = true;
    }
}
