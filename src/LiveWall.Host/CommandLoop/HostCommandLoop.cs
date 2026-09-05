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
    private readonly Channel<QueuedHostCommand> channel;
    private readonly HostState state = new();
    private readonly ISessionCoordinator coordinator;
    private readonly IPlaybackPolicyEvaluator playbackPolicyEvaluator;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task runTask;

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

        shutdown.Dispose();
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

        long generation = state.BeginGeneration();
        _ = ObserveApplyPreparationAsync(assignments, state.DisplayTopology, generation, cancellationToken);
        return HostCommandResult.Success(state.Revision);
    }

    private async Task<HostCommandResult> CompleteApplyAsync(
        ApplyPreparedCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Generation != state.Generation ||
            state.DisplayTopology?.Revision != command.Prepared.DisplayTopologyRevision)
        {
            _ = coordinator.RetireAsync(command.Prepared.Sessions, cancellationToken);
            return HostCommandResult.Success(state.Revision);
        }

        IReadOnlyList<ActiveHostSession> replaced = state.FindSessionsReplacedBy(
            command.Prepared.Assignments);
        try
        {
            await coordinator.ReplaceSurfacesAsync(
                    command.Prepared,
                    replaced.Select(session => session.Prepared).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                await coordinator.RetireAsync(command.Prepared.Sessions, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            return HostCommandResult.Reject(state.Revision, "host.desktop.replace_failed");
        }

        IReadOnlyList<ActiveHostSession> retired = state.ReplacePreparedSessions(command.Prepared);
        _ = coordinator.RetireAsync(
            retired.Select(session => session.Prepared).ToArray(),
            cancellationToken);
        return HostCommandResult.Success(state.Revision);
    }

    private HostCommandResult FailApply(ApplyPreparationFailedCommand command)
    {
        if (command.Generation != state.Generation)
        {
            return HostCommandResult.Success(state.Revision);
        }

        return HostCommandResult.Reject(state.Revision, command.ErrorCode);
    }

    private HostCommandResult BeginDesktopRecovery(CancellationToken cancellationToken)
    {
        if (state.DisplayTopology is null)
        {
            return HostCommandResult.Reject(state.Revision, "host.display.topology_unavailable");
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
        PreparedSurface[] provisional = recovery.Sessions
            .SelectMany(session => session.Replacement.Surfaces)
            .ToArray();
        if (command.Generation != state.Generation ||
            state.DisplayTopology?.Revision != recovery.DisplayTopologyRevision)
        {
            _ = coordinator.DestroySurfacesAsync(provisional, CancellationToken.None);
            return HostCommandResult.Success(state.Revision);
        }


        if (!state.CanReplaceRecoveredSessions(recovery))
        {
            _ = coordinator.DestroySurfacesAsync(provisional, CancellationToken.None);
            return HostCommandResult.Reject(state.Revision, "host.desktop.recovery_stale");
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
            _ = coordinator.DestroySurfacesAsync(provisional, CancellationToken.None);
            return HostCommandResult.Reject(state.Revision, "host.desktop.recovery_replace_failed");
        }

        if (!state.ReplaceRecoveredSessions(recovery))
        {
            throw new InvalidOperationException(
                "Desktop recovery state changed while the single-writer command was committing.");
        }

        PreparedSurface[] retired = recovery.Sessions
            .SelectMany(session => session.Previous.Surfaces)
            .ToArray();
        _ = coordinator.DestroySurfacesAsync(retired, CancellationToken.None);
        return HostCommandResult.Success(state.Revision);
    }

    private HostCommandResult FailDesktopRecovery(DesktopRecoveryFailedCommand command)
    {
        if (command.Generation != state.Generation)
        {
            return HostCommandResult.Success(state.Revision);
        }

        return HostCommandResult.Reject(state.Revision, command.ErrorCode);
    }

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
        _ = coordinator.RetireAsync(state.GetPreparedSessions(), cancellationToken);
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
        CancellationToken cancellationToken)
    {
        try
        {
            PreparedApply prepared = await coordinator.PrepareApplyAsync(
                    assignments,
                    topology,
                    generation,
                    cancellationToken)
                .ConfigureAwait(false);
            await PostInternalAsync(new ApplyPreparedCommand(generation, prepared), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await PostInternalAsync(
                    new ApplyPreparationFailedCommand(generation, "host.apply.failed", exception.Message),
                    cancellationToken)
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

    private async ValueTask PostInternalAsync(
        HostCommand command,
        CancellationToken cancellationToken) =>
        await channel.Writer.WriteAsync(new QueuedHostCommand(command, null), cancellationToken)
            .ConfigureAwait(false);

    private sealed record QueuedHostCommand(
        HostCommand Command,
        TaskCompletionSource<HostCommandResult>? Completion);
}
