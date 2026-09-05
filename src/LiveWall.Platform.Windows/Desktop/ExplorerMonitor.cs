using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

public sealed class ExplorerMonitor : IDisposable
{
    private static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan DefaultFallbackPollInterval = TimeSpan.FromSeconds(2);
    private readonly object gate = new();
    private readonly Func<ulong?> trackedAttachPoint;
    private readonly IExplorerShellProbe shellProbe;
    private readonly TimeSpan debounceInterval;
    private readonly TimeSpan fallbackPollInterval;
    private readonly Timer validationTimer;
    private readonly Timer fallbackTimer;
    private ulong lastProgmanHandle;
    private bool outageReported;
    private bool disposed;

    public ExplorerMonitor(Func<ulong?> trackedAttachPoint)
        : this(
            trackedAttachPoint,
            new NativeExplorerShellProbe(),
            DefaultDebounceInterval,
            DefaultFallbackPollInterval)
    {
    }

    internal ExplorerMonitor(
        Func<ulong?> trackedAttachPoint,
        IExplorerShellProbe shellProbe,
        TimeSpan fallbackPollInterval)
        : this(
            trackedAttachPoint,
            shellProbe,
            DefaultDebounceInterval,
            fallbackPollInterval)
    {
    }

    internal ExplorerMonitor(
        Func<ulong?> trackedAttachPoint,
        IExplorerShellProbe shellProbe,
        TimeSpan debounceInterval,
        TimeSpan fallbackPollInterval)
    {
        this.trackedAttachPoint = trackedAttachPoint ??
            throw new ArgumentNullException(nameof(trackedAttachPoint));
        this.shellProbe = shellProbe ?? throw new ArgumentNullException(nameof(shellProbe));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(debounceInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fallbackPollInterval, TimeSpan.Zero);
        this.debounceInterval = debounceInterval;
        this.fallbackPollInterval = fallbackPollInterval;
        validationTimer = new Timer(OnTimer);
        fallbackTimer = new Timer(OnTimer);
        lastProgmanHandle = shellProbe.GetProgmanHandle();
    }

    public event EventHandler<ExplorerRestartedEventArgs>? Restarted;

    public event EventHandler<ExplorerValidationFailedEventArgs>? ValidationFailed;

    public void Start()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            fallbackTimer.Change(fallbackPollInterval, fallbackPollInterval);
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            validationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            fallbackTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void RequestValidation()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            validationTimer.Change(debounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        validationTimer.Dispose();
        fallbackTimer.Dispose();
    }

    internal void CheckNow()
    {
        ExplorerRestartedEventArgs? notification = null;
        lock (gate)
        {
            ThrowIfDisposed();
            ulong currentProgmanHandle = shellProbe.GetProgmanHandle();
            ulong? attachPoint = trackedAttachPoint();
            bool progmanRecreated = lastProgmanHandle != 0 &&
                currentProgmanHandle != 0 &&
                currentProgmanHandle != lastProgmanHandle;
            bool attachPointInvalidated = attachPoint is > 0 &&
                !shellProbe.IsWindow(attachPoint.Value);
            ExplorerChangeReason? reason = progmanRecreated
                ? ExplorerChangeReason.ProgmanRecreated
                : attachPointInvalidated
                    ? ExplorerChangeReason.AttachPointInvalidated
                    : null;

            if (currentProgmanHandle != 0)
            {
                lastProgmanHandle = currentProgmanHandle;
            }

            if (reason is null)
            {
                outageReported = false;
            }
            else if (!outageReported)
            {
                outageReported = true;
                notification = new ExplorerRestartedEventArgs(
                    reason.Value,
                    attachPoint,
                    currentProgmanHandle);
            }
        }

        if (notification is not null)
        {
            Restarted?.Invoke(this, notification);
        }
    }

    private void OnTimer(object? state)
    {
        try
        {
            CheckNow();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            ValidationFailed?.Invoke(this, new ExplorerValidationFailedEventArgs(exception));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}

public enum ExplorerChangeReason
{
    ProgmanRecreated,
    AttachPointInvalidated,
}

public sealed class ExplorerRestartedEventArgs(
    ExplorerChangeReason reason,
    ulong? previousAttachPoint,
    ulong currentProgmanHandle) : EventArgs
{
    public ExplorerChangeReason Reason { get; } = reason;

    public ulong? PreviousAttachPoint { get; } = previousAttachPoint;

    public ulong CurrentProgmanHandle { get; } = currentProgmanHandle;
}

public sealed class ExplorerValidationFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception ??
        throw new ArgumentNullException(nameof(exception));
}

internal interface IExplorerShellProbe
{
    ulong GetProgmanHandle();

    bool IsWindow(ulong handle);
}

internal sealed class NativeExplorerShellProbe : IExplorerShellProbe
{
    public ulong GetProgmanHandle()
    {
        nint handle = DesktopNativeMethods.FindWindow("Progman", null);
        return unchecked((ulong)handle.ToInt64());
    }

    public bool IsWindow(ulong handle) =>
        DesktopNativeMethods.IsWindow(unchecked((nint)(long)handle));
}
