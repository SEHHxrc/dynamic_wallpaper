using LiveWall.Application.Layouts;
using LiveWall.Application.Library;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.CommandLoop;

namespace LiveWall.Host.Orchestration;

internal interface ISessionCoordinator
{
    Task<PreparedApply> PrepareApplyAsync(
        IReadOnlyList<WallpaperAssignment> assignments,
        DisplayTopology topology,
        long generation,
        CancellationToken cancellationToken);

    Task RetireAsync(
        IReadOnlyList<PreparedSession> sessions,
        CancellationToken cancellationToken);

    Task ReplaceSurfacesAsync(
        PreparedApply prepared,
        IReadOnlyList<PreparedSession> replacedSessions,
        CancellationToken cancellationToken);

    Task<PreparedRecovery> PrepareRecoveryAsync(
        IReadOnlyList<PreparedSession> activeSessions,
        DisplayTopology topology,
        long hostGeneration,
        CancellationToken cancellationToken);

    Task ReplaceRecoveredSurfacesAsync(
        PreparedRecovery recovery,
        CancellationToken cancellationToken);

    Task DestroySurfacesAsync(
        IEnumerable<PreparedSurface> surfaces,
        CancellationToken cancellationToken);
}

internal sealed class SessionCoordinator : ISessionCoordinator
{
    private readonly IWallpaperRepository wallpaperRepository;
    private readonly ILayoutPlanner layoutPlanner;
    private readonly RendererProviderSelector rendererProviderSelector;
    private readonly IDesktopHost desktopHost;
    private readonly RuntimeEnvironment runtimeEnvironment;

    public SessionCoordinator(
        IWallpaperRepository wallpaperRepository,
        ILayoutPlanner layoutPlanner,
        RendererProviderSelector rendererProviderSelector,
        IDesktopHost desktopHost,
        RuntimeEnvironment runtimeEnvironment)
    {
        this.wallpaperRepository = wallpaperRepository ??
            throw new ArgumentNullException(nameof(wallpaperRepository));
        this.layoutPlanner = layoutPlanner ?? throw new ArgumentNullException(nameof(layoutPlanner));
        this.rendererProviderSelector = rendererProviderSelector ??
            throw new ArgumentNullException(nameof(rendererProviderSelector));
        this.desktopHost = desktopHost ?? throw new ArgumentNullException(nameof(desktopHost));
        this.runtimeEnvironment = runtimeEnvironment ??
            throw new ArgumentNullException(nameof(runtimeEnvironment));
    }

    public async Task<PreparedApply> PrepareApplyAsync(
        IReadOnlyList<WallpaperAssignment> assignments,
        DisplayTopology topology,
        long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        Dictionary<WallpaperId, WallpaperLibraryEntry> libraryEntries = [];
        foreach (WallpaperId wallpaperId in assignments
                     .Select(assignment => assignment.WallpaperId)
                     .Distinct())
        {
            WallpaperLibraryEntry libraryEntry =
                await wallpaperRepository.FindAsync(wallpaperId, cancellationToken)
                    .ConfigureAwait(false) ??
                throw new InvalidOperationException($"Wallpaper '{wallpaperId}' was not found.");
            libraryEntries.Add(wallpaperId, libraryEntry);
        }

        DesktopTopology desktopTopology = await desktopHost
            .EnsureTopologyAsync(topology, cancellationToken)
            .ConfigureAwait(false);
        LayoutPlan plan = layoutPlanner.CreatePlan(topology, assignments);
        Dictionary<DisplayId, DisplayDescriptor> displays = topology.Displays
            .ToDictionary(display => display.Id);
        List<PreparedSession> preparedSessions = [];
        DesktopSurface? pendingSurface = null;
        SurfaceRequest? pendingRequest = null;
        IRendererSession? pendingRenderer = null;

        try
        {
            foreach (PlannedSurface plannedSurface in plan.Surfaces)
            {
                WallpaperLibraryEntry libraryEntry = libraryEntries[plannedSurface.WallpaperId];
                IRendererProvider provider = rendererProviderSelector.Select(
                    libraryEntry.Definition,
                    runtimeEnvironment);
                pendingRequest = new SurfaceRequest(
                    plannedSurface.DisplayIds,
                    plannedSurface.Bounds,
                    plannedSurface.FitMode);
                pendingSurface = await desktopHost.CreateSurfaceAsync(
                        pendingRequest,
                        cancellationToken)
                    .ConfigureAwait(false);
                SessionId sessionId = new(Guid.NewGuid().ToString("N"));
                pendingRenderer = await provider.CreateAsync(
                        new RendererLaunchContext(
                            sessionId,
                            generation,
                            libraryEntry.Definition,
                            libraryEntry.CanonicalContentRoot),
                        cancellationToken)
                    .ConfigureAwait(false);
                double scaleFactor = plannedSurface.DisplayIds.Count == 1
                    ? displays[plannedSurface.DisplayIds[0]].ScaleFactor
                    : 1;

                await pendingRenderer.SendAsync(
                        new AttachRendererSurface(
                            generation,
                            pendingSurface.WindowHandle,
                            plannedSurface.Bounds,
                            scaleFactor),
                        cancellationToken)
                    .ConfigureAwait(false);
                await pendingRenderer.SendAsync(
                        new LoadRendererContent(
                            generation,
                            libraryEntry.Definition,
                            libraryEntry.CanonicalContentRoot),
                        cancellationToken)
                    .ConfigureAwait(false);
                await WaitForFirstFrameAsync(pendingRenderer, generation, cancellationToken)
                    .ConfigureAwait(false);

                WallpaperSession session = new(
                    sessionId,
                    plannedSurface.WallpaperId,
                    plannedSurface.DisplayIds,
                    provider.Descriptor.Id,
                    PlaybackState.Playing,
                    generation);
                preparedSessions.Add(new PreparedSession(
                    session,
                    pendingRenderer,
                    [new PreparedSurface(pendingSurface, pendingRequest)]));
                pendingRenderer = null;
                pendingSurface = null;
                pendingRequest = null;
            }

            return new PreparedApply(
                generation,
                topology.Revision,
                desktopTopology.Revision,
                assignments.ToArray(),
                preparedSessions);
        }
        catch (Exception preparationFailure)
        {
            List<Exception> cleanupFailures = [];
            await CleanupSessionAsync(
                    pendingRenderer,
                    pendingSurface is null
                        ? []
                        : [new PreparedSurface(pendingSurface, pendingRequest!)],
                    generation,
                    "preparation-failed",
                    cleanupFailures,
                    CancellationToken.None)
                .ConfigureAwait(false);
            foreach (PreparedSession prepared in preparedSessions)
            {
                await CleanupSessionAsync(
                        prepared.Renderer,
                        prepared.Surfaces,
                        prepared.Session.Generation,
                        "preparation-rolled-back",
                        cleanupFailures,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "Wallpaper preparation failed and one or more provisional resources could not be released.",
                    [preparationFailure, .. cleanupFailures]);
            }

            throw;
        }
    }

    public async Task RetireAsync(
        IReadOnlyList<PreparedSession> sessions,
        CancellationToken cancellationToken)
    {
        List<Exception> cleanupFailures = [];
        foreach (PreparedSession prepared in sessions)
        {
            await CleanupSessionAsync(
                    prepared.Renderer,
                    prepared.Surfaces,
                    prepared.Session.Generation,
                    "session-replaced",
                    cleanupFailures,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                "One or more retired session resources could not be released.",
                cleanupFailures);
        }
    }

    public Task ReplaceSurfacesAsync(
        PreparedApply prepared,
        IReadOnlyList<PreparedSession> replacedSessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(replacedSessions);
        return desktopHost.ReplaceSurfacesAsync(
            new SurfaceReplacement(
                prepared.Sessions
                    .SelectMany(session => session.Surfaces)
                    .Select(surface => surface.Surface.Id)
                    .ToArray(),
                replacedSessions
                    .SelectMany(session => session.Surfaces)
                    .Select(surface => surface.Surface.Id)
                    .ToArray(),
                prepared.DesktopTopologyRevision),
            cancellationToken);
    }

    public async Task<PreparedRecovery> PrepareRecoveryAsync(
        IReadOnlyList<PreparedSession> activeSessions,
        DisplayTopology topology,
        long hostGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeSessions);
        ArgumentNullException.ThrowIfNull(topology);
        DesktopTopology desktopTopology = await desktopHost
            .EnsureTopologyAsync(topology, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<DisplayId, DisplayDescriptor> displays = topology.Displays
            .ToDictionary(display => display.Id);
        List<RecoveredSession> recovered = [];
        List<PreparedSurface> provisionalSurfaces = [];

        try
        {
            foreach (PreparedSession active in activeSessions)
            {
                if (active.Surfaces.Count != 1)
                {
                    throw new InvalidOperationException(
                        "Desktop recovery currently requires exactly one Surface per Renderer session.");
                }

                PreparedSurface previousSurface = active.Surfaces[0];
                DesktopSurface provisional = await desktopHost.CreateSurfaceAsync(
                        previousSurface.Request,
                        cancellationToken)
                    .ConfigureAwait(false);
                PreparedSurface provisionalSurface = new(provisional, previousSurface.Request);
                provisionalSurfaces.Add(provisionalSurface);
                double scaleFactor = previousSurface.Request.DisplayIds.Count == 1
                    ? displays[previousSurface.Request.DisplayIds[0]].ScaleFactor
                    : 1;

                await active.Renderer.SendAsync(
                        new SetRendererBounds(
                            active.Session.Generation,
                            previousSurface.Request.Bounds,
                            scaleFactor),
                        cancellationToken)
                    .ConfigureAwait(false);
                await active.Renderer.SendAsync(
                        new AttachRendererSurface(
                            active.Session.Generation,
                            provisional.WindowHandle,
                            previousSurface.Request.Bounds,
                            scaleFactor),
                        cancellationToken)
                    .ConfigureAwait(false);
                await WaitForSurfaceAndFirstFrameAsync(
                        active.Renderer,
                        active.Session.Generation,
                        cancellationToken)
                    .ConfigureAwait(false);
                recovered.Add(new RecoveredSession(
                    active,
                    active with { Surfaces = [provisionalSurface] }));
            }

            return new PreparedRecovery(
                hostGeneration,
                topology.Revision,
                desktopTopology.Revision,
                recovered);
        }
        catch (Exception recoveryFailure)
        {
            List<Exception> rollbackFailures = [];
            foreach (PreparedSession active in activeSessions.Take(recovered.Count + 1))
            {
                await TryReattachPreviousSurfaceAsync(active, displays, rollbackFailures)
                    .ConfigureAwait(false);
            }

            await DestroySurfacesCollectingFailuresAsync(provisionalSurfaces, rollbackFailures)
                .ConfigureAwait(false);
            if (rollbackFailures.Count > 0)
            {
                throw new AggregateException(
                    "Desktop recovery failed and rollback was incomplete.",
                    [recoveryFailure, .. rollbackFailures]);
            }

            throw;
        }
    }

    public Task ReplaceRecoveredSurfacesAsync(
        PreparedRecovery recovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        return desktopHost.ReplaceSurfacesAsync(
            new SurfaceReplacement(
                recovery.Sessions
                    .SelectMany(session => session.Replacement.Surfaces)
                    .Select(surface => surface.Surface.Id)
                    .ToArray(),
                recovery.Sessions
                    .SelectMany(session => session.Previous.Surfaces)
                    .Select(surface => surface.Surface.Id)
                    .ToArray(),
                recovery.DesktopTopologyRevision),
            cancellationToken);
    }

    public async Task DestroySurfacesAsync(
        IEnumerable<PreparedSurface> surfaces,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        List<Exception> failures = [];
        foreach (PreparedSurface surface in surfaces)
        {
            try
            {
                await desktopHost.DestroySurfaceAsync(surface.Surface.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Desktop Surface cleanup failed.", failures);
        }
    }

    private static async Task TryReattachPreviousSurfaceAsync(
        PreparedSession active,
        Dictionary<DisplayId, DisplayDescriptor> displays,
        List<Exception> failures)
    {
        if (active.Surfaces.Count != 1)
        {
            return;
        }

        PreparedSurface previous = active.Surfaces[0];
        double scaleFactor = previous.Request.DisplayIds.Count == 1
            ? displays[previous.Request.DisplayIds[0]].ScaleFactor
            : 1;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        try
        {
            await active.Renderer.SendAsync(
                    new SetRendererBounds(
                        active.Session.Generation,
                        previous.Request.Bounds,
                        scaleFactor),
                    timeout.Token)
                .ConfigureAwait(false);
            await active.Renderer.SendAsync(
                    new AttachRendererSurface(
                        active.Session.Generation,
                        previous.Surface.WindowHandle,
                        previous.Request.Bounds,
                        scaleFactor),
                    timeout.Token)
                .ConfigureAwait(false);
            await WaitForSurfaceAndFirstFrameAsync(
                    active.Renderer,
                    active.Session.Generation,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private async Task DestroySurfacesCollectingFailuresAsync(
        IEnumerable<PreparedSurface> surfaces,
        List<Exception> failures)
    {
        try
        {
            await DestroySurfacesAsync(surfaces, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AggregateException exception)
        {
            failures.AddRange(exception.InnerExceptions);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private async Task CleanupSessionAsync(
        IRendererSession? renderer,
        IReadOnlyList<PreparedSurface> surfaces,
        long generation,
        string reason,
        List<Exception> failures,
        CancellationToken shutdownCancellationToken = default)
    {
        if (renderer is not null)
        {
            try
            {
                await renderer.SendAsync(
                        new ShutdownRenderer(generation, reason),
                        shutdownCancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                await renderer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        foreach (PreparedSurface surface in surfaces)
        {
            try
            {
                await desktopHost.DestroySurfaceAsync(surface.Surface.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private static async Task WaitForFirstFrameAsync(
        IRendererSession renderer,
        long generation,
        CancellationToken cancellationToken)
    {
        await foreach (RendererEvent rendererEvent in renderer.ReadEventsAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (rendererEvent.Generation != generation)
            {
                continue;
            }

            switch (rendererEvent)
            {
                case RendererFirstFramePresented:
                    return;
                case RendererFailed failure:
                    throw new InvalidOperationException(
                        $"Renderer failed before presenting its first frame: {failure.ErrorCode}.");
            }
        }

        throw new InvalidOperationException("Renderer event stream ended before the first frame.");
    }

    private static async Task WaitForSurfaceAndFirstFrameAsync(
        IRendererSession renderer,
        long generation,
        CancellationToken cancellationToken)
    {
        bool surfaceAttached = false;
        bool firstFramePresented = false;
        await foreach (RendererEvent rendererEvent in renderer.ReadEventsAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (rendererEvent.Generation != generation)
            {
                continue;
            }

            switch (rendererEvent)
            {
                case RendererSurfaceAttached:
                    surfaceAttached = true;
                    break;
                case RendererFirstFramePresented:
                    firstFramePresented = true;
                    break;
                case RendererFailed failure:
                    throw new InvalidOperationException(
                        $"Renderer failed during desktop recovery: {failure.ErrorCode}.");
            }

            if (surfaceAttached && firstFramePresented)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "Renderer event stream ended before SurfaceAttached and FirstFramePresented.");
    }
}

internal sealed record PreparedApply(
    long Generation,
    long DisplayTopologyRevision,
    long DesktopTopologyRevision,
    IReadOnlyList<WallpaperAssignment> Assignments,
    IReadOnlyList<PreparedSession> Sessions);

internal sealed record PreparedSession(
    WallpaperSession Session,
    IRendererSession Renderer,
    IReadOnlyList<PreparedSurface> Surfaces);

internal sealed record PreparedSurface(
    DesktopSurface Surface,
    SurfaceRequest Request);

internal sealed record PreparedRecovery(
    long HostGeneration,
    long DisplayTopologyRevision,
    long DesktopTopologyRevision,
    IReadOnlyList<RecoveredSession> Sessions);

internal sealed record RecoveredSession(
    PreparedSession Previous,
    PreparedSession Replacement);
