using System.Diagnostics;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;

namespace LiveWall.Platform.Windows.Displays;

public sealed class WindowsDisplayTopologySource : IDisplayTopologySource, IDisposable
{
    private static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan[] RefreshFailureRetryDelays =
    [
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];
    private readonly object gate = new();
    private readonly object refreshGate = new();
    private readonly IDisplaySnapshotProvider snapshotProvider;
    private readonly TimeSpan debounceInterval;
    private readonly Timer refreshTimer;
    private DisplayTopology current = new(0, []);
    private int refreshFailureAttempt;
    private int immediateRefreshQueued;
    private bool emptyCandidatePending;
    private long emptyCandidateStartedTimestamp;
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
        DisplayTopology? published = null;
        DisplayTopology result;
        lock (refreshGate)
        {
            DisplayDescriptor[] displays;
            try
            {
                displays = snapshotProvider.Read()
                    .Select(CreateDescriptor)
                    .OrderByDescending(display => display.IsPrimary)
                    .ThenBy(display => display.Bounds.Y)
                    .ThenBy(display => display.Bounds.X)
                    .ThenBy(display => display.Id.Value, StringComparer.Ordinal)
                    .ToArray();
                EnsureUniqueIds(displays);
            }
            catch
            {
                lock (gate)
                {
                    CancelEmptyCandidateUnsafe();
                }

                throw;
            }

            lock (gate)
            {
                DisplayTopology previous = current;
                refreshFailureAttempt = 0;

                // The first successful sample establishes the startup state, including the
                // distinct case where the process starts without an attached display.
                if (previous.Revision == 0)
                {
                    result = new DisplayTopology(1, displays);
                    current = result;
                    published = previous;
                }
                else if (previous.Displays.Count > 0 && displays.Length == 0)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (!emptyCandidatePending)
                    {
                        emptyCandidatePending = true;
                        emptyCandidateStartedTimestamp = now;
                        ScheduleEmptyConfirmationUnsafe(debounceInterval);
                        return previous;
                    }

                    TimeSpan elapsed = Stopwatch.GetElapsedTime(emptyCandidateStartedTimestamp, now);
                    if (elapsed < debounceInterval)
                    {
                        ScheduleEmptyConfirmationUnsafe(debounceInterval - elapsed);
                        return previous;
                    }

                    CancelEmptyCandidateUnsafe();
                    result = new DisplayTopology(checked(previous.Revision + 1), displays);
                    current = result;
                    published = previous;
                }
                else
                {
                    if (displays.Length > 0)
                    {
                        CancelEmptyCandidateUnsafe();
                    }

                    if (DisplaysEqual(previous.Displays, displays))
                    {
                        return previous;
                    }

                    result = new DisplayTopology(checked(previous.Revision + 1), displays);
                    current = result;
                    published = previous;
                }
            }
        }

        Changed?.Invoke(this, new DisplayTopologyChangedEventArgs(published!, result));
        return result;
    }

    public void RequestRefresh()
    {
        ThrowIfDisposed();
        lock (gate)
        {
            refreshFailureAttempt = 0;
        }
        refreshTimer.Change(debounceInterval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Requests a best-effort immediate snapshot for visual continuity and retains the normal
    /// debounced refresh as the authoritative stability check.
    /// </summary>
    public void RequestTransitionRefresh()
    {
        RequestRefresh();
        if (Interlocked.Exchange(ref immediateRefreshQueued, 1) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(static state =>
        {
            WindowsDisplayTopologySource source = (WindowsDisplayTopologySource)state!;
            try
            {
                source.Refresh();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                source.RefreshFailed?.Invoke(
                    source,
                    new DisplayTopologyRefreshFailedEventArgs(exception));
            }
            finally
            {
                Interlocked.Exchange(ref source.immediateRefreshQueued, 0);
            }
        }, this);
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
            CancelEmptyCandidateUnsafe();
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
            lock (gate)
            {
                if (!disposed && refreshFailureAttempt < RefreshFailureRetryDelays.Length)
                {
                    TimeSpan retryDelay = RefreshFailureRetryDelays[refreshFailureAttempt++];
                    refreshTimer.Change(retryDelay, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    private void ScheduleEmptyConfirmationUnsafe(TimeSpan delay)
    {
        if (!disposed)
        {
            refreshTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelEmptyCandidateUnsafe()
    {
        emptyCandidatePending = false;
        emptyCandidateStartedTimestamp = 0;
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
