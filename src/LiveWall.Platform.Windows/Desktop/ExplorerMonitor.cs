using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

internal interface IExplorerChangeMonitor : IDisposable
{
    event EventHandler<ExplorerRestartedEventArgs>? Restarted;

    event EventHandler<ExplorerValidationFailedEventArgs>? ValidationFailed;

    event EventHandler<ExplorerValidationCompletedEventArgs>? ValidationCompleted;

    void Start();

    void Stop();

    void RequestValidation(long systemRevision = 0);
}

public sealed class ExplorerMonitor : IExplorerChangeMonitor
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
    private long requestedSystemRevision;

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

    public event EventHandler<ExplorerValidationCompletedEventArgs>? ValidationCompleted;

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

    public void RequestValidation(long systemRevision = 0)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (systemRevision > 0)
            {
                requestedSystemRevision = systemRevision;
            }
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

    internal void CheckNow(Action<long>? validationStarted = null)
    {
        ExplorerRestartedEventArgs? notification = null;
        ulong currentProgmanHandle;
        long validatedSystemRevision;
        lock (gate)
        {
            ThrowIfDisposed();
            validatedSystemRevision = requestedSystemRevision;
            validationStarted?.Invoke(validatedSystemRevision);
            currentProgmanHandle = shellProbe.GetProgmanHandle();
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
        ValidationCompleted?.Invoke(
            this,
            new ExplorerValidationCompletedEventArgs(
                currentProgmanHandle != 0,
                notification is not null,
                currentProgmanHandle,
                validatedSystemRevision));
    }

    private void OnTimer(object? state)
    {
        long validatedSystemRevision = 0;
        try
        {
            CheckNow(revision => validatedSystemRevision = revision);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            ValidationFailed?.Invoke(this, new ExplorerValidationFailedEventArgs(exception, validatedSystemRevision));
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

public sealed class ExplorerValidationFailedEventArgs(Exception exception, long systemRevision = 0) : EventArgs
{
    public long SystemRevision { get; } = systemRevision;
    public Exception Exception { get; } = exception ??
        throw new ArgumentNullException(nameof(exception));
}

public sealed class ExplorerValidationCompletedEventArgs(
    bool shellAvailable,
    bool shellChanged,
    ulong currentProgmanHandle,
    long systemRevision = 0) : EventArgs
{
    public long SystemRevision { get; } = systemRevision;
    public bool ShellAvailable { get; } = shellAvailable;

    public bool ShellChanged { get; } = shellChanged;

    public ulong CurrentProgmanHandle { get; } = currentProgmanHandle;
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
        ShellExecutionContext context = new WindowsShellNativeProbe().CaptureExecutionContext();
        if (!string.Equals(context.WindowStationName, "WinSta0", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(context.DesktopName, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        nint handle = DesktopNativeMethods.FindWindow("Progman", null);
        if (handle == 0 || handle != DesktopNativeMethods.GetShellWindow())
        {
            return 0;
        }
        _ = DesktopNativeMethods.GetWindowThreadProcessId(handle, out uint processId);
        if (!DesktopNativeMethods.ProcessIdToSessionId(processId, out uint shellSession) ||
            context.SessionId != shellSession)
        {
            return 0;
        }
        return unchecked((ulong)handle.ToInt64());
    }

    public bool IsWindow(ulong handle) =>
        DesktopNativeMethods.IsWindow(unchecked((nint)(long)handle));
}
