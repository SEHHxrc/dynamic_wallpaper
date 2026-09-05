using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;

namespace LiveWall.Platform.Windows.Desktop;

public sealed class WindowsDesktopHost : IDesktopHost, IAsyncDisposable
{
    private readonly object stateGate = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly DesktopHostAdapterSelector adapterSelector;
    private readonly IDesktopSurfaceFactory surfaceFactory;
    private readonly WindowsShellSnapshot shellSnapshot;
    private readonly Dictionary<SurfaceId, TrackedSurface> surfaces = [];
    private HashSet<DisplayId> connectedDisplays = [];
    private SelectedDesktopAdapter? selectedAdapter;
    private DesktopTopology topology = new(0, []);
    private long displayTopologyRevision = -1;
    private bool disposed;

    public WindowsDesktopHost(WindowsShellSnapshot shellSnapshot)
        : this(
            shellSnapshot,
            new DesktopHostAdapterSelector([
                new RaisedDesktopAdapter(),
                new LegacyWorkerWAdapter(),
            ]),
            new NativeDesktopSurfaceFactory())
    {
    }

    internal WindowsDesktopHost(
        WindowsShellSnapshot shellSnapshot,
        DesktopHostAdapterSelector adapterSelector,
        IDesktopSurfaceFactory surfaceFactory)
    {
        this.shellSnapshot = shellSnapshot ??
            throw new ArgumentNullException(nameof(shellSnapshot));
        this.adapterSelector = adapterSelector ??
            throw new ArgumentNullException(nameof(adapterSelector));
        this.surfaceFactory = surfaceFactory ??
            throw new ArgumentNullException(nameof(surfaceFactory));
    }

    public ulong? CurrentAttachPointHandle
    {
        get
        {
            lock (stateGate)
            {
                return selectedAdapter?.AttachPoint.WindowHandle;
            }
        }
    }

    public IAsyncEnumerable<DesktopWindowSignal> ReadWindowSignalsAsync(
        CancellationToken cancellationToken) =>
        surfaceFactory.ReadSignalsAsync(cancellationToken);

    public async Task<DesktopTopology> EnsureTopologyAsync(
        DisplayTopology displays,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(displays);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            SelectedDesktopAdapter selected = await adapterSelector
                .DiscoverAsync(shellSnapshot, cancellationToken)
                .ConfigureAwait(false);
            HashSet<DisplayId> nextDisplays = displays.Displays
                .Select(display => display.Id)
                .ToHashSet();

            lock (stateGate)
            {
                bool unchanged = displayTopologyRevision == displays.Revision &&
                    selectedAdapter?.AttachPoint == selected.AttachPoint &&
                    connectedDisplays.SetEquals(nextDisplays);
                if (unchanged)
                {
                    return topology;
                }

                selectedAdapter = selected;
                connectedDisplays = nextDisplays;
                displayTopologyRevision = displays.Revision;
                topology = new DesktopTopology(
                    checked(topology.Revision + 1),
                    [selected.AttachPoint]);
                return topology;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<DesktopSurface> CreateSurfaceAsync(
        SurfaceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DisplayIds.Count == 0)
        {
            throw new ArgumentException("A Surface must target at least one display.", nameof(request));
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            SelectedDesktopAdapter selected;
            lock (stateGate)
            {
                selected = selectedAdapter ??
                    throw new InvalidOperationException("Desktop topology has not been initialized.");
                DisplayId? disconnected = request.DisplayIds
                    .Cast<DisplayId?>()
                    .FirstOrDefault(display =>
                        display is not null && !connectedDisplays.Contains(display.Value));
                if (disconnected is not null)
                {
                    throw new InvalidOperationException(
                        $"Display '{disconnected.Value}' is not connected.");
                }
            }

            SurfaceId id = new(Guid.NewGuid().ToString("N"));
            ulong handle = await surfaceFactory.CreateAsync(
                    id,
                    request,
                    selected.AttachPoint,
                    cancellationToken)
                .ConfigureAwait(false);
            DesktopSurface surface = new(id, request.DisplayIds.ToArray(), handle);
            lock (stateGate)
            {
                surfaces.Add(
                    id,
                    new TrackedSurface(surface, request, DesktopSurfaceState.Provisional));
            }

            return surface;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task ReplaceSurfacesAsync(
        SurfaceReplacement replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            TrackedSurface[] provisional;
            TrackedSurface[] replaced;
            lock (stateGate)
            {
                if (replacement.DesktopTopologyRevision != topology.Revision)
                {
                    throw new InvalidOperationException(
                        $"Desktop topology revision '{replacement.DesktopTopologyRevision}' is stale; " +
                        $"current revision is '{topology.Revision}'.");
                }

                provisional = ResolveSurfaces(
                    replacement.ProvisionalSurfaceIds,
                    DesktopSurfaceState.Provisional,
                    "provisional");
                replaced = ResolveSurfaces(
                    replacement.ReplacedSurfaceIds,
                    DesktopSurfaceState.Active,
                    "active");
                if (provisional.Select(item => item.Surface.Id)
                    .Intersect(replaced.Select(item => item.Surface.Id))
                    .Any())
                {
                    throw new ArgumentException(
                        "A Surface cannot be both provisional and replaced.",
                        nameof(replacement));
                }
            }

            await surfaceFactory.ReplaceAsync(
                    provisional.Select(item => item.Surface.WindowHandle).ToArray(),
                    replaced.Select(item => item.Surface.WindowHandle).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);

            lock (stateGate)
            {
                foreach (TrackedSurface tracked in replaced)
                {
                    surfaces[tracked.Surface.Id] = tracked with
                    {
                        State = DesktopSurfaceState.Retired,
                    };
                }

                foreach (TrackedSurface tracked in provisional)
                {
                    surfaces[tracked.Surface.Id] = tracked with
                    {
                        State = DesktopSurfaceState.Active,
                    };
                }
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task DestroySurfaceAsync(
        SurfaceId surfaceId,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            TrackedSurface? tracked;
            lock (stateGate)
            {
                surfaces.TryGetValue(surfaceId, out tracked);
            }

            if (tracked is null)
            {
                return;
            }

            await surfaceFactory.DestroyAsync(tracked.Surface.WindowHandle, cancellationToken)
                .ConfigureAwait(false);
            lock (stateGate)
            {
                surfaces.Remove(surfaceId);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<DesktopTopology> RecoverAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            SelectedDesktopAdapter? previous;
            lock (stateGate)
            {
                previous = selectedAdapter;
            }

            if (previous is not null)
            {
                await previous.Adapter.RecoverAsync(cancellationToken).ConfigureAwait(false);
            }

            SelectedDesktopAdapter selected = await adapterSelector
                .DiscoverAsync(shellSnapshot, cancellationToken)
                .ConfigureAwait(false);
            lock (stateGate)
            {
                selectedAdapter = selected;
                topology = new DesktopTopology(
                    checked(topology.Revision + 1),
                    [selected.AttachPoint]);
                return topology;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lock (stateGate)
            {
                surfaces.Clear();
            }

            await surfaceFactory.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private TrackedSurface[] ResolveSurfaces(
        IReadOnlyList<SurfaceId> ids,
        DesktopSurfaceState expectedState,
        string role)
    {
        if (ids.Distinct().Count() != ids.Count)
        {
            throw new ArgumentException($"The {role} Surface list contains duplicate IDs.");
        }

        TrackedSurface[] resolved = new TrackedSurface[ids.Count];
        for (int index = 0; index < ids.Count; index++)
        {
            if (!surfaces.TryGetValue(ids[index], out TrackedSurface? tracked) ||
                tracked.State != expectedState)
            {
                throw new InvalidOperationException(
                    $"Surface '{ids[index].Value}' is missing or is not {role}.");
            }

            resolved[index] = tracked;
        }

        return resolved;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed record TrackedSurface(
        DesktopSurface Surface,
        SurfaceRequest Request,
        DesktopSurfaceState State);

    private enum DesktopSurfaceState
    {
        Provisional,
        Active,
        Retired,
    }
}
