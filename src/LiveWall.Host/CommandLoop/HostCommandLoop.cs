using System.Collections.Concurrent;
using System.Threading.Channels;
using LiveWall.Application.Policies;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Sessions;
using LiveWall.Host.Orchestration;

namespace LiveWall.Host.CommandLoop;

internal sealed class HostCommandLoop : IAsyncDisposable
{
    private const int RetainedApplyDiagnostics = 32;
    private const int MaximumRecoveryRebuildAttempts = 3;
    private static readonly TimeSpan[] RecoveryRebuildDelays =
    [
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];
    private readonly Channel<QueuedHostCommand> channel;
    private readonly HostState state = new();
    private readonly ISessionCoordinator coordinator;
    private readonly IPlaybackPolicyEvaluator playbackPolicyEvaluator;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task runTask;
    private readonly ConcurrentDictionary<long, ApplyGenerationOperation> applyOperations = new();
    private readonly Lock retirementGate = new();
    private readonly List<Task<SessionRetirementBatchResult>> pendingRetirements = [];
    private readonly List<SessionRetirementBatchResult> retirementHistory = [];
    private bool desktopRecoveryInProgress;
    private bool desktopRecoveryCircuitOpen;

    public HostCommandLoop(
        ISessionCoordinator coordinator,
        IPlaybackPolicyEvaluator playbackPolicyEvaluator)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.playbackPolicyEvaluator = playbackPolicyEvaluator ??
            throw new ArgumentNullException(nameof(playbackPolicyEvaluator));
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
        channel.Writer.TryComplete();
        shutdown.Cancel();
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
        CancellationToken cancellationToken) =>
        command switch
        {
            ApplyPreparedCommand prepared => CompleteApplyAsync(prepared, cancellationToken),
            DesktopRecoveryPreparedCommand recovered => CompleteDesktopRecoveryAsync(
                recovered,
                cancellationToken),
            DesktopRecoveryRebuildPreparedCommand rebuilt => CompleteRecoveryRebuildAsync(
                rebuilt,
                cancellationToken),
            _ => Task.FromResult(Handle(command, cancellationToken)),
        };

    private HostCommandResult Handle(HostCommand command, CancellationToken cancellationToken) =>
        command switch
        {
            ApplyWallpaperCommand apply => BeginApply(apply, cancellationToken),
            RestoreAssignmentsCommand restore => BeginRestore(restore, cancellationToken),
            ApplyPreparationFailedCommand failed => FailApply(failed),
            DisplayTopologyChangedCommand topology => SetTopology(topology),
            SystemPolicyChangedCommand policy => SetSystemState(policy, cancellationToken),
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
        CancellationToken cancellationToken) =>
        BeginApply(command.Assignments, cancellationToken);

    private HostCommandResult BeginApply(
        IReadOnlyList<WallpaperAssignment> assignments,
        CancellationToken cancellationToken)
    {
        if (state.DisplayTopology is null)
        {
            return HostCommandResult.Reject(state.Revision, "host.display.topology_unavailable");
        }

        if (assignments.Count == 0)
        {
            return HostCommandResult.Reject(state.Revision, "host.assignment.empty");
        }

        int surfaceCount = coordinator.GetPlannedSurfaceCount(assignments, state.DisplayTopology);
        if (surfaceCount == 0)
        {
            return HostCommandResult.Reject(state.Revision, "host.layout.empty");
        }

        if (state.Generation > 0)
        {
            CompleteSupersededApply(state.Generation);
        }
        long generation = state.BeginGeneration();
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
        return HostCommandResult.Reject(state.Revision, command.ErrorCode);
    }

    private HostCommandResult BeginDesktopRecovery(CancellationToken cancellationToken)
    {
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
            return HostCommandResult.Success(state.Revision);
        }

        desktopRecoveryInProgress = true;
        if (state.GetPreparedSessions().Count == 0)
        {
            return StartRecoveryRebuild(attempt: 1, cancellationToken);
        }

        long generation = state.BeginGeneration();
        _ = ObserveDesktopRecoveryAsync(
            state.GetPreparedSessions(),
            state.DisplayTopology,
            generation,
            cancellationToken);
        return HostCommandResult.Success(state.Revision);
    }

    private async Task<HostCommandResult> CompleteDesktopRecoveryAsync(
        DesktopRecoveryPreparedCommand command,
        CancellationToken cancellationToken)
    {
        PreparedRecovery recovery = command.Recovery;
        if (command.Generation != state.Generation)
        {
            _ = coordinator.AbandonRecoveryAsync(recovery, CancellationToken.None);
            return HostCommandResult.Success(state.Revision);
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

        PreparedSurface[] retired = recovery.Sessions
            .SelectMany(session => session.Previous.Surfaces)
            .ToArray();
        _ = coordinator.AbandonSurfacesAsync(retired, CancellationToken.None);
        desktopRecoveryInProgress = false;
        return HostCommandResult.Success(state.Revision);
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
        }
        catch
        {
            desktopRecoveryInProgress = false;
            desktopRecoveryCircuitOpen = true;
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
            return HostCommandResult.Success(state.Revision);
        }

        state.InvalidateAllSessions();
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
            desktopRecoveryInProgress = false;
            return HostCommandResult.Success(state.Revision);
        }

        return StartRecoveryRebuild(command.Attempt, cancellationToken);
    }

    private HostCommandResult StartRecoveryRebuild(
        int attempt,
        CancellationToken cancellationToken)
    {
        if (state.DisplayTopology is null)
        {
            desktopRecoveryInProgress = false;
            return HostCommandResult.Reject(state.Revision, "host.display.topology_unavailable");
        }

        IReadOnlyList<WallpaperAssignment> assignments = state.GetAssignments();
        if (assignments.Count == 0)
        {
            desktopRecoveryInProgress = false;
            return HostCommandResult.Success(state.Revision);
        }

        long generation = state.BeginGeneration();
        int surfaceCount = coordinator.GetPlannedSurfaceCount(assignments, state.DisplayTopology);
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
        if (command.Generation != state.Generation ||
            state.DisplayTopology?.Revision != command.Prepared.DisplayTopologyRevision)
        {
            CompleteSupersededApply(command.Generation);
            TrackRetirement(command.Prepared.Sessions, CancellationToken.None);
            desktopRecoveryInProgress = false;
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
            catch
            {
                desktopRecoveryInProgress = false;
                desktopRecoveryCircuitOpen = true;
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
        ApplyGenerationOperation? completedOperation = FindApplyOperation(command.Generation);
        completedOperation?.Record(
            ApplyGenerationPhase.SurfacesReplaced,
            command.Prepared.Assignments.Select(assignment => assignment.DisplayId).ToArray());
        completedOperation?.Succeed();
        desktopRecoveryInProgress = false;
        desktopRecoveryCircuitOpen = false;
        return HostCommandResult.Success(state.Revision);
    }

    private HostCommandResult FailRecoveryRebuild(
        DesktopRecoveryRebuildFailedCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Generation != state.Generation)
        {
            CompleteSupersededApply(command.Generation);
            return HostCommandResult.Success(state.Revision);
        }

        if (command.TerminalState == ApplyGenerationTerminalState.Cancelled)
        {
            FindApplyOperation(command.Generation)?.Fail(
                command.Phase,
                command.FailureReason,
                command.Message,
                ApplyGenerationTerminalState.Cancelled);
            desktopRecoveryInProgress = false;
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
            return HostCommandResult.Reject(state.Revision, finalErrorCode);
        }

        operation?.Fail(failurePhase, failureReason, failureDetail);
        ScheduleRecoveryRebuild(failedGeneration, nextAttempt, cancellationToken);
        return HostCommandResult.Reject(state.Revision, finalErrorCode);
    }

    private void ScheduleRecoveryRebuild(
        long expectedGeneration,
        int attempt,
        CancellationToken cancellationToken) =>
        _ = PostRecoveryRetryAfterDelayAsync(
            expectedGeneration,
            attempt,
            cancellationToken);

    private HostCommandResult SetTopology(DisplayTopologyChangedCommand command)
    {
        state.SetDisplayTopology(command.Topology);
        return HostCommandResult.Success(state.Revision);
    }

    private HostCommandResult SetSystemState(
        SystemPolicyChangedCommand command,
        CancellationToken cancellationToken)
    {
        state.SetSystemState(command.SystemState);
        ApplyPlaybackPolicy(null, cancellationToken);
        return HostCommandResult.Success(state.Revision);
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
        state.RequestShutdown();
        PreparedSession[] sessions = state.InvalidateAllSessions().ToArray();
        TrackRetirement(sessions, CancellationToken.None);
        return HostCommandResult.Success(state.Revision);
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
                operation.DeadlineToken);
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
        try
        {
            PreparedRecovery recovery = await coordinator.PrepareRecoveryAsync(
                    activeSessions,
                    topology,
                    generation,
                    cancellationToken)
                .ConfigureAwait(false);
            await PostInternalAsync(
                    new DesktopRecoveryPreparedCommand(generation, recovery),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await PostInternalAsync(
                    new DesktopRecoveryFailedCommand(
                        generation,
                        "host.desktop.recovery_failed",
                        exception.Message),
                    cancellationToken)
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
                operation.DeadlineToken);
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

    private ApplyGenerationOperation? FindApplyOperation(long generation) =>
        applyOperations.TryGetValue(generation, out ApplyGenerationOperation? operation)
            ? operation
            : null;

    private void CompleteSupersededApply(long generation) =>
        FindApplyOperation(generation)?.Fail(
            FindApplyOperation(generation)?.CurrentPhase,
            ApplyFailureReason.Superseded,
            $"Apply generation {generation} was superseded by a newer Host generation.",
            ApplyGenerationTerminalState.Cancelled);

    private void PruneCompletedApplyOperations(long currentGeneration)
    {
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
            }
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
