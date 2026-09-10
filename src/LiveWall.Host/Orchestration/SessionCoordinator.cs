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
        ApplyGenerationOperation operation,
        CancellationToken cancellationToken);

    int GetPlannedSurfaceCount(
        IReadOnlyList<WallpaperAssignment> assignments,
        DisplayTopology topology);

    TimeSpan GetApplyDeadline(int surfaceCount);

    Task<SessionRetirementBatchResult> RetireAsync(
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

    Task AbandonSurfacesAsync(
        IEnumerable<PreparedSurface> surfaces,
        CancellationToken cancellationToken);

    Task AbandonRecoveryAsync(
        PreparedRecovery recovery,
        CancellationToken cancellationToken);
}

internal sealed class SessionCoordinator : ISessionCoordinator
{
    private readonly IWallpaperRepository wallpaperRepository;
    private readonly ILayoutPlanner layoutPlanner;
    private readonly RendererProviderSelector rendererProviderSelector;
    private readonly IDesktopHost desktopHost;
    private readonly RuntimeEnvironment runtimeEnvironment;
    private readonly TimeSpan rendererStageTimeout;
    private readonly ApplyDeadlineBudget applyDeadlineBudget;
    private readonly SessionRetirementPolicy retirementPolicy;

    public SessionCoordinator(
        IWallpaperRepository wallpaperRepository,
        ILayoutPlanner layoutPlanner,
        RendererProviderSelector rendererProviderSelector,
        IDesktopHost desktopHost,
        RuntimeEnvironment runtimeEnvironment,
        TimeSpan? rendererStageTimeout = null,
        ApplyDeadlineBudget? applyDeadlineBudget = null,
        SessionRetirementPolicy? retirementPolicy = null)
    {
        this.wallpaperRepository = wallpaperRepository ??
            throw new ArgumentNullException(nameof(wallpaperRepository));
        this.layoutPlanner = layoutPlanner ?? throw new ArgumentNullException(nameof(layoutPlanner));
        this.rendererProviderSelector = rendererProviderSelector ??
            throw new ArgumentNullException(nameof(rendererProviderSelector));
        this.desktopHost = desktopHost ?? throw new ArgumentNullException(nameof(desktopHost));
        this.runtimeEnvironment = runtimeEnvironment ??
            throw new ArgumentNullException(nameof(runtimeEnvironment));
        this.rendererStageTimeout = rendererStageTimeout ?? TimeSpan.FromSeconds(10);
        this.applyDeadlineBudget = applyDeadlineBudget ?? ApplyDeadlineBudget.Default;
        this.retirementPolicy = (retirementPolicy ?? SessionRetirementPolicy.Default).Validate();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            this.rendererStageTimeout,
            TimeSpan.Zero);
    }

    public async Task<PreparedApply> PrepareApplyAsync(
        IReadOnlyList<WallpaperAssignment> assignments,
        DisplayTopology topology,
        long generation,
        ApplyGenerationOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(operation);
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

        DesktopTopology desktopTopology;
        try
        {
            desktopTopology = await desktopHost
                .EnsureTopologyAsync(topology, cancellationToken)
                .ConfigureAwait(false);
            operation.Record(
                ApplyGenerationPhase.TopologyReady,
                assignments.Select(assignment => assignment.DisplayId).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            operation.Record(
                ApplyGenerationPhase.TopologyReady,
                assignments.Select(assignment => assignment.DisplayId).ToArray(),
                outcome: ApplyStageOutcome.Rejected,
                failureReason: ApplyFailureReason.TopologyFailed,
                detail: exception.Message);
            throw new ApplyStageException(
                ApplyGenerationPhase.TopologyReady,
                ApplyFailureReason.TopologyFailed,
                exception.Message,
                exception);
        }
        LayoutPlan plan = layoutPlanner.CreatePlan(topology, assignments);
        Dictionary<DisplayId, DisplayDescriptor> displays = topology.Displays
            .ToDictionary(display => display.Id);
        List<PreparedSession> preparedSessions = [];
        DesktopSurface? pendingSurface = null;
        SurfaceRequest? pendingRequest = null;
        IRendererSession? pendingRenderer = null;
        IReadOnlyList<DisplayId>? pendingDisplayIds = null;
        int pendingSurfaceOrdinal = 0;

        try
        {
            for (int surfaceIndex = 0; surfaceIndex < plan.Surfaces.Count; surfaceIndex++)
            {
                PlannedSurface plannedSurface = plan.Surfaces[surfaceIndex];
                int surfaceOrdinal = surfaceIndex + 1;
                pendingDisplayIds = plannedSurface.DisplayIds;
                pendingSurfaceOrdinal = surfaceOrdinal;
                WallpaperLibraryEntry libraryEntry = libraryEntries[plannedSurface.WallpaperId];
                IRendererProvider provider = rendererProviderSelector.Select(
                    libraryEntry.Definition,
                    runtimeEnvironment);
                pendingRequest = new SurfaceRequest(
                    plannedSurface.DisplayIds,
                    plannedSurface.Bounds,
                    plannedSurface.FitMode);
                operation.BeginPhase(ApplyGenerationPhase.SurfaceCreated);
                try
                {
                    pendingSurface = await desktopHost.CreateSurfaceAsync(
                            pendingRequest,
                            cancellationToken)
                        .ConfigureAwait(false);
                    operation.Record(
                        ApplyGenerationPhase.SurfaceCreated,
                        plannedSurface.DisplayIds,
                        surfaceOrdinal);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    operation.Record(
                        ApplyGenerationPhase.SurfaceCreated,
                        plannedSurface.DisplayIds,
                        surfaceOrdinal,
                        ApplyStageOutcome.Rejected,
                        ApplyFailureReason.SurfaceCreationFailed,
                        exception.Message);
                    throw new ApplyStageException(
                        ApplyGenerationPhase.SurfaceCreated,
                        ApplyFailureReason.SurfaceCreationFailed,
                        exception.Message,
                        exception);
                }
                SessionId sessionId = new(Guid.NewGuid().ToString("N"));
                RendererLaunchContext launchContext = new(
                    sessionId,
                    generation,
                    libraryEntry.Definition,
                    libraryEntry.CanonicalContentRoot);
                operation.BeginPhase(ApplyGenerationPhase.ProcessStarted);
                pendingRenderer = provider is IObservableRendererProvider observable
                    ? await observable.CreateObservedAsync(
                            launchContext,
                            milestone =>
                            {
                                if (milestone.IsPhaseStart)
                                {
                                    operation.BeginPhase(milestone.Phase);
                                }
                                else
                                {
                                    operation.Record(
                                        milestone.Phase,
                                        plannedSurface.DisplayIds,
                                        surfaceOrdinal,
                                        milestone.Outcome,
                                        milestone.FailureReason,
                                        milestone.Detail,
                                        milestone.Timestamp);
                                }
                            },
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await provider.CreateAsync(launchContext, cancellationToken)
                        .ConfigureAwait(false);
                if (provider is not IObservableRendererProvider)
                {
                    operation.Record(
                        ApplyGenerationPhase.Initialized,
                        plannedSurface.DisplayIds,
                        surfaceOrdinal);
                }
                double scaleFactor = plannedSurface.DisplayIds.Count == 1
                    ? displays[plannedSurface.DisplayIds[0]].ScaleFactor
                    : 1;

                operation.BeginPhase(ApplyGenerationPhase.AttachSent);
                try
                {
                    await pendingRenderer.SendAsync(
                            new AttachRendererSurface(
                                generation,
                                pendingSurface.WindowHandle,
                                plannedSurface.Bounds,
                                scaleFactor),
                            cancellationToken)
                        .ConfigureAwait(false);
                    operation.Record(
                        ApplyGenerationPhase.AttachSent,
                        plannedSurface.DisplayIds,
                        surfaceOrdinal);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    operation.Record(
                        ApplyGenerationPhase.AttachSent,
                        plannedSurface.DisplayIds,
                        surfaceOrdinal,
                        ApplyStageOutcome.Rejected,
                        ApplyFailureReason.AttachWriteFailed,
                        exception.Message);
                    throw new ApplyStageException(
                        ApplyGenerationPhase.AttachSent,
                        ApplyFailureReason.AttachWriteFailed,
                        exception.Message,
                        exception);
                }
                operation.BeginPhase(ApplyGenerationPhase.SurfaceAttached);
                await WaitForSurfaceAttachedAsync(
                        pendingRenderer,
                        generation,
                        "initial attachment",
                        operation,
                        plannedSurface.DisplayIds,
                        surfaceOrdinal,
                        cancellationToken)
                    .ConfigureAwait(false);
                operation.BeginPhase(ApplyGenerationPhase.LoadSent);
                try
                {
                    await pendingRenderer.SendAsync(
                            new LoadRendererContent(
                                generation,
                                libraryEntry.Definition,
                                libraryEntry.CanonicalContentRoot),
                            cancellationToken)
                        .ConfigureAwait(false);
                    operation.Record(
                        ApplyGenerationPhase.LoadSent,
                        plannedSurface.DisplayIds,
                        surfaceOrdinal);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    operation.Record(
                        ApplyGenerationPhase.LoadSent,
                        plannedSurface.DisplayIds,
                        surfaceOrdinal,
                        ApplyStageOutcome.Rejected,
                        ApplyFailureReason.LoadWriteFailed,
                        exception.Message);
                    throw new ApplyStageException(
                        ApplyGenerationPhase.LoadSent,
                        ApplyFailureReason.LoadWriteFailed,
                        exception.Message,
                        exception);
                }
                operation.BeginPhase(ApplyGenerationPhase.ContentLoaded);
                await WaitForContentAndFirstFrameAsync(
                        pendingRenderer,
                        generation,
                        operation,
                        plannedSurface.DisplayIds,
                        surfaceOrdinal,
                        cancellationToken)
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
                    [new PreparedSurface(pendingSurface, pendingRequest)],
                    surfaceOrdinal));
                pendingRenderer = null;
                pendingSurface = null;
                pendingRequest = null;
                pendingDisplayIds = null;
                pendingSurfaceOrdinal = 0;
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
            ApplyGenerationPhase failedPhase =
                operation.CurrentPhase ?? ApplyGenerationPhase.TopologyReady;
            List<Exception> cleanupFailures = [];
            await CleanupSessionAsync(
                    pendingRenderer,
                    pendingSurface is null
                        ? []
                        : [new PreparedSurface(pendingSurface, pendingRequest!)],
                    generation,
                    "preparation-failed",
                    cleanupFailures,
                    operation,
                    pendingDisplayIds,
                    pendingSurfaceOrdinal,
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
                        operation,
                        prepared.Session.Displays,
                        prepared.SurfaceOrdinal,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "Wallpaper preparation failed and one or more provisional resources could not be released.",
                    [preparationFailure, .. cleanupFailures]);
            }

            operation.BeginPhase(failedPhase);
            throw;
        }
    }

    public int GetPlannedSurfaceCount(
        IReadOnlyList<WallpaperAssignment> assignments,
        DisplayTopology topology)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(topology);
        return layoutPlanner.CreatePlan(topology, assignments).Surfaces.Count;
    }

    public TimeSpan GetApplyDeadline(int surfaceCount) =>
        applyDeadlineBudget.Calculate(surfaceCount);

    public async Task<SessionRetirementBatchResult> RetireAsync(
        IReadOnlyList<PreparedSession> sessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        SessionRetirementResult[] results = await Task.WhenAll(
                sessions.Select(prepared =>
                    RetireSessionAsync(prepared, cancellationToken)))
            .ConfigureAwait(false);
        return new SessionRetirementBatchResult(Array.AsReadOnly(results));
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
                            hostGeneration,
                            previousSurface.Request.Bounds,
                            scaleFactor),
                        cancellationToken)
                    .ConfigureAwait(false);
                await active.Renderer.SendAsync(
                        new AttachRendererSurface(
                            hostGeneration,
                            provisional.WindowHandle,
                            previousSurface.Request.Bounds,
                            scaleFactor),
                        cancellationToken)
                    .ConfigureAwait(false);
                await WaitForSurfaceAttachedAsync(
                        active.Renderer,
                        hostGeneration,
                        "desktop recovery attachment",
                        null,
                        null,
                        0,
                        cancellationToken)
                    .ConfigureAwait(false);
                await WaitForFirstFrameAfterReattachAsync(
                        active.Renderer,
                        hostGeneration,
                        cancellationToken)
                    .ConfigureAwait(false);
                recovered.Add(new RecoveredSession(
                    active,
                    active with
                    {
                        Session = active.Session with { Generation = hostGeneration },
                        Surfaces = [provisionalSurface],
                    }));
            }

            return new PreparedRecovery(
                hostGeneration,
                topology.Revision,
                desktopTopology.Revision,
                recovered);
        }
        catch (Exception recoveryFailure)
        {
            List<Exception> cleanupFailures = [];
            await AbandonSessionsAndSurfacesAsync(
                    activeSessions,
                    activeSessions.SelectMany(session => session.Surfaces)
                        .Concat(provisionalSurfaces),
                    cleanupFailures,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "Desktop recovery failed and uncertain generation resources could not all be abandoned.",
                    [recoveryFailure, .. cleanupFailures]);
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

    public async Task AbandonSurfacesAsync(
        IEnumerable<PreparedSurface> surfaces,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        List<Exception> failures = [];
        foreach (PreparedSurface surface in surfaces)
        {
            try
            {
                await desktopHost.AbandonSurfaceAsync(surface.Surface.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Desktop Surface abandonment failed.", failures);
        }
    }

    public async Task AbandonRecoveryAsync(
        PreparedRecovery recovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        PreparedSession[] sessions = recovery.Sessions
            .Select(item => item.Previous)
            .DistinctBy(item => item.Session.Id)
            .ToArray();
        PreparedSurface[] surfaces = recovery.Sessions
            .SelectMany(item => item.Previous.Surfaces.Concat(item.Replacement.Surfaces))
            .DistinctBy(item => item.Surface.Id)
            .ToArray();
        List<Exception> failures = [];
        await AbandonSessionsAndSurfacesAsync(
                sessions,
                surfaces,
                failures,
                cancellationToken)
            .ConfigureAwait(false);
        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Desktop recovery resources could not all be abandoned.",
                failures);
        }
    }

    private async Task AbandonSessionsAndSurfacesAsync(
        IEnumerable<PreparedSession> sessions,
        IEnumerable<PreparedSurface> surfaces,
        List<Exception> failures,
        CancellationToken cancellationToken)
    {
        HashSet<IRendererSession> abandonedRenderers =
            new(ReferenceEqualityComparer.Instance);
        foreach (PreparedSession session in sessions)
        {
            IRendererSession renderer = session.Renderer;
            if (!abandonedRenderers.Add(renderer))
            {
                continue;
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

        foreach (PreparedSurface surface in surfaces.DistinctBy(item => item.Surface.Id))
        {
            try
            {
                await desktopHost.AbandonSurfaceAsync(
                        surface.Surface.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private async Task CleanupSessionAsync(
        IRendererSession? renderer,
        IReadOnlyList<PreparedSurface> surfaces,
        long generation,
        string reason,
        List<Exception> failures,
        ApplyGenerationOperation? operation = null,
        IReadOnlyList<DisplayId>? displayIds = null,
        int surfaceOrdinal = 0,
        CancellationToken shutdownCancellationToken = default)
    {
        int failuresBeforeCleanup = failures.Count;
        using CancellationTokenSource? cleanupDeadline = operation is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(
                shutdownCancellationToken,
                operation.DeadlineToken);
        CancellationToken gracefulShutdownToken =
            cleanupDeadline?.Token ?? shutdownCancellationToken;
        if (renderer is not null)
        {
            bool shutdownSent = false;
            try
            {
                await renderer.SendAsync(
                        new ShutdownRenderer(generation, reason),
                        gracefulShutdownToken)
                    .ConfigureAwait(false);
                shutdownSent = true;
            }
            catch (OperationCanceledException) when (
                operation?.DeadlineToken.IsCancellationRequested == true)
            {
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (shutdownSent)
            {
                try
                {
                    await WaitForShutdownCompletedAsync(
                            renderer,
                            generation,
                            gracefulShutdownToken)
                        .ConfigureAwait(false);
                    operation?.Record(
                        ApplyGenerationPhase.ShutdownCompleted,
                        displayIds,
                        surfaceOrdinal);
                }
                catch (OperationCanceledException) when (
                    operation?.DeadlineToken.IsCancellationRequested == true)
                {
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
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

        operation?.Record(
            ApplyGenerationPhase.CleanupVerified,
            displayIds,
            surfaceOrdinal,
            failures.Count == failuresBeforeCleanup
                ? ApplyStageOutcome.Success
                : ApplyStageOutcome.Rejected,
            failures.Count == failuresBeforeCleanup
                ? ApplyFailureReason.None
                : ApplyFailureReason.CleanupFailed,
            failures.Count == failuresBeforeCleanup
                ? null
                : "One or more Renderer or Surface resources failed cleanup.");
    }

    private async Task<SessionRetirementResult> RetireSessionAsync(
        PreparedSession prepared,
        CancellationToken cancellationToken)
    {
        long startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        List<SessionRetirementIssue> issues = [];
        bool shutdownSent = false;
        bool shutdownCompleted = false;
        bool rendererDisposed = false;
        bool surfacesCleaned = true;

        using (CancellationTokenSource shutdownBudget =
               CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            shutdownBudget.CancelAfter(retirementPolicy.GracefulShutdown);
            try
            {
                await prepared.Renderer.SendAsync(
                        new ShutdownRenderer(prepared.Session.Generation, "session-replaced"),
                        shutdownBudget.Token)
                    .ConfigureAwait(false);
                shutdownSent = true;
                await WaitForShutdownCompletedAsync(
                        prepared.Renderer,
                        prepared.Session.Generation,
                        shutdownBudget.Token)
                    .ConfigureAwait(false);
                shutdownCompleted = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                issues.Add(new SessionRetirementIssue(
                    SessionRetirementIssueKind.ShutdownCancelled,
                    "Graceful Renderer shutdown was cancelled by the caller."));
            }
            catch (OperationCanceledException)
            {
                issues.Add(new SessionRetirementIssue(
                    SessionRetirementIssueKind.ShutdownTimeout,
                    $"Renderer did not complete graceful shutdown within {retirementPolicy.GracefulShutdown}."));
            }
            catch (TimeoutException exception)
            {
                issues.Add(new SessionRetirementIssue(
                    SessionRetirementIssueKind.ShutdownTimeout,
                    exception.Message));
            }
            catch (Exception exception)
            {
                issues.Add(new SessionRetirementIssue(
                    SessionRetirementIssueKind.ShutdownFailed,
                    exception.Message));
            }
        }

        try
        {
            Task disposeTask = prepared.Renderer.DisposeAsync().AsTask();
            await disposeTask.WaitAsync(
                    retirementPolicy.RendererDispose,
                    CancellationToken.None)
                .ConfigureAwait(false);
            rendererDisposed = true;
        }
        catch (TimeoutException)
        {
            issues.Add(new SessionRetirementIssue(
                SessionRetirementIssueKind.RendererDisposeTimeout,
                $"Renderer DisposeAsync did not complete within {retirementPolicy.RendererDispose}."));
        }
        catch (Exception exception)
        {
            issues.Add(new SessionRetirementIssue(
                SessionRetirementIssueKind.RendererDisposeFailed,
                exception.Message));
        }

        foreach (PreparedSurface surface in prepared.Surfaces)
        {
            try
            {
                await desktopHost.DestroySurfaceAsync(surface.Surface.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                surfacesCleaned = false;
                issues.Add(new SessionRetirementIssue(
                    SessionRetirementIssueKind.SurfaceCleanupFailed,
                    $"Surface '{surface.Surface.Id}' cleanup failed: {exception.Message}"));
            }
        }

        return new SessionRetirementResult(
            prepared.Session.Id,
            prepared.Session.Generation,
            Array.AsReadOnly(prepared.Session.Displays.ToArray()),
            prepared.SurfaceOrdinal,
            SessionRetirementClock.Elapsed(startedTimestamp),
            shutdownSent,
            shutdownCompleted,
            rendererDisposed,
            surfacesCleaned,
            Array.AsReadOnly(issues.ToArray()));
    }

    private async Task WaitForSurfaceAttachedAsync(
        IRendererSession renderer,
        long generation,
        string phase,
        ApplyGenerationOperation? operation,
        IReadOnlyList<DisplayId>? displayIds,
        int surfaceOrdinal,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (RendererEvent rendererEvent in ReadStageEventsAsync(renderer, phase, cancellationToken)
                               .ConfigureAwait(false))
            {
                ValidateEventGeneration(rendererEvent, generation, phase);

                switch (rendererEvent)
                {
                    case RendererSurfaceAttached:
                        operation?.Record(
                            ApplyGenerationPhase.SurfaceAttached,
                            displayIds,
                            surfaceOrdinal);
                        return;
                    case RendererFailed failure:
                        throw new InvalidOperationException(
                            $"Renderer failed during {phase}: {failure.ErrorCode}.");
                    case RendererPlaybackStateChanged:
                        continue;
                    default:
                        throw UnexpectedRendererEvent(rendererEvent, phase);
                }
            }

            throw new InvalidOperationException(
                $"Renderer event stream ended before SurfaceAttached during {phase}.");
        }
        catch (TimeoutException exception) when (operation is not null)
        {
            operation.Record(
                ApplyGenerationPhase.SurfaceAttached,
                displayIds,
                surfaceOrdinal,
                ApplyStageOutcome.Timeout,
                ApplyFailureReason.SurfaceAttachedTimeout,
                exception.Message);
            throw new ApplyStageException(
                ApplyGenerationPhase.SurfaceAttached,
                ApplyFailureReason.SurfaceAttachedTimeout,
                exception.Message,
                exception);
        }
        catch (Exception exception) when (
            operation is not null &&
            exception is not OperationCanceledException &&
            exception is not ApplyStageException)
        {
            operation.Record(
                ApplyGenerationPhase.SurfaceAttached,
                displayIds,
                surfaceOrdinal,
                ApplyStageOutcome.Rejected,
                ApplyFailureReason.SurfaceAttachmentRejected,
                exception.Message);
            throw new ApplyStageException(
                ApplyGenerationPhase.SurfaceAttached,
                ApplyFailureReason.SurfaceAttachmentRejected,
                exception.Message,
                exception);
        }
    }

    private async Task WaitForContentAndFirstFrameAsync(
        IRendererSession renderer,
        long generation,
        ApplyGenerationOperation operation,
        IReadOnlyList<DisplayId> displayIds,
        int surfaceOrdinal,
        CancellationToken cancellationToken)
    {
        bool contentLoaded = false;
        try
        {
            await foreach (RendererEvent rendererEvent in ReadStageEventsAsync(
                               renderer,
                               "initial content load",
                               cancellationToken)
                               .ConfigureAwait(false))
            {
                const string phase = "initial content load";
                ValidateEventGeneration(rendererEvent, generation, phase);

                switch (rendererEvent)
                {
                    case RendererContentLoaded when !contentLoaded:
                        contentLoaded = true;
                        operation.Record(
                            ApplyGenerationPhase.ContentLoaded,
                            displayIds,
                            surfaceOrdinal);
                        operation.BeginPhase(ApplyGenerationPhase.FirstFramePresented);
                        break;
                    case RendererFirstFramePresented when contentLoaded:
                        operation.Record(
                            ApplyGenerationPhase.FirstFramePresented,
                            displayIds,
                            surfaceOrdinal);
                        return;
                    case RendererFailed failure:
                        throw new InvalidOperationException(
                            $"Renderer failed during {phase}: {failure.ErrorCode}.");
                    case RendererPlaybackStateChanged:
                        continue;
                    default:
                        throw UnexpectedRendererEvent(rendererEvent, phase);
                }
            }

            throw new InvalidOperationException(
                "Renderer event stream ended before ContentLoaded and FirstFramePresented.");
        }
        catch (TimeoutException exception)
        {
            ApplyGenerationPhase failedPhase = contentLoaded
                ? ApplyGenerationPhase.FirstFramePresented
                : ApplyGenerationPhase.ContentLoaded;
            ApplyFailureReason reason = contentLoaded
                ? ApplyFailureReason.FirstFrameTimeout
                : ApplyFailureReason.ContentLoadedTimeout;
            operation.Record(
                failedPhase,
                displayIds,
                surfaceOrdinal,
                ApplyStageOutcome.Timeout,
                reason,
                exception.Message);
            throw new ApplyStageException(failedPhase, reason, exception.Message, exception);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            exception is not ApplyStageException)
        {
            ApplyGenerationPhase failedPhase = contentLoaded
                ? ApplyGenerationPhase.FirstFramePresented
                : ApplyGenerationPhase.ContentLoaded;
            ApplyFailureReason reason = contentLoaded
                ? ApplyFailureReason.FirstFrameRejected
                : ApplyFailureReason.ContentLoadRejected;
            operation.Record(
                failedPhase,
                displayIds,
                surfaceOrdinal,
                ApplyStageOutcome.Rejected,
                reason,
                exception.Message);
            throw new ApplyStageException(failedPhase, reason, exception.Message, exception);
        }
    }

    private async Task WaitForFirstFrameAfterReattachAsync(
        IRendererSession renderer,
        long generation,
        CancellationToken cancellationToken)
    {
        await foreach (RendererEvent rendererEvent in ReadStageEventsAsync(
                           renderer,
                           "desktop recovery first frame",
                           cancellationToken)
                           .ConfigureAwait(false))
        {
            const string phase = "desktop recovery first frame";
            ValidateEventGeneration(rendererEvent, generation, phase);
            switch (rendererEvent)
            {
                case RendererFirstFramePresented:
                    return;
                case RendererFailed failure:
                    throw new InvalidOperationException(
                        $"Renderer failed during {phase}: {failure.ErrorCode}.");
                case RendererPlaybackStateChanged:
                    continue;
                default:
                    throw UnexpectedRendererEvent(rendererEvent, phase);
            }
        }

        throw new InvalidOperationException(
            "Renderer event stream ended before FirstFramePresented during desktop recovery.");
    }

    private async Task WaitForShutdownCompletedAsync(
        IRendererSession renderer,
        long generation,
        CancellationToken cancellationToken)
    {
        await foreach (RendererEvent rendererEvent in ReadStageEventsAsync(
                           renderer,
                           "Renderer shutdown",
                           cancellationToken)
                           .ConfigureAwait(false))
        {
            ValidateEventGeneration(rendererEvent, generation, "Renderer shutdown");
            switch (rendererEvent)
            {
                case RendererShutdownCompleted:
                    return;
                case RendererFailed failure:
                    throw new InvalidOperationException(
                        $"Renderer failed during shutdown: {failure.ErrorCode}.");
                case RendererPlaybackStateChanged:
                    continue;
                default:
                    throw UnexpectedRendererEvent(rendererEvent, "Renderer shutdown");
            }
        }

        throw new InvalidOperationException(
            "Renderer event stream ended before ShutdownCompleted.");
    }

    private static void ValidateEventGeneration(
        RendererEvent rendererEvent,
        long expectedGeneration,
        string phase)
    {
        if (rendererEvent.Generation != expectedGeneration)
        {
            throw new InvalidOperationException(
                $"Renderer emitted generation {rendererEvent.Generation} during {phase}; " +
                $"generation {expectedGeneration} was required.");
        }
    }

    private static InvalidOperationException UnexpectedRendererEvent(
        RendererEvent rendererEvent,
        string phase) =>
        new($"Renderer emitted unexpected {rendererEvent.GetType().Name} during {phase}.");

    private async IAsyncEnumerable<RendererEvent> ReadStageEventsAsync(
        IRendererSession renderer,
        string phase,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(rendererStageTimeout);
        IAsyncEnumerator<RendererEvent> enumerator = renderer
            .ReadEventsAsync(timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Renderer did not complete {phase} within {rendererStageTimeout}.");
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
            }
        }

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
    IReadOnlyList<PreparedSurface> Surfaces,
    int SurfaceOrdinal = 0);

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
