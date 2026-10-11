using System.Collections.Concurrent;
using System.Threading.Channels;
using LiveWall.Application.Layouts;
using LiveWall.Application.Policies;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Sessions;
using LiveWall.Host.Orchestration;

namespace LiveWall.Host.CommandLoop;

internal sealed class HostCommandLoop : IAsyncDisposable
{
    private const int RetainedApplyDiagnostics = 32;
    private const int RetainedRecoveryDiagnostics = 32;
    private const int RetainedDisplayReconciliationDiagnostics = 32;
    private const int MaximumRecoveryRebuildAttempts = 3;
    private static readonly TimeSpan[] RecoveryRebuildDelays =
    [
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];
    private static readonly TimeSpan[] DisplayReconciliationRetryDelays =
    [
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];
    private readonly Channel<QueuedHostCommand> channel;
    private readonly HostState state = new();
    private readonly ISessionCoordinator coordinator;
    private readonly IPlaybackPolicyEvaluator playbackPolicyEvaluator;
    private readonly DisplayReconciliationPlanner displayReconciliationPlanner;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task runTask;
    private readonly ConcurrentDictionary<long, ApplyGenerationOperation> applyOperations = new();
    private readonly ConcurrentDictionary<long, DesktopRecoveryOperation> recoveryOperations = new();
    private readonly ConcurrentDictionary<long, DisplayReconciliationOperation>
        displayReconciliationOperations = new();
    private readonly Lock retirementGate = new();
    private readonly List<Task<SessionRetirementBatchResult>> pendingRetirements = [];
    private readonly List<SessionRetirementBatchResult> retirementHistory = [];
    private bool desktopRecoveryInProgress;
    private bool desktopRecoveryCircuitOpen;
    private DesktopRecoveryOperation? activeRecoveryOperation;
    private DisplayReconciliationOperation? activeDisplayReconciliationOperation;
    private long? activeDisplayReconciliationGeneration;
    private volatile bool stopping;
    private readonly RendererPlaybackJournal playbackJournal = new();
    private bool deferredSessionShellChange;
    private DisplayTopology? deferredSessionTopology;
    private readonly HashSet<long> sessionBlockedGenerations = [];
    private WallpaperAssignment[]? pendingApplyAssignments;
    private readonly ConcurrentDictionary<long, SessionEnvironmentOperation> sessionEnvironmentOperations = new();
    private SessionEnvironmentOperation? activeSessionEnvironmentOperation;
    private CancellationTokenSource? activeRecoveryPreparation;
    private bool recoveryTopologyPending;
    private bool recoveryTopologyDirty;

    internal Task<SessionEnvironmentResult> WaitForSessionEnvironmentAsync(long validationRevision,
        CancellationToken cancellationToken) =>
        sessionEnvironmentOperations.TryGetValue(validationRevision, out SessionEnvironmentOperation? operation)
            ? operation.Completion.WaitAsync(cancellationToken)
            : Task.FromException<SessionEnvironmentResult>(new InvalidOperationException(
                $"Session validation {validationRevision} is not available."));

    internal IReadOnlyList<RendererPlaybackEvidence> GetPlaybackPolicyEvidenceForDiagnostics() =>
        playbackJournal.Snapshot();

    public HostCommandLoop(
        ISessionCoordinator coordinator,
        IPlaybackPolicyEvaluator playbackPolicyEvaluator,
        ILayoutPlanner? layoutPlanner = null)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.playbackPolicyEvaluator = playbackPolicyEvaluator ??
            throw new ArgumentNullException(nameof(playbackPolicyEvaluator));
        displayReconciliationPlanner = new DisplayReconciliationPlanner(
            layoutPlanner ?? new DefaultLayoutPlanner());
        channel = Channel.CreateUnbounded<QueuedHostCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        runTask = RunAsync(shutdown.Token);
    }

    public async Task<HostCommandResult> SendAsync(
        HostCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        TaskCompletionSource<HostCommandResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await channel.Writer.WriteAsync(new QueuedHostCommand(command, completion), cancellationToken)
            .ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool TryPost(HostCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return channel.Writer.TryWrite(new QueuedHostCommand(command, null));
    }

    internal IReadOnlyList<PreparedSession> GetPreparedSessionsForDiagnostics() =>
        state.GetPreparedSessions();

    internal Task<ApplyGenerationResult> WaitForApplyGenerationAsync(
        long generation,
        CancellationToken cancellationToken) =>
        applyOperations.TryGetValue(generation, out ApplyGenerationOperation? operation)
            ? operation.Completion.WaitAsync(cancellationToken)
            : Task.FromException<ApplyGenerationResult>(new InvalidOperationException(
                $"Apply generation {generation} is not available."));

    internal IReadOnlyList<ApplyTimelineEntry> GetApplyTimelineForDiagnostics(long generation) =>
        applyOperations.TryGetValue(generation, out ApplyGenerationOperation? operation)
            ? operation.Snapshot()
            : [];

    internal bool IsApplyDeadlineDisposedForDiagnostics(long generation) =>
        applyOperations.TryGetValue(generation, out ApplyGenerationOperation? operation) &&
        operation.IsDeadlineDisposed;

    internal Task<DesktopRecoveryResult> WaitForDesktopRecoveryAsync(
        long recoveryId,
        CancellationToken cancellationToken) =>
        recoveryOperations.TryGetValue(recoveryId, out DesktopRecoveryOperation? operation)
            ? operation.Completion.WaitAsync(cancellationToken)
            : Task.FromException<DesktopRecoveryResult>(new InvalidOperationException(
                $"Desktop recovery {recoveryId} is not available."));

    internal IReadOnlyList<DesktopRecoveryTimelineEntry> GetDesktopRecoveryTimelineForDiagnostics(
        long recoveryId) =>
        recoveryOperations.TryGetValue(recoveryId, out DesktopRecoveryOperation? operation)
            ? operation.Snapshot()
            : [];

    internal int RecoveryDiagnosticsCountForTests => recoveryOperations.Count;

    internal IReadOnlyList<long> GetRecoveryOperationIdsForDiagnostics() =>
        recoveryOperations.Keys.Order().ToArray();

    internal bool IsStoppingForEnvironmentEvents => stopping;

    internal Task<DisplayReconciliationResult> WaitForDisplayReconciliationAsync(
        long topologyRevision,
        CancellationToken cancellationToken) =>
        displayReconciliationOperations.TryGetValue(
            topologyRevision,
            out DisplayReconciliationOperation? operation)
            ? operation.Completion.WaitAsync(cancellationToken)
            : Task.FromException<DisplayReconciliationResult>(new InvalidOperationException(
                $"Display reconciliation {topologyRevision} is not available."));

    internal int DisplayReconciliationDiagnosticsCountForTests =>
        displayReconciliationOperations.Count;

    internal int RetainedApplyDiagnosticsCount => applyOperations.Count;

    internal IReadOnlyList<SessionRetirementBatchResult> GetRetirementHistoryForDiagnostics()
    {
        lock (retirementGate)
        {
            return Array.AsReadOnly(retirementHistory.ToArray());
        }
    }

    internal async Task<SessionRetirementBatchResult> WaitForPendingRetirementsForDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        Task<SessionRetirementBatchResult>[] retirements;
        lock (retirementGate)
        {
            retirements = pendingRetirements.ToArray();
        }

        SessionRetirementBatchResult[] batches = await Task.WhenAll(retirements)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return new SessionRetirementBatchResult(Array.AsReadOnly(
            batches.SelectMany(batch => batch.Sessions)
                .GroupBy(session => session.SessionId)
                .Select(group => group.Last())
                .ToArray()));
    }

    internal async Task<SessionRetirementBatchResult> WaitForSessionRetirementsForDiagnosticsAsync(
        IReadOnlyCollection<SessionId> sessionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        HashSet<SessionId> expected = sessionIds.ToHashSet();
        if (expected.Count == 0)
        {
            return new SessionRetirementBatchResult([]);
        }

        while (true)
        {
            SessionRetirementResult[] observed;
            Task<SessionRetirementBatchResult>[] pending;
            lock (retirementGate)
            {
                observed = retirementHistory
                    .SelectMany(batch => batch.Sessions)
                    .Where(result => expected.Contains(result.SessionId))
                    .DistinctBy(result => result.SessionId)
                    .ToArray();
                pending = pendingRetirements.Where(task => !task.IsCompleted).ToArray();
            }

            if (observed.Select(result => result.SessionId).ToHashSet().SetEquals(expected))
            {
                return new SessionRetirementBatchResult(Array.AsReadOnly(observed));
            }

            Task progress = pending.Length == 0
                ? Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                : Task.WhenAny(pending);
            await progress.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<SessionRetirementBatchResult> RetireActiveSessionsForDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        HostCommandResult result = await SendAsync(new ShutdownCommand(), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                $"Host rejected diagnostic retirement: {result.ErrorCode}.");
        }

        return await WaitForPendingRetirementsForDiagnosticsAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        activeSessionEnvironmentOperation?.Complete(SessionEnvironmentTerminalState.Cancelled,
            reason: SessionEnvironmentFailureReason.HostStopping);
        channel.Writer.TryComplete();
        shutdown.Cancel();
        if (activeRecoveryOperation is not null)
        {
            CompleteActiveRecovery(
                state.Generation,
                DesktopRecoveryTerminalState.Cancelled,
                null,
                ApplyFailureReason.Cancelled,
                "Host command loop was disposed during desktop recovery.");
        }
        activeDisplayReconciliationOperation?.Complete(
            DisplayReconciliationTerminalState.Cancelled,
            state.Generation,
            ApplyFailureReason.Cancelled,
            "Host command loop was disposed during display reconciliation.");
        if (activeDisplayReconciliationGeneration is long reconciliationGeneration)
        {
            CompleteSupersededApply(reconciliationGeneration);
        }
        activeDisplayReconciliationOperation = null;
        activeDisplayReconciliationGeneration = null;
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }

        PreparedSession[] activeSessions = state.InvalidateAllSessions().ToArray();
        if (activeSessions.Length > 0)
        {
            TrackRetirement(activeSessions, CancellationToken.None);
        }

        Task<SessionRetirementBatchResult>[] retirements;
        lock (retirementGate)
        {
            retirements = pendingRetirements.ToArray();
        }

        try
        {
            await Task.WhenAll(retirements).ConfigureAwait(false);
        }
        finally
        {
            foreach (ApplyGenerationOperation operation in applyOperations.Values)
            {
                operation.Dispose();
            }
            applyOperations.Clear();
            shutdown.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (QueuedHostCommand queued in channel.Reader.ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            try
            {
                HostCommandResult result = await HandleAsync(queued.Command, cancellationToken)
                    .ConfigureAwait(false);
                queued.Completion?.TrySetResult(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                queued.Completion?.TrySetException(exception);
            }
        }
    }

    private Task<HostCommandResult> HandleAsync(
        HostCommand command,
        CancellationToken cancellationToken)
    {
        if (state.ShutdownRequested)
        {
            return HandleStoppingAsync(command);
        }

        return command switch
        {
            ApplyPreparedCommand prepared => CompleteApplyAsync(prepared, cancellationToken),
            DisplayReconciliationPreparedCommand reconciled =>
                CompleteDisplayReconciliationAsync(reconciled, cancellationToken),
            DesktopRecoveryPreparedCommand recovered => CompleteDesktopRecoveryAsync(
                recovered,
                cancellationToken),
            DesktopRecoveryRebuildPreparedCommand rebuilt => CompleteRecoveryRebuildAsync(
                rebuilt,
                cancellationToken),
            DisplayTopologyChangedCommand topology => SetTopologyAsync(topology, cancellationToken),
            SystemStateChangedCommand systemState => SetSystemStateAsync(systemState, cancellationToken),
            ContinueSessionReconnectCommand reconnect => ContinueSessionReconnectAsync(reconnect, cancellationToken),
            _ => Task.FromResult(Handle(command, cancellationToken)),
        };
    }

    private async Task<HostCommandResult> HandleStoppingAsync(HostCommand command)
    {
        switch (command)
        {
            case ShutdownCommand:
            case GetHostStateCommand:
                return HostCommandResult.Success(state.Revision, state.Snapshot());
            case ApplyPreparedCommand prepared:
                CompleteSupersededApply(prepared.Generation);
                TrackRetirement(prepared.Prepared.Sessions, CancellationToken.None);
                return HostCommandResult.Success(state.Revision, state.Snapshot());
            case DisplayReconciliationPreparedCommand reconciled:
                CompleteSupersededApply(reconciled.Generation);
                TrackRetirement(reconciled.Prepared.Sessions, CancellationToken.None);
                CompleteDisplayReconciliation(
                    reconciled.Prepared.DisplayTopologyRevision,
                    DisplayReconciliationTerminalState.Cancelled,
                    reconciled.Generation,
                    ApplyFailureReason.Cancelled,
                    "Host is stopping; the prepared display reconciliation was discarded.");
                return HostCommandResult.Success(state.Revision, state.Snapshot());
            case DesktopRecoveryPreparedCommand recovered:
                try
                {
                    // Shutdown already retired the active/previous Renderer sessions. Only
                    // abandon the not-yet-committed replacement Surface generation here;
                    // abandoning the entire recovery would dispose the same Renderer twice.
                    await coordinator.AbandonSurfacesAsync(
                            recovered.Recovery.Sessions
                                .SelectMany(session => session.Replacement.Surfaces),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    desktopRecoveryInProgress = false;
                    CompleteActiveRecovery(
                        state.Generation,
                        DesktopRecoveryTerminalState.Cancelled,
                        null,
                        ApplyFailureReason.Cancelled,
                        "Host is stopping; the prepared desktop recovery was abandoned.");
                }
                return HostCommandResult.Success(state.Revision, state.Snapshot());
            case DesktopRecoveryRebuildPreparedCommand rebuilt:
                CompleteSupersededApply(rebuilt.Generation);
                TrackRetirement(rebuilt.Prepared.Sessions, CancellationToken.None);
                desktopRecoveryInProgress = false;
                CompleteActiveRecovery(
                    state.Generation,
                    DesktopRecoveryTerminalState.Cancelled,
                    null,
                    ApplyFailureReason.Cancelled,
                    "Host is stopping; the prepared recovery rebuild was discarded.");
                return HostCommandResult.Success(state.Revision, state.Snapshot());
            case ApplyPreparationFailedCommand failed:
                FindApplyOperation(failed.Generation)?.Fail(
                    failed.Phase,
                    ApplyFailureReason.Cancelled,
                    "Host stopped while Apply preparation was completing.",
                    ApplyGenerationTerminalState.Cancelled);
                return HostCommandResult.Success(state.Revision, state.Snapshot());
            case DisplayReconciliationFailedCommand failed:
                CompleteDisplayReconciliation(
                    failed.TopologyRevision,
                    DisplayReconciliationTerminalState.Cancelled,
                    failed.Generation,
                    ApplyFailureReason.Cancelled,
                    "Host stopped while display reconciliation was completing.");
                return HostCommandResult.Success(state.Revision, state.Snapshot());
            case DesktopRecoveryFailedCommand:
            case DesktopRecoveryRebuildFailedCommand:
                desktopRecoveryInProgress = false;
                CompleteActiveRecovery(
                    state.Generation,
                    DesktopRecoveryTerminalState.Cancelled,
                    null,
                    ApplyFailureReason.Cancelled,
                    "Host stopped while desktop recovery was completing.");
                return HostCommandResult.Success(state.Revision, state.Snapshot());
            case RetryDesktopRecoveryCommand:
            case RendererEventCommand:
            case RendererExitedCommand:
                return HostCommandResult.Success(state.Revision, state.Snapshot());
            default:
                return HostCommandResult.Reject(state.Revision, "host.stopping");
        }
    }

    private HostCommandResult Handle(HostCommand command, CancellationToken cancellationToken) =>
        command switch
        {
            SessionEnvironmentFailedCommand failed => CompleteSessionEnvironmentFailure(failed),
            SessionEnvironmentCompletedCommand completed => CompleteSessionEnvironment(completed),
            ApplyWallpaperCommand apply => BeginApply(apply, cancellationToken),
            RestoreAssignmentsCommand restore => BeginRestore(restore, cancellationToken),
            ApplyPreparationFailedCommand failed => FailApply(failed),
            DisplayReconciliationFailedCommand failed => FailDisplayReconciliation(failed),
            SetPlaybackPolicyCommand policy => SetPlaybackPolicy(policy, cancellationToken),
            ChangePlaybackIntentCommand playback => ChangePlaybackIntent(playback, cancellationToken),
            RendererEventCommand rendererEvent => HandleRendererEvent(rendererEvent),
            RendererExitedCommand rendererExited => HandleRendererExit(rendererExited),
            ExplorerRestartedCommand => BeginDesktopRecovery(cancellationToken),
            DesktopRecoveryFailedCommand failed => FailDesktopRecovery(failed),
            RetryDesktopRecoveryCommand retry => BeginRecoveryRebuild(retry, cancellationToken),
            DesktopRecoveryRebuildFailedCommand failed => FailRecoveryRebuild(
                failed,
                cancellationToken),
            GetHostStateCommand => HostCommandResult.Success(state.Revision, state.Snapshot()),
            ShutdownCommand => RequestShutdown(cancellationToken),
            _ => HostCommandResult.Reject(state.Revision, "host.command.unsupported"),
        };

    private HostCommandResult BeginApply(
        ApplyWallpaperCommand command,
        CancellationToken cancellationToken)
    {
        WallpaperAssignment[] assignments = command.Displays
            .Select(displayId => new WallpaperAssignment(
                displayId,
                command.WallpaperId,
                command.LayoutMode,
                command.FitMode,
                command.PropertyPresetId))
            .ToArray();
        return BeginApply(assignments, cancellationToken);
    }

    private HostCommandResult BeginRestore(
        RestoreAssignmentsCommand command,
        CancellationToken cancellationToken)
    {
        state.RestoreAssignments(command.Assignments);
        if (state.DisplayTopology is null)
        {
            return HostCommandResult.Reject(state.Revision, "host.display.topology_unavailable");
        }

        HashSet<DisplayId> connected = state.DisplayTopology.Displays
            .Select(display => display.Id)
            .ToHashSet();
        WallpaperAssignment[] connectedAssignments = command.Assignments
            .Where(assignment => connected.Contains(assignment.DisplayId))
            .ToArray();
        return connectedAssignments.Length == 0
            ? HostCommandResult.Success(state.Revision, state.Snapshot())
            : BeginApply(connectedAssignments, cancellationToken);
    }

    private HostCommandResult BeginApply(
        WallpaperAssignment[] assignments,
        CancellationToken cancellationToken)
    {
        if (state.SystemState.IsSessionDisconnected ||
            activeSessionEnvironmentOperation is { Completion.IsCompleted: false })
        {
            return HostCommandResult.Reject(state.Revision, "host.session.presentation_blocked");
        }
        if (state.DisplayTopology is null)
        {
            return HostCommandResult.Reject(state.Revision, "host.display.topology_unavailable");
        }

        if (assignments.Length == 0)
        {
            return HostCommandResult.Reject(state.Revision, "host.assignment.empty");
        }

        int surfaceCount;
        try
        {
            surfaceCount = coordinator.GetPlannedSurfaceCount(assignments, state.DisplayTopology);
        }
        catch (LayoutPlanningException exception)
        {
            return HostCommandResult.Reject(state.Revision, exception.ErrorCode);
        }
        if (surfaceCount == 0)
        {
            return HostCommandResult.Reject(state.Revision, "host.layout.empty");
        }

        if (state.Generation > 0)
        {
            CompleteSupersededApply(state.Generation);
        }
        long generation = state.BeginGeneration();
        pendingApplyAssignments = assignments;
        ApplyGenerationOperation operation = new(
            generation,
            surfaceCount,
            coordinator.GetApplyDeadline(surfaceCount));
        operation.BeginPhase(ApplyGenerationPhase.TopologyReady);
        if (!applyOperations.TryAdd(generation, operation))
        {
            throw new InvalidOperationException($"Apply generation {generation} already exists.");
        }
        PruneCompletedApplyOperations(generation);

        _ = ObserveApplyPreparationAsync(
            assignments,
            state.DisplayTopology,
            generation,
            operation,
            cancellationToken);
        return HostCommandResult.Success(state.Revision, state.Snapshot());
    }

    private async Task<HostCommandResult> CompleteApplyAsync(
        ApplyPreparedCommand command,
        CancellationToken cancellationToken)
    {
        if (IsSessionCommitBlocked(command.Generation))
        {
            CancelSessionApply(command.Generation);
            TrackRetirement(command.Prepared.Sessions, CancellationToken.None);
            return HostCommandResult.Reject(state.Revision, "host.session.presentation_blocked");
        }
        if (command.Generation != state.Generation ||
            state.DisplayTopology?.Revision != command.Prepared.DisplayTopologyRevision)
        {
            CompleteSupersededApply(command.Generation);
            TrackRetirement(command.Prepared.Sessions, CancellationToken.None);
            return HostCommandResult.Success(state.Revision);
        }

        IReadOnlyList<ActiveHostSession> replaced = state.FindSessionsReplacedBy(
            command.Prepared.Assignments);
        ApplyGenerationOperation? replacementOperation = FindApplyOperation(command.Generation);
        replacementOperation?.BeginPhase(ApplyGenerationPhase.SurfacesReplaced);
        using CancellationTokenSource replacementDeadline =
            replacementOperation is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    replacementOperation.DeadlineToken);
        try
        {
            await coordinator.ReplaceSurfacesAsync(
                    command.Prepared,
                    replaced.Select(session => session.Prepared).ToArray(),
                    replacementDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ApplyGenerationOperation? operation = FindApplyOperation(command.Generation);
            bool timedOut = exception is OperationCanceledException &&
                replacementOperation?.DeadlineToken.IsCancellationRequested == true &&
                !cancellationToken.IsCancellationRequested;
            operation?.Record(
                ApplyGenerationPhase.SurfacesReplaced,
                command.Prepared.Assignments.Select(assignment => assignment.DisplayId).ToArray(),
                outcome: timedOut ? ApplyStageOutcome.Timeout : ApplyStageOutcome.Rejected,
                failureReason: timedOut
                    ? ApplyFailureReason.DeadlineExceeded
                    : ApplyFailureReason.ReplaceFailed,
                detail: exception.Message);
            operation?.Fail(
                ApplyGenerationPhase.SurfacesReplaced,
                timedOut ? ApplyFailureReason.DeadlineExceeded : ApplyFailureReason.ReplaceFailed,
                exception.Message);
            try
            {
                _ = await coordinator.RetireAsync(
                        command.Prepared.Sessions,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            return HostCommandResult.Reject(state.Revision, "host.desktop.replace_failed");
        }

        IReadOnlyList<ActiveHostSession> retired = state.ReplacePreparedSessions(command.Prepared);
        pendingApplyAssignments = null;
        ApplyPlaybackPolicy(null, cancellationToken);
        ApplyGenerationOperation? completedOperation = FindApplyOperation(command.Generation);
        completedOperation?.Record(
            ApplyGenerationPhase.SurfacesReplaced,
            command.Prepared.Assignments.Select(assignment => assignment.DisplayId).ToArray());
        completedOperation?.Succeed();
        desktopRecoveryCircuitOpen = false;
        TrackRetirement(
            retired.Select(session => session.Prepared).ToArray(),
            CancellationToken.None);
        return HostCommandResult.Success(state.Revision);
    }

    private HostCommandResult FailApply(ApplyPreparationFailedCommand command)
    {
        if (command.Generation != state.Generation)
        {
            return HostCommandResult.Success(state.Revision);
        }

        FindApplyOperation(command.Generation)?.Fail(
            command.Phase,
            command.FailureReason,
            command.Message,
            command.TerminalState);
        pendingApplyAssignments = null;
        return HostCommandResult.Reject(state.Revision, command.ErrorCode);
    }

    private HostCommandResult BeginDesktopRecovery(CancellationToken cancellationToken)
    {
        if (state.SystemState.IsSessionDisconnected)
        {
            deferredSessionShellChange = true;
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }
        if (state.DisplayTopology is null)
        {
            return HostCommandResult.Reject(state.Revision, "host.display.topology_unavailable");
        }

        if (desktopRecoveryCircuitOpen)
        {
            return HostCommandResult.Reject(
                state.Revision,
                "host.desktop.recovery_circuit_open");
        }

        if (desktopRecoveryInProgress)
        {
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }

        // A confirmed Shell change invalidates every uncommitted plan, even if it
        // already produced FirstFrame. Only the recovery transaction may now commit.
        foreach ((long pendingGeneration, ApplyGenerationOperation operation) in applyOperations)
        {
            if (!operation.Completion.IsCompleted)
                CompleteSupersededApply(pendingGeneration);
        }
        if (activeDisplayReconciliationOperation is not null)
        {
            activeDisplayReconciliationOperation.Complete(
                DisplayReconciliationTerminalState.Cancelled,
                activeDisplayReconciliationGeneration,
                ApplyFailureReason.Superseded,
                "Display preparation was superseded by a confirmed Shell generation change.");
            activeDisplayReconciliationOperation = null;
            activeDisplayReconciliationGeneration = null;
        }

        desktopRecoveryInProgress = true;
        long recoveryId = checked(state.Generation + 1);
        DesktopRecoveryOperation recoveryOperation = new(recoveryId);
        if (!recoveryOperations.TryAdd(recoveryId, recoveryOperation))
        {
            throw new InvalidOperationException($"Desktop recovery {recoveryId} already exists.");
        }
        PruneCompletedRecoveryOperations(recoveryId);
        activeRecoveryOperation = recoveryOperation;
        if (state.GetPreparedSessions().Count == 0)
        {
            return StartRecoveryRebuild(attempt: 1, cancellationToken);
        }

        long generation = state.BeginGeneration();
        recoveryOperation.Record(
            generation,
            0,
            DesktopRecoveryPhase.ShellGenerationObserved);
        _ = ObserveDesktopRecoveryAsync(
            state.GetPreparedSessions().OrderBy(session => session.SurfaceOrdinal).ToArray(),
            state.DisplayTopology,
            generation,
            (activeRecoveryPreparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)).Token);
        return HostCommandResult.Success(state.Revision, state.Snapshot());
    }

    private async Task<HostCommandResult> CompleteDesktopRecoveryAsync(
        DesktopRecoveryPreparedCommand command,
        CancellationToken cancellationToken)
    {
        PreparedRecovery recovery = command.Recovery;
        if (command.Generation != state.Generation)
        {
            await coordinator.AbandonRecoveryAsync(recovery, CancellationToken.None).ConfigureAwait(false);
            CompleteSupersededRecovery(command.Generation);
            return HostCommandResult.Success(state.Revision);
        }
        if (IsSessionCommitBlocked(command.Generation))
        {
            state.InvalidateAllSessions();
            await coordinator.AbandonRecoveryAsync(recovery, CancellationToken.None)
                .ConfigureAwait(false);
            desktopRecoveryInProgress = false;
            deferredSessionShellChange = true;
            CompleteActiveRecovery(command.Generation, DesktopRecoveryTerminalState.Cancelled,
                null, ApplyFailureReason.SessionUnavailable, "Session disconnected before recovery commit.");
            return HostCommandResult.Reject(state.Revision, "host.session.presentation_blocked");
        }
        if (state.DisplayTopology?.Revision != recovery.DisplayTopologyRevision)
        {
            return await AbandonRecoveryAndScheduleRebuildAsync(
                    recovery,
                    command.Generation,
                    "host.desktop.recovery_topology_stale",
                    cancellationToken)
                .ConfigureAwait(false);
        }


        if (!state.CanReplaceRecoveredSessions(recovery))
        {
            return await AbandonRecoveryAndScheduleRebuildAsync(
                    recovery,
                    command.Generation,
                    "host.desktop.recovery_stale",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            if (recovery.Sessions.Count > 0)
            {
                await coordinator.ReplaceRecoveredSurfacesAsync(recovery, cancellationToken)
                    .ConfigureAwait(false);
            }
            activeRecoveryOperation?.Record(
                command.Generation,
                0,
                DesktopRecoveryPhase.SurfacesReplaced,
                recovery.Sessions.SelectMany(item => item.Replacement.Session.Displays).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await AbandonRecoveryAndScheduleRebuildAsync(
                    recovery,
                    command.Generation,
                    "host.desktop.recovery_replace_failed",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!state.ReplaceRecoveredSessions(recovery))
        {
            throw new InvalidOperationException(
                "Desktop recovery state changed while the single-writer command was committing.");
        }
        ApplyPlaybackPolicy(null, cancellationToken);

        PreparedSurface[] retired = recovery.Sessions
            .SelectMany(session => session.Previous.Surfaces)
            .ToArray();
        try
        {
            await coordinator.AbandonSurfacesAsync(retired, CancellationToken.None)
                .ConfigureAwait(false);
            activeRecoveryOperation?.Record(
                command.Generation,
                0,
                DesktopRecoveryPhase.CleanupVerified,
                recovery.Sessions.SelectMany(item => item.Replacement.Session.Displays).ToArray());
        }
        catch (Exception exception)
        {
            activeRecoveryOperation?.Record(
                command.Generation,
                0,
                DesktopRecoveryPhase.CleanupVerified,
                outcome: ApplyStageOutcome.Rejected,
                failureReason: ApplyFailureReason.CleanupFailed,
                detail: exception.Message);
            CompleteActiveRecovery(
                command.Generation,
                DesktopRecoveryTerminalState.Failed,
                null,
                ApplyFailureReason.CleanupFailed,
                exception.Message);
            desktopRecoveryCircuitOpen = true;
            return HostCommandResult.Reject(
                state.Revision,
                "host.desktop.recovery_cleanup_failed");
        }
        desktopRecoveryInProgress = false;
        CompleteActiveRecovery(
            command.Generation,
            DesktopRecoveryTerminalState.Succeeded,
            DesktopRecoveryCompletionMode.Reattached);
        return HostCommandResult.Success(state.Revision, state.Snapshot());
    }

    private async Task<HostCommandResult> AbandonRecoveryAndScheduleRebuildAsync(
        PreparedRecovery recovery,
        long failedGeneration,
        string errorCode,
        CancellationToken cancellationToken)
    {
        state.InvalidateAllSessions();
        try
        {
            await coordinator.AbandonRecoveryAsync(recovery, CancellationToken.None)
                .ConfigureAwait(false);
            activeRecoveryOperation?.Record(
                failedGeneration,
                0,
                DesktopRecoveryPhase.BatchAbandoned);
        }
        catch
        {
            desktopRecoveryInProgress = false;
            desktopRecoveryCircuitOpen = true;
            CompleteActiveRecovery(
                failedGeneration,
                DesktopRecoveryTerminalState.Failed,
                null,
                ApplyFailureReason.CleanupFailed,
                "Desktop recovery batch abandonment failed.");
            return HostCommandResult.Reject(
                state.Revision,
                "host.desktop.recovery_cleanup_failed");
        }

        ScheduleRecoveryRebuild(failedGeneration, attempt: 1, cancellationToken);
        return HostCommandResult.Reject(state.Revision, errorCode);
    }

    private HostCommandResult FailDesktopRecovery(DesktopRecoveryFailedCommand command)
    {
        if (command.Generation != state.Generation)
        {
            CompleteSupersededRecovery(command.Generation);
            return HostCommandResult.Success(state.Revision);
        }

        state.InvalidateAllSessions();
        if (IsSessionCommitBlocked(command.Generation))
        {
            deferredSessionShellChange = true;
            desktopRecoveryInProgress = false;
            CompleteActiveRecovery(command.Generation, DesktopRecoveryTerminalState.Cancelled,
                null, ApplyFailureReason.SessionUnavailable, "Session interrupted recovery preparation.");
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }
        desktopRecoveryInProgress = true;
        ScheduleRecoveryRebuild(command.Generation, attempt: 1, shutdown.Token);
        return HostCommandResult.Reject(state.Revision, command.ErrorCode);
    }

    private HostCommandResult BeginRecoveryRebuild(
        RetryDesktopRecoveryCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ExpectedGeneration != state.Generation ||
            state.GetPreparedSessions().Count > 0)
        {
            if (command.ExpectedGeneration != state.Generation)
                CompleteSupersededRecovery(command.ExpectedGeneration);
            return HostCommandResult.Success(state.Revision);
        }

        if (!desktopRecoveryInProgress || activeRecoveryOperation is null)
            return HostCommandResult.Success(state.Revision);

        return StartRecoveryRebuild(command.Attempt, cancellationToken);
    }

    private HostCommandResult StartRecoveryRebuild(
        int attempt,
        CancellationToken cancellationToken)
    {
        recoveryTopologyDirty = false;
        if (state.SystemState.IsSessionDisconnected)
        {
            deferredSessionShellChange = true;
            desktopRecoveryInProgress = false;
            CompleteActiveRecovery(state.Generation, DesktopRecoveryTerminalState.Cancelled,
                null, ApplyFailureReason.SessionUnavailable, "Session disconnected before rebuild.");
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }
        if (state.DisplayTopology is null)
        {
            desktopRecoveryInProgress = false;
            CompleteActiveRecovery(
                state.Generation,
                DesktopRecoveryTerminalState.Failed,
                null,
                ApplyFailureReason.TopologyFailed,
                "Display topology was unavailable during desktop recovery rebuild.");
            return HostCommandResult.Reject(state.Revision, "host.display.topology_unavailable");
        }

        HashSet<DisplayId> connectedDisplays = state.DisplayTopology.Displays
            .Select(display => display.Id).ToHashSet();
        WallpaperAssignment[] assignments = state.GetAssignments()
            .Where(assignment => connectedDisplays.Contains(assignment.DisplayId)).ToArray();
        if (assignments.Length == 0)
        {
            desktopRecoveryInProgress = false;
            CompleteActiveRecovery(
                state.Generation,
                DesktopRecoveryTerminalState.Succeeded,
                DesktopRecoveryCompletionMode.Rebuilt);
            return HostCommandResult.Success(state.Revision);
        }

        int surfaceCount;
        try
        {
            surfaceCount = coordinator.GetPlannedSurfaceCount(assignments, state.DisplayTopology);
        }
        catch (LayoutPlanningException exception)
        {
            desktopRecoveryInProgress = false;
            CompleteActiveRecovery(state.Generation, DesktopRecoveryTerminalState.Failed, null,
                ApplyFailureReason.TopologyFailed, exception.Message);
            return HostCommandResult.Reject(state.Revision, exception.ErrorCode);
        }
        long generation = state.BeginGeneration();
        activeRecoveryOperation?.Record(
            generation,
            attempt,
            DesktopRecoveryPhase.RebuildStarted);
        ApplyGenerationOperation operation = new(
            generation,
            surfaceCount,
            coordinator.GetApplyDeadline(surfaceCount));
        operation.BeginPhase(ApplyGenerationPhase.TopologyReady);
        if (!applyOperations.TryAdd(generation, operation))
        {
            throw new InvalidOperationException($"Apply generation {generation} already exists.");
        }
        PruneCompletedApplyOperations(generation);
        _ = ObserveRecoveryRebuildAsync(
            assignments,
            state.DisplayTopology,
            generation,
            attempt,
            operation,
            cancellationToken);
        return HostCommandResult.Success(state.Revision);
    }

    private async Task<HostCommandResult> CompleteRecoveryRebuildAsync(
        DesktopRecoveryRebuildPreparedCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Generation != state.Generation)
        {
            CompleteSupersededApply(command.Generation);
            TrackRetirement(command.Prepared.Sessions, CancellationToken.None);
            CompleteSupersededRecovery(command.Generation);
            return HostCommandResult.Success(state.Revision);
        }
        if (IsSessionCommitBlocked(command.Generation))
        {
            CancelSessionApply(command.Generation);
            TrackRetirement(command.Prepared.Sessions, CancellationToken.None);
            deferredSessionShellChange = true;
            desktopRecoveryInProgress = false;
            CompleteActiveRecovery(command.Generation, DesktopRecoveryTerminalState.Cancelled,
                null, ApplyFailureReason.SessionUnavailable, "Session disconnected before rebuild commit.");
            return HostCommandResult.Reject(state.Revision, "host.session.presentation_blocked");
        }
        if (state.DisplayTopology?.Revision != command.Prepared.DisplayTopologyRevision)
        {
            CompleteSupersededApply(command.Generation);
            SessionRetirementBatchResult cleanup = await ObserveRetirementAsync(
                command.Prepared.Sessions, CancellationToken.None).ConfigureAwait(false);
            if (command.Generation == state.Generation && activeRecoveryOperation is not null)
            {
                if (!cleanup.OwnedResourcesCleaned)
                {
                    desktopRecoveryInProgress = false;
                    desktopRecoveryCircuitOpen = true;
                    CompleteActiveRecovery(command.Generation, DesktopRecoveryTerminalState.Failed,
                        null, ApplyFailureReason.CleanupFailed, "Superseded recovery batch cleanup failed.");
                }
                else ScheduleRecoveryRebuild(command.Generation, 1, cancellationToken);
            }
            return HostCommandResult.Success(state.Revision);
        }

        try
        {
            await coordinator.ReplaceSurfacesAsync(command.Prepared, [], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ApplyGenerationOperation? operation = FindApplyOperation(command.Generation);
            operation?.Record(
                ApplyGenerationPhase.SurfacesReplaced,
                command.Prepared.Assignments.Select(assignment => assignment.DisplayId).ToArray(),
                outcome: ApplyStageOutcome.Rejected,
                failureReason: ApplyFailureReason.ReplaceFailed,
                detail: exception.Message);
            try
            {
                _ = await coordinator.RetireAsync(
                        command.Prepared.Sessions,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                desktopRecoveryInProgress = false;
                desktopRecoveryCircuitOpen = true;
                CompleteActiveRecovery(
                    command.Generation,
                    DesktopRecoveryTerminalState.Failed,
                    null,
                    ApplyFailureReason.CleanupFailed,
                    cleanupException.Message);
                return HostCommandResult.Reject(
                    state.Revision,
                    "host.desktop.rebuild_cleanup_failed");
            }

            return ScheduleNextRecoveryRebuild(
                command.Generation,
                command.Attempt,
                "host.desktop.rebuild_replace_failed",
                ApplyGenerationPhase.SurfacesReplaced,
                ApplyFailureReason.ReplaceFailed,
                exception.Message,
                cancellationToken);
        }

        _ = state.ReplacePreparedSessions(command.Prepared);
        ApplyPlaybackPolicy(null, cancellationToken);
        ApplyGenerationOperation? completedOperation = FindApplyOperation(command.Generation);
        completedOperation?.Record(
            ApplyGenerationPhase.SurfacesReplaced,
            command.Prepared.Assignments.Select(assignment => assignment.DisplayId).ToArray());
        completedOperation?.Succeed();
        desktopRecoveryInProgress = false;
        desktopRecoveryCircuitOpen = false;
        activeRecoveryOperation?.Record(
            command.Generation,
            command.Attempt,
            DesktopRecoveryPhase.SurfacesReplaced,
            command.Prepared.Assignments.Select(item => item.DisplayId).ToArray());
        CompleteActiveRecovery(
            command.Generation,
            DesktopRecoveryTerminalState.Succeeded,
            DesktopRecoveryCompletionMode.Rebuilt);
        return HostCommandResult.Success(state.Revision);
    }

    private HostCommandResult FailRecoveryRebuild(
        DesktopRecoveryRebuildFailedCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Generation != state.Generation)
        {
            CompleteSupersededApply(command.Generation);
            CompleteSupersededRecovery(command.Generation);
            return HostCommandResult.Success(state.Revision);
        }

        if (command.TerminalState == ApplyGenerationTerminalState.Cancelled)
        {
            if (recoveryTopologyDirty && !IsSessionCommitBlocked(command.Generation))
            {
                FindApplyOperation(command.Generation)?.Fail(command.Phase,
                    ApplyFailureReason.Superseded, "Recovery preparation was superseded by a newer topology.",
                    ApplyGenerationTerminalState.Cancelled);
                ScheduleRecoveryRebuild(command.Generation, 1, cancellationToken);
                return HostCommandResult.Success(state.Revision);
            }
            FindApplyOperation(command.Generation)?.Fail(
                command.Phase,
                command.FailureReason,
                command.Message,
                ApplyGenerationTerminalState.Cancelled);
            desktopRecoveryInProgress = false;
            CompleteActiveRecovery(
                command.Generation,
                DesktopRecoveryTerminalState.Cancelled,
                null,
                command.FailureReason,
                command.Message);
            return HostCommandResult.Reject(state.Revision, command.ErrorCode);
        }

        return ScheduleNextRecoveryRebuild(
            command.Generation,
            command.Attempt,
            command.ErrorCode,
            command.Phase,
            command.FailureReason,
            command.Message,
            cancellationToken);
    }

    private HostCommandResult ScheduleNextRecoveryRebuild(
        long failedGeneration,
        int failedAttempt,
        string finalErrorCode,
        ApplyGenerationPhase failurePhase,
        ApplyFailureReason failureReason,
        string failureDetail,
        CancellationToken cancellationToken)
    {
        int nextAttempt = checked(failedAttempt + 1);
        ApplyGenerationOperation? operation = FindApplyOperation(failedGeneration);
        if (nextAttempt > MaximumRecoveryRebuildAttempts)
        {
            operation?.Fail(
                failurePhase,
                failureReason,
                failureDetail,
                ApplyGenerationTerminalState.CircuitBroken);
            desktopRecoveryInProgress = false;
            desktopRecoveryCircuitOpen = true;
            CompleteActiveRecovery(
                failedGeneration,
                DesktopRecoveryTerminalState.CircuitBroken,
                null,
                failureReason,
                failureDetail);
            return HostCommandResult.Reject(state.Revision, finalErrorCode);
        }

        operation?.Fail(failurePhase, failureReason, failureDetail);
        ScheduleRecoveryRebuild(failedGeneration, nextAttempt, cancellationToken);
        return HostCommandResult.Reject(state.Revision, finalErrorCode);
    }

    private void ScheduleRecoveryRebuild(
        long expectedGeneration,
        int attempt,
        CancellationToken cancellationToken)
    {
        activeRecoveryOperation?.Record(
            expectedGeneration,
            attempt,
            DesktopRecoveryPhase.RebuildScheduled);
        activeRecoveryPreparation?.Dispose();
        activeRecoveryPreparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = PostRecoveryRetryAfterDelayAsync(
            expectedGeneration,
            attempt,
            activeRecoveryPreparation.Token);
    }

    private async Task<HostCommandResult> SetTopologyAsync(
        DisplayTopologyChangedCommand command,
        CancellationToken cancellationToken,
        DisplayTopology? deferredPrevious = null)
    {
        DisplayTopology? previous = deferredPrevious ?? state.DisplayTopology;
        if (previous is null)
        {
            state.SetDisplayTopology(command.Topology);
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }
        if (command.Topology.Revision <= previous.Revision)
        {
            return HostCommandResult.Reject(state.Revision, "host.display.topology_stale");
        }

        if (state.SystemState.IsSessionDisconnected)
        {
            deferredSessionTopology ??= previous;
            state.SetDisplayTopology(command.Topology);
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }

        if (desktopRecoveryInProgress && activeRecoveryOperation is not null)
        {
            // Shell invalidation takes precedence: never resize or prepare a partial
            // topology batch using uncertain old-generation Surface/Renderer resources.
            activeDisplayReconciliationOperation?.Complete(
                DisplayReconciliationTerminalState.Cancelled, state.Generation,
                ApplyFailureReason.Superseded, "A newer topology joined the Shell recovery transaction.");
            state.SetDisplayTopology(command.Topology);
            DisplayReconciliationOperation joined = new(command.Topology.Revision,
                command.Topology.Displays.Count == 0
                    ? DisplayReconciliationKind.Suspend : DisplayReconciliationKind.Reconfigure);
            joined.Record(DisplayReconciliationPhase.PlanCreated, state.Generation,
                command.Topology.Displays.Select(display => display.Id).ToArray(),
                detail: "Coalesced into the active Shell recovery; only the latest revision may commit.");
            displayReconciliationOperations.TryAdd(command.Topology.Revision, joined);
            activeDisplayReconciliationOperation = joined;
            activeDisplayReconciliationGeneration = null;
            PruneCompletedDisplayReconciliations(command.Topology.Revision);
            recoveryTopologyPending = true;
            recoveryTopologyDirty = true;
            activeRecoveryPreparation?.Cancel();
            FindApplyOperation(state.Generation)?.CancelPreparation();
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }

        // A newer accepted topology revision always owns preparation. Cancel the exact
        // generation associated with the previous reconciliation before planning the
        // new state, including when that state becomes NoChange or Suspend.
        if (activeDisplayReconciliationOperation is not null &&
            !activeDisplayReconciliationOperation.Completion.IsCompleted)
        {
            activeDisplayReconciliationOperation.Complete(
                DisplayReconciliationTerminalState.Cancelled,
                activeDisplayReconciliationGeneration,
                ApplyFailureReason.Superseded,
                "Display reconciliation was superseded by a newer topology revision.");
            if (activeDisplayReconciliationGeneration is long supersededGeneration)
            {
                CompleteSupersededApply(supersededGeneration);
            }
            activeDisplayReconciliationOperation = null;
            activeDisplayReconciliationGeneration = null;
        }

        ActiveSurfaceState[] active = state.GetPreparedSessions()
            .Where(session => session.Surfaces.Count > 0)
            .Select(session => new ActiveSurfaceState(
                session.Session.Id,
                session.Session.WallpaperId,
                session.Session.Displays,
                DisplayBounds.Union(session.Surfaces.Select(surface => surface.Request.Bounds)),
                session.Surfaces[0].Request.FitMode))
            .ToArray();
        DisplayReconciliationPlan plan;
        try
        {
            plan = displayReconciliationPlanner.CreatePlan(
                previous,
                command.Topology,
                state.GetAssignments(),
                active);
        }
        catch (LayoutPlanningException exception)
        {
            DisplayReconciliationOperation rejected = new(
                command.Topology.Revision,
                DisplayReconciliationKind.Reconfigure);
            rejected.Record(
                DisplayReconciliationPhase.PlanCreated,
                outcome: ApplyStageOutcome.Rejected,
                failureReason: ApplyFailureReason.TopologyFailed,
                detail: exception.Message);
            rejected.Complete(
                DisplayReconciliationTerminalState.Failed,
                null,
                ApplyFailureReason.TopologyFailed,
                exception.Message);
            if (!displayReconciliationOperations.TryAdd(command.Topology.Revision, rejected))
            {
                throw new InvalidOperationException(
                    $"Display reconciliation {command.Topology.Revision} already exists.");
            }
            PruneCompletedDisplayReconciliations(command.Topology.Revision);
            return HostCommandResult.Reject(state.Revision, exception.ErrorCode);
        }
        DisplayReconciliationOperation reconciliation = new(
            command.Topology.Revision,
            plan.Kind);
        reconciliation.Record(
            DisplayReconciliationPhase.PlanCreated,
            displayIds: plan.AffectedDisplayIds,
            detail: string.Join(",", plan.Reasons));
        if (!displayReconciliationOperations.TryAdd(command.Topology.Revision, reconciliation))
        {
            throw new InvalidOperationException(
                $"Display reconciliation {command.Topology.Revision} already exists.");
        }
        activeDisplayReconciliationOperation = reconciliation;
        activeDisplayReconciliationGeneration = null;
        PruneCompletedDisplayReconciliations(command.Topology.Revision);
        state.SetDisplayTopology(command.Topology);
        if (plan.Kind == DisplayReconciliationKind.NoChange)
        {
            if (plan.Reasons.Any(reason => reason is
                    DisplayTopologyChangeReason.BoundsChanged or
                    DisplayTopologyChangeReason.ScaleChanged or
                    DisplayTopologyChangeReason.VirtualDesktopOriginChanged))
            {
                await RestoreActiveSurfaceGeometryAsync(
                        plan.DesiredLayout,
                        command.Topology,
                        state.Generation,
                        reconciliation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            CompleteActiveDisplayReconciliation(null);
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }

        if (plan.SurfacesToPrepare.Count == 0)
        {
            IReadOnlyList<PreparedSession> retired = state.TakeSessions(plan.RetiredSessionIds);
            TrackRetirement(retired, CancellationToken.None);
            reconciliation.Record(
                DisplayReconciliationPhase.RetirementScheduled,
                displayIds: plan.AffectedDisplayIds);
            CompleteActiveDisplayReconciliation(null);
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }

        if (activeRecoveryOperation is not null)
        {
            desktopRecoveryInProgress = false;
            CompleteActiveRecovery(
                state.Generation,
                DesktopRecoveryTerminalState.Cancelled,
                null,
                ApplyFailureReason.Superseded,
                "Desktop recovery was superseded by a display topology revision.");
        }
        if (state.Generation > 0)
        {
            CompleteSupersededApply(state.Generation);
        }
        HashSet<DisplayId> preparedDisplayIds = plan.SurfacesToPrepare
            .SelectMany(surface => surface.DisplayIds)
            .ToHashSet();
        WallpaperAssignment[] assignments = plan.ConnectedAssignments
            .Where(assignment => preparedDisplayIds.Contains(assignment.DisplayId))
            .ToArray();
        long generation = state.BeginGeneration();
        activeDisplayReconciliationGeneration = generation;
        reconciliation.Record(
            DisplayReconciliationPhase.PreparationStarted,
            generation,
            plan.AffectedDisplayIds);
        ApplyGenerationOperation operation = new(
            generation,
            plan.SurfacesToPrepare.Count,
            coordinator.GetApplyDeadline(plan.SurfacesToPrepare.Count));
        operation.BeginPhase(ApplyGenerationPhase.TopologyReady);
        if (!applyOperations.TryAdd(generation, operation))
        {
            throw new InvalidOperationException($"Apply generation {generation} already exists.");
        }
        PruneCompletedApplyOperations(generation);
        if (plan.ActiveSurfaceResizes.Count > 0)
        {
            Dictionary<DisplayId, DisplayDescriptor> nextDisplays = command.Topology.Displays
                .ToDictionary(display => display.Id);
            ActiveSurfaceResizeRequest[] resizeRequests = plan.ActiveSurfaceResizes
                .Select(resize =>
                {
                    PreparedSession? session = state.FindPreparedSession(resize.SessionId);
                    if (session is null)
                    {
                        return null;
                    }
                    double scaleFactor = resize.Target.DisplayIds.Count == 1 &&
                        nextDisplays.TryGetValue(
                            resize.Target.DisplayIds[0],
                            out DisplayDescriptor? display)
                                ? display.ScaleFactor
                                : 1;
                    return new ActiveSurfaceResizeRequest(session, resize.Target, scaleFactor);
                })
                .Where(request => request is not null)
                .Cast<ActiveSurfaceResizeRequest>()
                .ToArray();
            try
            {
                IReadOnlyList<ActiveSurfaceResizeResult> resized = await coordinator
                    .ResizeActiveSurfacesAsync(
                        resizeRequests,
                        command.Topology,
                        generation,
                        operation.DeadlineToken)
                    .ConfigureAwait(false);
                reconciliation.Record(
                    DisplayReconciliationPhase.ActiveSurfacesResized,
                    generation,
                    resized.SelectMany(item => item.Request.DisplayIds).Distinct().ToArray(),
                    detail: $"Resized {resized.Count} active Surface(s) before replacement preparation.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                reconciliation.Record(
                    DisplayReconciliationPhase.ActiveSurfacesResized,
                    generation,
                    plan.ActiveSurfaceResizes
                        .SelectMany(item => item.Target.DisplayIds)
                        .Distinct()
                        .ToArray(),
                    ApplyStageOutcome.Rejected,
                    ApplyFailureReason.TopologyFailed,
                    $"Active transition resize was skipped: {exception.Message}");
                reconciliation.Record(
                    DisplayReconciliationPhase.PreparationRetryScheduled,
                    generation,
                    plan.ActiveSurfaceResizes
                        .SelectMany(item => item.Target.DisplayIds)
                        .Distinct()
                        .ToArray(),
                    ApplyStageOutcome.Rejected,
                    ApplyFailureReason.TopologyFailed,
                    "Authoritative preparation will retry Shell topology discovery immediately.");
                reconciliation.Record(
                    DisplayReconciliationPhase.PreparationRetryStarted,
                    generation,
                    plan.ActiveSurfaceResizes
                        .SelectMany(item => item.Target.DisplayIds)
                        .Distinct()
                        .ToArray(),
                    detail: "Authoritative preparation Shell topology retry started.");
            }
        }
        _ = ObserveDisplayReconciliationAsync(
            assignments,
            command.Topology,
            generation,
            plan.RetiredSessionIds,
            operation,
            cancellationToken);
        return HostCommandResult.Success(state.Revision, state.Snapshot());
    }

    private async Task RestoreActiveSurfaceGeometryAsync(
        LayoutPlan desiredLayout,
        DisplayTopology topology,
        long generation,
        DisplayReconciliationOperation reconciliation,
        CancellationToken cancellationToken)
    {
        Dictionary<DisplayId, DisplayDescriptor> displays = topology.Displays
            .ToDictionary(display => display.Id);
        List<ActiveSurfaceResizeRequest> requests = [];
        foreach (PreparedSession session in state.GetPreparedSessions())
        {
            PlannedSurface? target = desiredLayout.Surfaces.FirstOrDefault(surface =>
                surface.WallpaperId == session.Session.WallpaperId &&
                surface.DisplayIds.Count == session.Session.Displays.Count &&
                !surface.DisplayIds.Except(session.Session.Displays).Any());
            if (target is null)
            {
                continue;
            }
            double scaleFactor = target.DisplayIds.Count == 1 &&
                displays.TryGetValue(target.DisplayIds[0], out DisplayDescriptor? display)
                    ? display.ScaleFactor
                    : 1;
            requests.Add(new ActiveSurfaceResizeRequest(session, target, scaleFactor));
        }

        try
        {
            IReadOnlyList<ActiveSurfaceResizeResult> resized = await coordinator
                .ResizeActiveSurfacesAsync(
                    requests,
                    topology,
                    generation,
                    cancellationToken)
                .ConfigureAwait(false);
            reconciliation.Record(
                DisplayReconciliationPhase.ActiveSurfacesResized,
                generation,
                resized.SelectMany(item => item.Request.DisplayIds).Distinct().ToArray(),
                detail: $"Restored {resized.Count} active Surface(s) to the final no-change topology.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reconciliation.Record(
                DisplayReconciliationPhase.ActiveSurfacesResized,
                generation,
                requests.SelectMany(item => item.Target.DisplayIds).Distinct().ToArray(),
                ApplyStageOutcome.Rejected,
                ApplyFailureReason.TopologyFailed,
                $"No-change transition geometry restore was skipped: {exception.Message}");
        }
    }

    private async Task<HostCommandResult> CompleteDisplayReconciliationAsync(
        DisplayReconciliationPreparedCommand command,
        CancellationToken cancellationToken)
    {
        if (IsSessionCommitBlocked(command.Generation))
        {
            CancelSessionApply(command.Generation);
            TrackRetirement(command.Prepared.Sessions, CancellationToken.None);
            CompleteDisplayReconciliation(command.Prepared.DisplayTopologyRevision,
                DisplayReconciliationTerminalState.Cancelled, command.Generation,
                ApplyFailureReason.SessionUnavailable, "Session disconnected before topology commit.");
            return HostCommandResult.Reject(state.Revision, "host.session.presentation_blocked");
        }
        if (command.Generation != state.Generation ||
            state.DisplayTopology?.Revision != command.Prepared.DisplayTopologyRevision)
        {
            CompleteSupersededApply(command.Generation);
            TrackRetirement(command.Prepared.Sessions, CancellationToken.None);
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }

        HashSet<SessionId> retiredIds = command.RetiredSessionIds.ToHashSet();
        PreparedSession[] replaced = state.GetPreparedSessions()
            .Where(session => retiredIds.Contains(session.Session.Id) ||
                session.Session.Displays.Any(display =>
                    command.Prepared.Assignments.Any(assignment =>
                        assignment.DisplayId == display)))
            .ToArray();
        ApplyGenerationOperation? operation = FindApplyOperation(command.Generation);
        operation?.BeginPhase(ApplyGenerationPhase.SurfacesReplaced);
        try
        {
            await coordinator.ReplaceSurfacesAsync(
                    command.Prepared,
                    replaced,
                    operation?.DeadlineToken ?? cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            IsTransientShellTopologyMutation(exception) &&
            command.ReplacementAttempt < DisplayReconciliationRetryDelays.Length)
        {
            operation?.Record(
                ApplyGenerationPhase.SurfacesReplaced,
                command.Prepared.Assignments.Select(item => item.DisplayId).ToArray(),
                outcome: ApplyStageOutcome.Rejected,
                failureReason: ApplyFailureReason.TopologyFailed,
                detail: exception.Message);
            SessionRetirementBatchResult abandoned = await coordinator.RetireAsync(
                    command.Prepared.Sessions,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!abandoned.OwnedResourcesCleaned)
            {
                const string cleanupDetail =
                    "Transient topology replacement could not clean all provisional resources.";
                operation?.Fail(
                    ApplyGenerationPhase.CleanupVerified,
                    ApplyFailureReason.CleanupFailed,
                    cleanupDetail);
                CompleteDisplayReconciliation(
                    command.Prepared.DisplayTopologyRevision,
                    DisplayReconciliationTerminalState.Failed,
                    command.Generation,
                    ApplyFailureReason.CleanupFailed,
                    cleanupDetail);
                return HostCommandResult.Reject(
                    state.Revision,
                    "host.display.reconcile_retry_cleanup_failed");
            }

            TimeSpan delay = DisplayReconciliationRetryDelays[command.ReplacementAttempt];
            if (displayReconciliationOperations.TryGetValue(
                    command.Prepared.DisplayTopologyRevision,
                    out DisplayReconciliationOperation? retryReconciliation))
            {
                retryReconciliation.Record(
                    DisplayReconciliationPhase.PreparationRetryScheduled,
                    command.Generation,
                    command.Prepared.Assignments.Select(item => item.DisplayId).ToArray(),
                    ApplyStageOutcome.Rejected,
                    ApplyFailureReason.TopologyFailed,
                    $"Shell coordinate space changed before Replace; retry " +
                    $"{command.ReplacementAttempt + 1} scheduled after {delay}. {exception.Message}");
            }
            _ = RetryDisplayReconciliationAfterReplaceAsync(
                command.Prepared.Assignments,
                state.DisplayTopology!,
                command.Generation,
                command.RetiredSessionIds,
                operation!,
                command.ReplacementAttempt,
                cancellationToken);
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }
        catch (Exception exception)
        {
            operation?.Record(
                ApplyGenerationPhase.SurfacesReplaced,
                command.Prepared.Assignments.Select(item => item.DisplayId).ToArray(),
                outcome: ApplyStageOutcome.Rejected,
                failureReason: ApplyFailureReason.ReplaceFailed,
                detail: exception.Message);
            operation?.Fail(
                ApplyGenerationPhase.SurfacesReplaced,
                ApplyFailureReason.ReplaceFailed,
                exception.Message);
            TrackRetirement(command.Prepared.Sessions, CancellationToken.None);
            CompleteDisplayReconciliation(
                command.Prepared.DisplayTopologyRevision,
                DisplayReconciliationTerminalState.Failed,
                command.Generation,
                ApplyFailureReason.ReplaceFailed,
                exception.Message);
            return HostCommandResult.Reject(state.Revision, "host.display.reconcile_replace_failed");
        }

        IReadOnlyList<PreparedSession> retired = state.CommitDisplayReconciliation(
            command.Prepared,
            command.RetiredSessionIds);
        ApplyPlaybackPolicy(null, cancellationToken);
        operation?.Record(
            ApplyGenerationPhase.SurfacesReplaced,
            command.Prepared.Assignments.Select(item => item.DisplayId).ToArray());
        operation?.Succeed();
        if (displayReconciliationOperations.TryGetValue(
                command.Prepared.DisplayTopologyRevision,
                out DisplayReconciliationOperation? reconciliation))
        {
            reconciliation.Record(
                DisplayReconciliationPhase.SurfacesReplaced,
                command.Generation,
                command.Prepared.Assignments.Select(item => item.DisplayId).ToArray());
            reconciliation.Record(
                DisplayReconciliationPhase.RetirementScheduled,
                command.Generation,
                retired.SelectMany(item => item.Session.Displays).Distinct().ToArray());
        }
        TrackRetirement(retired, CancellationToken.None);
        CompleteDisplayReconciliation(
            command.Prepared.DisplayTopologyRevision,
            DisplayReconciliationTerminalState.Succeeded,
            command.Generation);
        return HostCommandResult.Success(state.Revision, state.Snapshot());
    }

    private HostCommandResult FailDisplayReconciliation(
        DisplayReconciliationFailedCommand command)
    {
        if (command.Generation == state.Generation)
        {
            FindApplyOperation(command.Generation)?.Fail(
                command.Phase,
                command.FailureReason,
                command.Message);
        }
        CompleteDisplayReconciliation(
            command.TopologyRevision,
            DisplayReconciliationTerminalState.Failed,
            command.Generation,
            command.FailureReason,
            command.Message);
        return HostCommandResult.Reject(state.Revision, command.ErrorCode);
    }

    private async Task<HostCommandResult> SetSystemStateAsync(
        SystemStateChangedCommand command,
        CancellationToken cancellationToken)
    {
        bool wasDisconnected = state.SystemState.IsSessionDisconnected;
        bool snapshotChanged = state.SystemState != command.Change.Snapshot;
        if (!state.TrySetSystemState(
                command.Change.Revision,
                command.Change.Snapshot,
                command.Change.Reason))
        {
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }
        if (snapshotChanged && !(wasDisconnected && !state.SystemState.IsSessionDisconnected))
        {
            ApplyPlaybackPolicy(null, cancellationToken);
        }
        if (state.SystemState.IsSessionDisconnected &&
            (!wasDisconnected || command.Change.Reason == SystemStateChangeReason.SessionDisconnected))
        {
            activeSessionEnvironmentOperation?.Complete(SessionEnvironmentTerminalState.Cancelled,
                reason: SessionEnvironmentFailureReason.SessionDisconnected);
            deferredSessionTopology ??= state.DisplayTopology;
            foreach ((long generation, ApplyGenerationOperation operation) in applyOperations)
            {
                if (!operation.Completion.IsCompleted)
                {
                    sessionBlockedGenerations.Add(generation);
                    CancelSessionApply(generation);
                }
            }
            if (state.GetAssignments().Count == 0 && pendingApplyAssignments is not null)
            {
                state.RestoreAssignments(pendingApplyAssignments);
            }
            if (desktopRecoveryInProgress)
            {
                sessionBlockedGenerations.Add(state.Generation);
                deferredSessionShellChange = true;
                activeRecoveryPreparation?.Cancel();
            }
            if (activeDisplayReconciliationOperation is not null)
            {
                deferredSessionShellChange = true;
                CompleteDisplayReconciliation(activeDisplayReconciliationOperation.TopologyRevision,
                    DisplayReconciliationTerminalState.Cancelled,
                    activeDisplayReconciliationGeneration, ApplyFailureReason.SessionUnavailable,
                    "Session disconnected during topology preparation.");
            }
        }
        else if (wasDisconnected && !state.SystemState.IsSessionDisconnected)
        {
            if (activeSessionEnvironmentOperation is null ||
                activeSessionEnvironmentOperation.Completion.IsCompleted)
            {
                BeginSessionEnvironmentOperation(command.Change.Revision);
            }
            if (desktopRecoveryInProgress && activeRecoveryOperation is not null)
            {
                _ = ContinueReconnectAfterRecoveryAsync(activeRecoveryOperation.Completion,
                    activeSessionEnvironmentOperation!.ValidationRevision);
                return HostCommandResult.Success(state.Revision, state.Snapshot());
            }
            return await ContinueSessionReconnectAsync(
                new ContinueSessionReconnectCommand(activeSessionEnvironmentOperation!.ValidationRevision),
                cancellationToken).ConfigureAwait(false);
        }
        if (command.Change.Reason == SystemStateChangeReason.SessionReconnectRequested)
        {
            BeginSessionEnvironmentOperation(command.Change.Revision);
        }
        return HostCommandResult.Success(state.Revision, state.Snapshot());
    }

    private bool IsSessionCommitBlocked(long generation) =>
        state.SystemState.IsSessionDisconnected || sessionBlockedGenerations.Contains(generation);

    private void CancelSessionApply(long generation)
    {
        ApplyGenerationOperation? operation = FindApplyOperation(generation);
        if (operation is null || operation.Completion.IsCompleted)
        {
            return;
        }
        operation.CancelPreparation();
        operation.Fail(operation.CurrentPhase, ApplyFailureReason.SessionUnavailable,
            "Session disconnected before presentation commit.", ApplyGenerationTerminalState.Cancelled);
    }

    private void BeginSessionEnvironmentOperation(long revision)
    {
        activeSessionEnvironmentOperation?.Complete(SessionEnvironmentTerminalState.Cancelled,
            reason: SessionEnvironmentFailureReason.Superseded);
        SessionEnvironmentOperation operation = new(revision);
        sessionEnvironmentOperations[revision] = operation;
        activeSessionEnvironmentOperation = operation;
        foreach (long stale in sessionEnvironmentOperations
                     .Where(item => item.Value.Completion.IsCompleted)
                     .OrderByDescending(item => item.Key).Skip(31).Select(item => item.Key).ToArray())
        {
            sessionEnvironmentOperations.TryRemove(stale, out _);
        }
    }

    private async Task ContinueReconnectAfterRecoveryAsync(Task<DesktopRecoveryResult> recovery, long revision)
    {
        try
        {
            await recovery.WaitAsync(shutdown.Token).ConfigureAwait(false);
            await PostInternalAsync(new ContinueSessionReconnectCommand(revision), shutdown.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task<HostCommandResult> ContinueSessionReconnectAsync(
        ContinueSessionReconnectCommand command, CancellationToken cancellationToken)
    {
        if (state.SystemState.IsSessionDisconnected ||
            activeSessionEnvironmentOperation?.ValidationRevision != command.SystemRevision ||
            activeSessionEnvironmentOperation.Completion.IsCompleted)
        {
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }
        DisplayTopology? previous = deferredSessionTopology;
        deferredSessionTopology = null;
        if (state.DisplayTopology?.Displays.Count == 0)
        {
            deferredSessionShellChange = false;
            TrackRetirement(state.InvalidateAllSessions(), CancellationToken.None);
            activeSessionEnvironmentOperation.Complete(SessionEnvironmentTerminalState.Succeeded,
                SessionEnvironmentPath.Reconfigure);
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }
        if (deferredSessionShellChange || state.GetPreparedSessions().Count == 0)
        {
            deferredSessionShellChange = false;
            TrackRetirement(state.InvalidateAllSessions(), CancellationToken.None);
            desktopRecoveryInProgress = false;
            long recoveryId = checked(state.Generation + 1);
            HostCommandResult accepted = BeginDesktopRecovery(cancellationToken);
            if (!accepted.Accepted ||
                !recoveryOperations.TryGetValue(recoveryId, out DesktopRecoveryOperation? recovery))
            {
                activeSessionEnvironmentOperation.Complete(SessionEnvironmentTerminalState.Failed,
                    SessionEnvironmentPath.Rebuild, SessionEnvironmentFailureReason.RecoveryRejected);
            }
            else
            {
                _ = ObserveSessionRecoveryAsync(recovery.Completion, command.SystemRevision);
            }
            return accepted;
        }
        if (previous is not null && state.DisplayTopology is not null &&
            previous.Revision != state.DisplayTopology.Revision)
        {
            long revision = state.DisplayTopology.Revision;
            HostCommandResult accepted = await SetTopologyAsync(
                new DisplayTopologyChangedCommand(state.DisplayTopology), cancellationToken, previous)
                .ConfigureAwait(false);
            if (displayReconciliationOperations.TryGetValue(revision, out DisplayReconciliationOperation? operation))
            {
                if (operation.Kind == DisplayReconciliationKind.NoChange)
                {
                    // The topology revision changed, but no Surface transaction was
                    // necessary. Releasing the session gate must still restore policy.
                    ApplyPlaybackPolicy(null, cancellationToken);
                }
                _ = ObserveSessionReconciliationAsync(operation.Completion, command.SystemRevision);
            }
            else
            {
                activeSessionEnvironmentOperation.Complete(SessionEnvironmentTerminalState.Failed,
                    SessionEnvironmentPath.Reconfigure, SessionEnvironmentFailureReason.ReconciliationRejected);
            }
            return accepted;
        }
        ApplyPlaybackPolicy(null, cancellationToken);
        activeSessionEnvironmentOperation.Complete(SessionEnvironmentTerminalState.Succeeded,
            SessionEnvironmentPath.Resume);
        return HostCommandResult.Success(state.Revision, state.Snapshot());
    }

    private async Task ObserveSessionRecoveryAsync(Task<DesktopRecoveryResult> pending, long revision)
    {
        DesktopRecoveryResult result = await pending.ConfigureAwait(false);
        if (!TryPost(new SessionEnvironmentCompletedCommand(revision,
            SessionEnvironmentPath.Rebuild, result.TerminalState == DesktopRecoveryTerminalState.Succeeded,
            result.TerminalState == DesktopRecoveryTerminalState.Succeeded
                ? SessionEnvironmentFailureReason.None : SessionEnvironmentFailureReason.RecoveryFailed)))
        {
            sessionEnvironmentOperations[revision].Complete(SessionEnvironmentTerminalState.Cancelled,
                reason: SessionEnvironmentFailureReason.HostStopping);
        }
    }

    private async Task ObserveSessionReconciliationAsync(Task<DisplayReconciliationResult> pending, long revision)
    {
        DisplayReconciliationResult result = await pending.ConfigureAwait(false);
        if (!TryPost(new SessionEnvironmentCompletedCommand(revision,
            result.Kind == DisplayReconciliationKind.NoChange ? SessionEnvironmentPath.Resume : SessionEnvironmentPath.Reconfigure,
            result.TerminalState == DisplayReconciliationTerminalState.Succeeded,
            result.TerminalState == DisplayReconciliationTerminalState.Succeeded
                ? SessionEnvironmentFailureReason.None : SessionEnvironmentFailureReason.ReconciliationFailed)))
        {
            sessionEnvironmentOperations[revision].Complete(SessionEnvironmentTerminalState.Cancelled,
                reason: SessionEnvironmentFailureReason.HostStopping);
        }
    }

    private HostCommandResult CompleteSessionEnvironment(SessionEnvironmentCompletedCommand command)
    {
        if (!state.SystemState.IsSessionDisconnected &&
            activeSessionEnvironmentOperation?.ValidationRevision == command.SystemRevision)
        {
            activeSessionEnvironmentOperation.Complete(command.Succeeded
                ? SessionEnvironmentTerminalState.Succeeded : SessionEnvironmentTerminalState.Failed,
                command.Path, command.Reason);
        }
        return HostCommandResult.Success(state.Revision, state.Snapshot());
    }

    private HostCommandResult CompleteSessionEnvironmentFailure(SessionEnvironmentFailedCommand command)
    {
        if (state.SystemStateRevision == command.SystemRevision)
        {
            activeSessionEnvironmentOperation?.Complete(SessionEnvironmentTerminalState.Failed,
                reason: command.Reason);
        }
        return HostCommandResult.Success(state.Revision, state.Snapshot());
    }

    private HostCommandResult SetPlaybackPolicy(
        SetPlaybackPolicyCommand command,
        CancellationToken cancellationToken)
    {
        state.SetPlaybackPolicy(command.Policy);
        ApplyPlaybackPolicy(null, cancellationToken);
        return HostCommandResult.Success(state.Revision);
    }

    private HostCommandResult ChangePlaybackIntent(
        ChangePlaybackIntentCommand command,
        CancellationToken cancellationToken)
    {
        state.ChangePlaybackIntent(command.Intent, command.SessionId);
        ApplyPlaybackPolicy(command.SessionId, cancellationToken);
        return HostCommandResult.Success(state.Revision);
    }

    private HostCommandResult HandleRendererEvent(RendererEventCommand command)
    {
        if (command.Generation != command.RendererEvent.Generation)
        {
            return HostCommandResult.Success(state.Revision);
        }

        PlaybackState? playbackState = command.RendererEvent switch
        {
            RendererPlaybackStateChanged changed => changed.State,
            RendererFailed => PlaybackState.Faulted,
            _ => null,
        };

        if (playbackState is not null)
        {
            state.UpdateSession(command.SessionId, command.Generation, playbackState.Value);
        }

        return HostCommandResult.Success(state.Revision);
    }

    private HostCommandResult HandleRendererExit(RendererExitedCommand command)
    {
        state.UpdateSession(command.SessionId, command.Generation, PlaybackState.Faulted);
        return HostCommandResult.Success(state.Revision);
    }

    private HostCommandResult RequestShutdown(CancellationToken cancellationToken)
    {
        activeSessionEnvironmentOperation?.Complete(SessionEnvironmentTerminalState.Cancelled,
            reason: SessionEnvironmentFailureReason.HostStopping);
        stopping = true;
        if (!state.RequestShutdown())
        {
            return HostCommandResult.Success(state.Revision, state.Snapshot());
        }

        foreach ((long generation, ApplyGenerationOperation operation) in applyOperations)
        {
            if (!operation.Completion.IsCompleted)
            {
                operation.CancelPreparation();
                operation.Fail(
                    operation.CurrentPhase,
                    ApplyFailureReason.Cancelled,
                    $"Apply generation {generation} was cancelled because the Host is stopping.",
                    ApplyGenerationTerminalState.Cancelled);
            }
        }
        if (activeDisplayReconciliationOperation is not null &&
            !activeDisplayReconciliationOperation.Completion.IsCompleted)
        {
            activeDisplayReconciliationOperation.Complete(
                DisplayReconciliationTerminalState.Cancelled,
                activeDisplayReconciliationGeneration,
                ApplyFailureReason.Cancelled,
                "Display reconciliation was cancelled because the Host is stopping.");
        }
        activeDisplayReconciliationOperation = null;
        activeDisplayReconciliationGeneration = null;
        if (activeRecoveryOperation is not null)
        {
            CompleteActiveRecovery(
                state.Generation,
                DesktopRecoveryTerminalState.Cancelled,
                null,
                ApplyFailureReason.Cancelled,
                "Desktop recovery was cancelled because the Host is stopping.");
        }
        desktopRecoveryInProgress = false;
        PreparedSession[] sessions = state.InvalidateAllSessions().ToArray();
        TrackRetirement(sessions, CancellationToken.None);
        return HostCommandResult.Success(state.Revision, state.Snapshot());
    }

    private void ApplyPlaybackPolicy(SessionId? sessionId, CancellationToken cancellationToken)
    {
        HostStateSnapshot snapshot = state.Snapshot();
        foreach (HostSessionSnapshot session in snapshot.Sessions
                     .Where(item => sessionId is null || item.Session.Id == sessionId.Value))
        {
            PlaybackDecision decision = playbackPolicyEvaluator.Evaluate(
                state.SystemState,
                state.PlaybackPolicy,
                new WallpaperSessionSnapshot(
                    session.UserIntent,
                    session.Session.State,
                    0,
                    true));
            state.UpdateSession(session.Session.Id, session.Session.Generation, decision.TargetState);
            playbackJournal.Record(session.Session.Id.Value, session.Session.Generation,
                RendererPlaybackEvidenceKind.PolicyDecided, nameof(ChangeRendererState),
                decision.TargetState, decision.Reason);
            _ = SendPlaybackDecisionAsync(session.Session.Id, decision, cancellationToken);
        }
    }

    private async Task ObserveApplyPreparationAsync(
        IReadOnlyList<WallpaperAssignment> assignments,
        LiveWall.Domain.Displays.DisplayTopology topology,
        long generation,
        ApplyGenerationOperation operation,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                operation.DeadlineToken,
                operation.PreparationCancellationToken);
        try
        {
            PreparedApply prepared = await coordinator.PrepareApplyAsync(
                    assignments,
                    topology,
                    generation,
                    operation,
                    deadline.Token)
                .ConfigureAwait(false);
            await PostInternalAsync(new ApplyPreparedCommand(generation, prepared), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            operation.DeadlineToken.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            ApplyGenerationPhase phase = operation.CurrentPhase ?? ApplyGenerationPhase.TopologyReady;
            operation.Record(
                phase,
                outcome: ApplyStageOutcome.Timeout,
                failureReason: ApplyFailureReason.DeadlineExceeded,
                detail: $"Apply exceeded its derived deadline of {operation.TotalDeadline}.");
            operation.Fail(
                phase,
                ApplyFailureReason.DeadlineExceeded,
                $"Apply exceeded its derived deadline of {operation.TotalDeadline}.");
            await PostInternalAsync(
                    new ApplyPreparationFailedCommand(
                        generation,
                        "host.apply.deadline_exceeded",
                        exception.Message,
                        phase,
                        ApplyFailureReason.DeadlineExceeded),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            ApplyGenerationPhase phase = operation.CurrentPhase ?? ApplyGenerationPhase.TopologyReady;
            operation.Record(
                phase,
                outcome: ApplyStageOutcome.Cancelled,
                failureReason: ApplyFailureReason.Cancelled,
                detail: exception.Message);
            operation.Fail(
                phase,
                ApplyFailureReason.Cancelled,
                exception.Message,
                ApplyGenerationTerminalState.Cancelled);
            await PostInternalAsync(
                    new ApplyPreparationFailedCommand(
                        generation,
                        "host.apply.cancelled",
                        exception.Message,
                        phase,
                        ApplyFailureReason.Cancelled,
                        ApplyGenerationTerminalState.Cancelled),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ApplyStageException? staged = exception as ApplyStageException;
            operation.Fail(
                staged?.Phase ?? operation.CurrentPhase,
                staged?.Reason ?? ApplyFailureReason.Unexpected,
                exception.Message);
            await PostInternalAsync(
                    new ApplyPreparationFailedCommand(
                        generation,
                        "host.apply.failed",
                        exception.Message,
                        staged?.Phase ?? operation.CurrentPhase ?? ApplyGenerationPhase.TopologyReady,
                        staged?.Reason ?? ApplyFailureReason.Unexpected),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task ObserveDesktopRecoveryAsync(
        IReadOnlyList<PreparedSession> activeSessions,
        LiveWall.Domain.Displays.DisplayTopology topology,
        long generation,
        CancellationToken cancellationToken)
    {
        DesktopRecoveryOperation? observedOperation = activeRecoveryOperation;
        try
        {
            PreparedRecovery recovery = await coordinator.PrepareRecoveryAsync(
                    activeSessions,
                    topology,
                    generation,
                    milestone => observedOperation?.Record(
                        milestone.HostGeneration,
                        0,
                        milestone.Phase,
                        milestone.DisplayIds,
                        milestone.SurfaceOrdinal,
                        milestone.Outcome,
                        milestone.FailureReason,
                        milestone.Detail),
                    cancellationToken)
                .ConfigureAwait(false);
            await PostInternalAsync(
                    new DesktopRecoveryPreparedCommand(generation, recovery),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DesktopRecoveryStageException? staged =
                exception as DesktopRecoveryStageException;
            observedOperation?.Record(
                generation,
                0,
                staged?.Phase ?? DesktopRecoveryPhase.BatchAbandoned,
                staged?.DisplayIds,
                staged?.SurfaceOrdinal ?? 0,
                outcome: ApplyStageOutcome.Rejected,
                failureReason: staged?.Reason ?? ApplyFailureReason.Unexpected,
                detail: exception.Message);
            observedOperation?.Record(
                generation,
                0,
                DesktopRecoveryPhase.BatchAbandoned,
                detail: "All uncertain Renderer sessions and old/provisional Surfaces were abandoned.");
            await PostInternalAsync(
                    new DesktopRecoveryFailedCommand(
                        generation,
                        "host.desktop.recovery_failed",
                        exception.Message,
                        staged?.Phase ?? DesktopRecoveryPhase.BatchAbandoned,
                        staged?.Reason ?? ApplyFailureReason.Unexpected),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task ObserveDisplayReconciliationAsync(
        IReadOnlyList<WallpaperAssignment> assignments,
        DisplayTopology topology,
        long generation,
        IReadOnlyList<SessionId> retiredSessionIds,
        ApplyGenerationOperation operation,
        CancellationToken cancellationToken,
        int replacementAttempt = 0)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operation.DeadlineToken,
            operation.PreparationCancellationToken);
        try
        {
            PreparedApply? prepared = null;
            for (int attempt = 0; prepared is null; attempt++)
            {
                try
                {
                    prepared = await coordinator.PrepareApplyAsync(
                            assignments,
                            topology,
                            generation,
                            operation,
                            deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (ApplyStageException exception) when (
                    IsTransientShellTopologyMutation(exception) &&
                    attempt < DisplayReconciliationRetryDelays.Length)
                {
                    TimeSpan delay = DisplayReconciliationRetryDelays[attempt];
                    if (displayReconciliationOperations.TryGetValue(
                            topology.Revision,
                            out DisplayReconciliationOperation? reconciliation))
                    {
                        reconciliation.Record(
                            DisplayReconciliationPhase.PreparationRetryScheduled,
                            generation,
                            assignments.Select(item => item.DisplayId).ToArray(),
                            ApplyStageOutcome.Rejected,
                            ApplyFailureReason.TopologyFailed,
                            $"Transient Shell topology mutation; retry {attempt + 1} " +
                            $"scheduled after {delay}. {exception.Message}");
                    }
                    await Task.Delay(delay, deadline.Token).ConfigureAwait(false);
                    reconciliation?.Record(
                        DisplayReconciliationPhase.PreparationRetryStarted,
                        generation,
                        assignments.Select(item => item.DisplayId).ToArray(),
                        detail: $"Transient Shell topology retry {attempt + 1} started.");
                }
            }
            await PostInternalAsync(
                    new DisplayReconciliationPreparedCommand(
                        generation,
                        prepared,
                        retiredSessionIds,
                        replacementAttempt),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            bool timedOut = operation.DeadlineToken.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested;
            ApplyGenerationPhase phase =
                operation.CurrentPhase ?? ApplyGenerationPhase.TopologyReady;
            ApplyFailureReason reason = timedOut
                ? ApplyFailureReason.DeadlineExceeded
                : ApplyFailureReason.Cancelled;
            operation.Fail(
                phase,
                reason,
                exception.Message,
                timedOut
                    ? ApplyGenerationTerminalState.Failed
                    : ApplyGenerationTerminalState.Cancelled);
            await PostInternalAsync(
                    new DisplayReconciliationFailedCommand(
                        generation,
                        topology.Revision,
                        timedOut
                            ? "host.display.reconcile_deadline_exceeded"
                            : "host.display.reconcile_cancelled",
                        exception.Message,
                        phase,
                        reason),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ApplyStageException? staged = exception as ApplyStageException;
            ApplyGenerationPhase phase =
                staged?.Phase ?? operation.CurrentPhase ?? ApplyGenerationPhase.TopologyReady;
            ApplyFailureReason reason = staged?.Reason ?? ApplyFailureReason.Unexpected;
            operation.Fail(phase, reason, exception.Message);
            await PostInternalAsync(
                    new DisplayReconciliationFailedCommand(
                        generation,
                        topology.Revision,
                        "host.display.reconcile_failed",
                        exception.Message,
                        phase,
                        reason),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static bool IsTransientShellTopologyMutation(ApplyStageException exception) =>
        exception.Phase == ApplyGenerationPhase.TopologyReady &&
        exception.Reason == ApplyFailureReason.TopologyFailed &&
        exception.Message.Contains("UnexpectedTopologyMutation", StringComparison.Ordinal);

    private static bool IsTransientShellTopologyMutation(Exception exception) =>
        exception.Message.Contains("UnexpectedTopologyMutation", StringComparison.Ordinal);

    private async Task RetryDisplayReconciliationAfterReplaceAsync(
        IReadOnlyList<WallpaperAssignment> assignments,
        DisplayTopology topology,
        long generation,
        IReadOnlyList<SessionId> retiredSessionIds,
        ApplyGenerationOperation operation,
        int failedReplacementAttempt,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource retry = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operation.DeadlineToken,
            operation.PreparationCancellationToken);
        try
        {
            TimeSpan delay = DisplayReconciliationRetryDelays[failedReplacementAttempt];
            await Task.Delay(delay, retry.Token).ConfigureAwait(false);
            if (displayReconciliationOperations.TryGetValue(
                    topology.Revision,
                    out DisplayReconciliationOperation? reconciliation))
            {
                reconciliation.Record(
                    DisplayReconciliationPhase.PreparationRetryStarted,
                    generation,
                    assignments.Select(item => item.DisplayId).ToArray(),
                    detail: $"Shell coordinate-space retry " +
                        $"{failedReplacementAttempt + 1} started.");
            }
            await ObserveDisplayReconciliationAsync(
                    assignments,
                    topology,
                    generation,
                    retiredSessionIds,
                    operation,
                    cancellationToken,
                    failedReplacementAttempt + 1)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            bool timedOut = operation.DeadlineToken.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested;
            await PostInternalAsync(
                    new DisplayReconciliationFailedCommand(
                        generation,
                        topology.Revision,
                        timedOut
                            ? "host.display.reconcile_deadline_exceeded"
                            : "host.display.reconcile_cancelled",
                        exception.Message,
                        operation.CurrentPhase ?? ApplyGenerationPhase.SurfacesReplaced,
                        timedOut
                            ? ApplyFailureReason.DeadlineExceeded
                            : ApplyFailureReason.Cancelled),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task ObserveRecoveryRebuildAsync(
        IReadOnlyList<WallpaperAssignment> assignments,
        LiveWall.Domain.Displays.DisplayTopology topology,
        long generation,
        int attempt,
        ApplyGenerationOperation operation,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                operation.DeadlineToken,
                operation.PreparationCancellationToken);
        try
        {
            PreparedApply prepared = await coordinator.PrepareApplyAsync(
                    assignments,
                    topology,
                    generation,
                    operation,
                    deadline.Token)
                .ConfigureAwait(false);
            await PostInternalAsync(
                    new DesktopRecoveryRebuildPreparedCommand(generation, attempt, prepared),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ApplyStageException? staged = exception as ApplyStageException;
            ApplyGenerationPhase phase =
                staged?.Phase ?? operation.CurrentPhase ?? ApplyGenerationPhase.TopologyReady;
            ApplyFailureReason reason = staged?.Reason ?? ApplyFailureReason.Unexpected;
            await PostInternalAsync(
                    new DesktopRecoveryRebuildFailedCommand(
                        generation,
                        attempt,
                        "host.desktop.rebuild_failed",
                        exception.Message,
                        phase,
                        reason),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            bool timedOut = operation.DeadlineToken.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested;
            ApplyGenerationPhase phase =
                operation.CurrentPhase ?? ApplyGenerationPhase.TopologyReady;
            await PostInternalAsync(
                    new DesktopRecoveryRebuildFailedCommand(
                        generation,
                        attempt,
                        timedOut ? "host.desktop.rebuild_deadline_exceeded" : "host.desktop.rebuild_cancelled",
                        exception.Message,
                        phase,
                        timedOut ? ApplyFailureReason.DeadlineExceeded : ApplyFailureReason.Cancelled,
                        timedOut
                            ? ApplyGenerationTerminalState.Failed
                            : ApplyGenerationTerminalState.Cancelled),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task PostRecoveryRetryAfterDelayAsync(
        long expectedGeneration,
        int attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RecoveryRebuildDelays[attempt - 1], cancellationToken)
                .ConfigureAwait(false);
            await PostInternalAsync(
                    new RetryDesktopRecoveryCommand(expectedGeneration, attempt),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!shutdown.IsCancellationRequested)
                await PostInternalAsync(new RetryDesktopRecoveryCommand(expectedGeneration, attempt),
                    CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task SendPlaybackDecisionAsync(
        SessionId sessionId,
        PlaybackDecision decision,
        CancellationToken cancellationToken)
    {
        PreparedSession? prepared = state.FindPreparedSession(sessionId);
        if (prepared is null)
        {
            return;
        }

        try
        {
            await prepared.Renderer.SendAsync(
                    new ChangeRendererState(prepared.Session.Generation, decision.TargetState),
                    cancellationToken)
                .ConfigureAwait(false);
            if (decision.TargetState == PlaybackState.Throttled)
            {
                await prepared.Renderer.SendAsync(
                        new ThrottleRenderer(
                            prepared.Session.Generation,
                            decision.FramesPerSecond,
                            decision.Quality),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            playbackJournal.Record(sessionId.Value, prepared.Session.Generation,
                RendererPlaybackEvidenceKind.WriteFailed, nameof(ChangeRendererState),
                decision.TargetState, decision.Reason, exception.GetType().Name);
        }
    }

    private ApplyGenerationOperation? FindApplyOperation(long generation) =>
        applyOperations.TryGetValue(generation, out ApplyGenerationOperation? operation)
            ? operation
            : null;

    private void CompleteSupersededRecovery(long generation)
    {
        // A stale callback may finish its own journal, never the newer transaction's.
        if (activeRecoveryOperation?.Snapshot().Any(item => item.HostGeneration == generation) != true)
            return;
        if (activeRecoveryOperation.Snapshot().Any(item => item.HostGeneration == state.Generation))
            return;
        desktopRecoveryInProgress = false;
        CompleteActiveRecovery(generation, DesktopRecoveryTerminalState.Cancelled, null,
            ApplyFailureReason.Superseded, "Recovery was superseded by a newer Host generation.");
    }

    private void CompleteActiveRecovery(
        long finalHostGeneration,
        DesktopRecoveryTerminalState terminalState,
        DesktopRecoveryCompletionMode? completionMode,
        ApplyFailureReason failureReason = ApplyFailureReason.None,
        string? detail = null)
    {
        DesktopRecoveryOperation? operation = activeRecoveryOperation;
        operation?.Complete(
            finalHostGeneration,
            terminalState,
            completionMode,
            failureReason,
            detail);
        if (recoveryTopologyPending && activeDisplayReconciliationOperation is not null)
        {
            if (terminalState == DesktopRecoveryTerminalState.Succeeded &&
                operation?.Snapshot().Any(item => item.HostGeneration == finalHostGeneration &&
                    item.Phase == DesktopRecoveryPhase.SurfacesReplaced &&
                    item.Outcome == ApplyStageOutcome.Success) == true)
                activeDisplayReconciliationOperation.Record(DisplayReconciliationPhase.SurfacesReplaced,
                    finalHostGeneration, state.GetPreparedSessions()
                        .SelectMany(session => session.Session.Displays).Distinct().ToArray(),
                    detail: "One atomic replacement committed by the shared Shell recovery transaction.");
            activeDisplayReconciliationOperation.Complete(terminalState switch
            {
                DesktopRecoveryTerminalState.Succeeded => DisplayReconciliationTerminalState.Succeeded,
                DesktopRecoveryTerminalState.Cancelled => DisplayReconciliationTerminalState.Cancelled,
                _ => DisplayReconciliationTerminalState.Failed,
            }, finalHostGeneration, failureReason, detail);
            activeDisplayReconciliationOperation = null;
            activeDisplayReconciliationGeneration = null;
        }
        recoveryTopologyPending = false;
        recoveryTopologyDirty = false;
        activeRecoveryOperation = null;
        activeRecoveryPreparation?.Dispose();
        activeRecoveryPreparation = null;
    }

    private void CompleteActiveDisplayReconciliation(long? hostGeneration)
    {
        DisplayReconciliationOperation? operation = activeDisplayReconciliationOperation;
        operation?.Complete(DisplayReconciliationTerminalState.Succeeded, hostGeneration);
        activeDisplayReconciliationOperation = null;
        activeDisplayReconciliationGeneration = null;
    }

    private void CompleteDisplayReconciliation(
        long topologyRevision,
        DisplayReconciliationTerminalState terminalState,
        long? hostGeneration,
        ApplyFailureReason failureReason = ApplyFailureReason.None,
        string? detail = null)
    {
        if (!displayReconciliationOperations.TryGetValue(
                topologyRevision,
                out DisplayReconciliationOperation? operation))
        {
            return;
        }
        operation.Complete(
            terminalState,
            hostGeneration,
            failureReason,
            detail);
        if (ReferenceEquals(activeDisplayReconciliationOperation, operation))
        {
            activeDisplayReconciliationOperation = null;
            activeDisplayReconciliationGeneration = null;
        }
    }

    private void CompleteSupersededApply(long generation)
    {
        ApplyGenerationOperation? operation = FindApplyOperation(generation);
        operation?.CancelPreparation();
        operation?.Fail(
            operation.CurrentPhase,
            ApplyFailureReason.Superseded,
            $"Apply generation {generation} was superseded by a newer Host generation.",
            ApplyGenerationTerminalState.Cancelled);
    }

    private void PruneCompletedApplyOperations(long currentGeneration)
    {
        sessionBlockedGenerations.RemoveWhere(generation => generation < currentGeneration);
        long[] completed = applyOperations
            .Where(item => item.Key != currentGeneration && item.Value.Completion.IsCompleted)
            .OrderByDescending(item => item.Key)
            .Skip(RetainedApplyDiagnostics - 1)
            .Select(item => item.Key)
            .ToArray();
        foreach (long generation in completed)
        {
            if (applyOperations.TryRemove(generation, out ApplyGenerationOperation? operation))
            {
                operation.Dispose();
                sessionBlockedGenerations.Remove(generation);
            }
        }
    }

    private void PruneCompletedRecoveryOperations(long currentRecoveryId)
    {
        long[] completed = recoveryOperations
            .Where(item => item.Key != currentRecoveryId && item.Value.Completion.IsCompleted)
            .OrderByDescending(item => item.Key)
            .Skip(RetainedRecoveryDiagnostics - 1)
            .Select(item => item.Key)
            .ToArray();
        foreach (long recoveryId in completed)
        {
            recoveryOperations.TryRemove(recoveryId, out _);
        }
    }

    private void PruneCompletedDisplayReconciliations(long currentTopologyRevision)
    {
        long[] completed = displayReconciliationOperations
            .Where(item => item.Key != currentTopologyRevision && item.Value.Completion.IsCompleted)
            .OrderByDescending(item => item.Key)
            .Skip(RetainedDisplayReconciliationDiagnostics - 1)
            .Select(item => item.Key)
            .ToArray();
        foreach (long topologyRevision in completed)
        {
            displayReconciliationOperations.TryRemove(topologyRevision, out _);
        }
    }

    private void TrackRetirement(
        IReadOnlyList<PreparedSession> sessions,
        CancellationToken cancellationToken)
    {
        if (sessions.Count == 0)
        {
            return;
        }

        Task<SessionRetirementBatchResult> retirement =
            ObserveRetirementAsync(sessions, cancellationToken);
        lock (retirementGate)
        {
            pendingRetirements.RemoveAll(task => task.IsCompleted);
            pendingRetirements.Add(retirement);
        }
    }

    private async Task<SessionRetirementBatchResult> ObserveRetirementAsync(
        IReadOnlyList<PreparedSession> sessions,
        CancellationToken cancellationToken)
    {
        SessionRetirementBatchResult result = await coordinator
            .RetireAsync(sessions, cancellationToken)
            .ConfigureAwait(false);
        lock (retirementGate)
        {
            retirementHistory.Add(result);
            if (retirementHistory.Count > RetainedApplyDiagnostics)
            {
                retirementHistory.RemoveRange(
                    0,
                    retirementHistory.Count - RetainedApplyDiagnostics);
            }
        }

        return result;
    }

    private async ValueTask PostInternalAsync(
        HostCommand command,
        CancellationToken cancellationToken) =>
        await channel.Writer.WriteAsync(new QueuedHostCommand(command, null), cancellationToken)
            .ConfigureAwait(false);

    private sealed record QueuedHostCommand(
        HostCommand Command,
        TaskCompletionSource<HostCommandResult>? Completion);
}
