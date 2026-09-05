using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;

namespace LiveWall.Platform.Windows.Displays;

public sealed class WindowsDisplayTopologySource : IDisplayTopologySource, IDisposable
{
    private static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(350);
    private readonly object gate = new();
    private readonly IDisplaySnapshotProvider snapshotProvider;
    private readonly TimeSpan debounceInterval;
    private readonly Timer refreshTimer;
    private DisplayTopology current = new(0, []);
    private bool disposed;

    public WindowsDisplayTopologySource()
        : this(new NativeDisplaySnapshotProvider(), DefaultDebounceInterval, initialize: true)
    {
    }

    internal WindowsDisplayTopologySource(
        IDisplaySnapshotProvider snapshotProvider,
        TimeSpan debounceInterval,
        bool initialize)
    {
        this.snapshotProvider = snapshotProvider ??
            throw new ArgumentNullException(nameof(snapshotProvider));
        ArgumentOutOfRangeException.ThrowIfLessThan(debounceInterval, TimeSpan.Zero);

        this.debounceInterval = debounceInterval;
        refreshTimer = new Timer(OnRefreshTimer);
        if (initialize)
        {
            Refresh();
        }
    }

    public DisplayTopology Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public event EventHandler<DisplayTopologyChangedEventArgs>? Changed;

    public event EventHandler<DisplayTopologyRefreshFailedEventArgs>? RefreshFailed;

    public DisplayTopology Refresh()
    {
        ThrowIfDisposed();
        DisplayDescriptor[] displays = snapshotProvider.Read()
            .Select(CreateDescriptor)
            .OrderByDescending(display => display.IsPrimary)
            .ThenBy(display => display.Bounds.Y)
            .ThenBy(display => display.Bounds.X)
            .ThenBy(display => display.Id.Value, StringComparer.Ordinal)
            .ToArray();
        EnsureUniqueIds(displays);

        DisplayTopology previous;
        DisplayTopology next;
        lock (gate)
        {
            previous = current;
            if (DisplaysEqual(previous.Displays, displays))
            {
                return previous;
            }

            next = new DisplayTopology(checked(previous.Revision + 1), displays);
            current = next;
        }

        Changed?.Invoke(this, new DisplayTopologyChangedEventArgs(previous, next));
        return next;
    }

    public void RequestRefresh()
    {
        ThrowIfDisposed();
        refreshTimer.Change(debounceInterval, Timeout.InfiniteTimeSpan);
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

        refreshTimer.Dispose();
    }

    private static bool DisplaysEqual(
        IReadOnlyList<DisplayDescriptor> left,
        DisplayDescriptor[] right) =>
        left.Count == right.Length && left.SequenceEqual(right);

    private static DisplayDescriptor CreateDescriptor(DisplaySnapshot snapshot)
    {
        if (!double.IsFinite(snapshot.ScaleFactor) || snapshot.ScaleFactor <= 0)
        {
            throw new InvalidOperationException(
                $"Display '{snapshot.DevicePath}' reported an invalid scale factor.");
        }

        if (snapshot.RefreshRateHz <= 0)
        {
            throw new InvalidOperationException(
                $"Display '{snapshot.DevicePath}' reported an invalid refresh rate.");
        }

        return new DisplayDescriptor(
            DisplayIdentity.FromDevicePath(snapshot.DevicePath),
            snapshot.DevicePath,
            snapshot.Bounds,
            snapshot.ScaleFactor,
            snapshot.RefreshRateHz,
            snapshot.IsPrimary);
    }

    private static void EnsureUniqueIds(IEnumerable<DisplayDescriptor> displays)
    {
        HashSet<DisplayId> ids = [];
        foreach (DisplayDescriptor display in displays)
        {
            if (!ids.Add(display.Id))
            {
                throw new InvalidOperationException(
                    $"Display device path produced duplicate identity '{display.Id}'.");
            }
        }
    }

    private void OnRefreshTimer(object? state)
    {
        try
        {
            Refresh();
        }
        catch (Exception exception)
        {
            RefreshFailed?.Invoke(this, new DisplayTopologyRefreshFailedEventArgs(exception));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}

public sealed class DisplayTopologyRefreshFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception ??
        throw new ArgumentNullException(nameof(exception));
}
