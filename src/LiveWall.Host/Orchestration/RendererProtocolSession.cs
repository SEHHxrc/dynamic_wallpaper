using System.Threading.Channels;
using LiveWall.Application.Sessions;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Domain.Sessions;
using LiveWall.Infrastructure.Ipc;

namespace LiveWall.Host.Orchestration;

internal sealed class RendererProtocolSession : IRendererSession
{
    private readonly LengthPrefixedJsonChannel protocolChannel;
    private readonly IRendererProcessHandle process;
    private readonly RendererProtocolStateMachine stateMachine;
    private readonly Channel<RendererEvent> events;
    private readonly HashSet<string> receivedMessageIds = new(StringComparer.Ordinal);
    private readonly Queue<ChangeRendererState> playbackReplies = new();
    private readonly Lock stateGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task receiveTask;
    private readonly Task processTask;
    private readonly TimeSpan processExitTimeout;
    private readonly TaskCompletionSource<RendererFailed> activeFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int failureObserverClaimed;
    private Task? disposeTask;
    private int readerClaimed;
    private bool disposed;
    private bool shutdownAcknowledged;
    private bool terminalPublished;
    private readonly RendererPlaybackJournal playbackJournal = new();

    internal IReadOnlyList<RendererPlaybackEvidence> GetPlaybackEvidenceForDiagnostics() =>
        playbackJournal.Snapshot();

    public RendererProtocolSession(
        SessionId id,
        long generation,
        LengthPrefixedJsonChannel protocolChannel,
        IRendererProcessHandle process,
        IEnumerable<string>? receivedHandshakeMessageIds = null,
        TimeSpan? processExitTimeout = null)
    {
        Id = id;
        this.protocolChannel = protocolChannel ?? throw new ArgumentNullException(nameof(protocolChannel));
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        this.processExitTimeout = processExitTimeout ?? TimeSpan.FromSeconds(1);
        if (this.processExitTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processExitTimeout));
        stateMachine = new RendererProtocolStateMachine(generation);
        if (receivedHandshakeMessageIds is not null)
        {
            receivedMessageIds.UnionWith(receivedHandshakeMessageIds);
        }
        events = Channel.CreateUnbounded<RendererEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        receiveTask = ReceiveAsync(lifetime.Token);
        processTask = ObserveProcessExitAsync(lifetime.Token);
    }

    public SessionId Id { get; }

    internal int ProcessId => process.Id;
    internal Task<RendererFailed> ActiveFailure => activeFailure.Task;
    internal bool TryClaimFailureObserver() => Interlocked.Exchange(ref failureObserverClaimed, 1) == 0;
    internal ValueTask DisconnectForDiagnosticsAsync() => protocolChannel.DisposeAsync();

    public IAsyncEnumerable<RendererEvent> ReadEventsAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref readerClaimed, 1) != 0)
        {
            throw new InvalidOperationException(
                "RendererProtocolSession supports exactly one event consumer.");
        }

        return ReadAndReleaseAsync(cancellationToken);
    }

    public async Task SendAsync(RendererCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        RendererEnvelope envelope;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stateMachine.ValidateAndApply(command);
            if (command is ChangeRendererState { TargetState: not LiveWall.Domain.Playback.PlaybackState.Stopped } requestedPlayback)
            {
                if (playbackReplies.Count >= 256) throw new InvalidOperationException("Too many pending playback acknowledgments.");
                playbackReplies.Enqueue(requestedPlayback);
            }
            envelope = RendererProtocolMapper.ToEnvelope(Id.Value, command);
        }

        if (command is ChangeRendererState playback)
        {
            playbackJournal.Record(Id.Value, command.Generation,
                RendererPlaybackEvidenceKind.WriteStarted, envelope.Type, playback.TargetState);
            try
            {
                await protocolChannel.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
                playbackJournal.Record(Id.Value, command.Generation,
                    RendererPlaybackEvidenceKind.WriteCompleted, envelope.Type, playback.TargetState);
            }
            catch (Exception exception)
            {
                playbackJournal.Record(Id.Value, command.Generation,
                    RendererPlaybackEvidenceKind.WriteFailed, envelope.Type, playback.TargetState,
                    failureType: exception.GetType().Name);
                throw;
            }
        }
        else
        {
            await protocolChannel.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (stateGate)
        {
            if (disposeTask is null)
            {
                disposed = true;
                stateMachine.Close();
                disposeTask = DisposeCoreAsync(shutdownAcknowledged);
            }

            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync(bool acknowledged)
    {
        lifetime.Cancel();
        activeFailure.TrySetCanceled();
        try
        {
            bool terminated = false;
            try
            {
                // ShutdownCompleted confirms resource teardown, but Main may still be
                // unwinding. Keep the pipe open while allowing a bounded normal return.
                // An abandoned/unacknowledged session receives no such grace period.
                if (acknowledged && !process.HasExited)
                    await WaitForProcessExitAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Terminate();
                    terminated = true;
                }
            }
            if (terminated && !await WaitForProcessExitAsync().ConfigureAwait(false))
                throw new TimeoutException("Renderer did not exit after termination.");
        }
        finally
        {
            try
            {
                await IgnoreCancellationAsync(receiveTask).ConfigureAwait(false);
                await IgnoreCancellationAsync(processTask).ConfigureAwait(false);
            }
            finally
            {
                try { await protocolChannel.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    try { await process.DisposeAsync().ConfigureAwait(false); }
                    finally
                    {
                        lifetime.Dispose();
                        events.Writer.TryComplete();
                    }
                }
            }
        }
    }

    private async Task<bool> WaitForProcessExitAsync()
    {
        using CancellationTokenSource budget = new(processExitTimeout);
        try
        {
            await process.WaitForExitAsync(budget.Token).WaitAsync(budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested || process.HasExited)
        {
        }
        return process.HasExited;
    }

    private async IAsyncEnumerable<RendererEvent> ReadAndReleaseAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (RendererEvent rendererEvent in events.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                // A queued control reply may predate a subsequent reattachment. It
                // cannot satisfy a new binding's stage or revive its playback state.
                if (rendererEvent is RendererPlaybackStateChanged)
                {
                    lock (stateGate)
                        if (rendererEvent.Generation < stateMachine.Generation) continue;
                }
                yield return rendererEvent;
            }
        }
        finally
        {
            Volatile.Write(ref readerClaimed, 0);
        }
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                RendererEnvelope envelope = await protocolChannel
                    .ReadAsync<RendererEnvelope>(cancellationToken)
                    .ConfigureAwait(false);
                ValidateEnvelope(envelope);
                RendererEvent? rendererEvent = RendererProtocolMapper.ToEvent(envelope);
                if (rendererEvent is null)
                {
                    continue;
                }

                lock (stateGate)
                {
                    if (rendererEvent is RendererPlaybackStateChanged feedback)
                    {
                        if (!playbackReplies.TryDequeue(out ChangeRendererState? expected) ||
                            expected.Generation != feedback.Generation || expected.TargetState != feedback.State || stateMachine.IsClosed)
                            throw new InvalidOperationException("Renderer playback acknowledgment does not match a sent command.");
                    }
                    else stateMachine.ValidateAndApply(rendererEvent);
                    if (rendererEvent is RendererShutdownCompleted)
                        shutdownAcknowledged = true;
                    if (rendererEvent is RendererPlaybackStateChanged playback)
                    {
                        playbackJournal.Record(Id.Value, playback.Generation,
                            RendererPlaybackEvidenceKind.FeedbackReceived, envelope.Type, playback.State);
                    }
                }

                await events.Writer.WriteAsync(rendererEvent, cancellationToken)
                    .ConfigureAwait(false);
                if (rendererEvent is RendererFailed failure) activeFailure.TrySetResult(failure);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (exception is ProtocolFrameException)
            {
                try
                {
                    await process.WaitForExitAsync(cancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }

                if (process.HasExited)
                {
                    PublishProcessFailure();
                    return;
                }
            }

            PublishProtocolFailure(exception);
        }
    }

    private async Task ObserveProcessExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            // Process exit can race a final, already-written ShutdownCompleted frame.
            // Give the sole receive pump a bounded chance to drain the pipe before
            // classifying the exit. No feedback is fabricated if draining times out.
            try
            {
                await receiveTask.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            bool isDisposed;
            bool closed;
            lock (stateGate)
            {
                isDisposed = disposed;
                closed = stateMachine.IsClosed;
            }

            if (!isDisposed && !closed)
            {
                PublishProcessFailure();
            }
            else if (closed)
            {
                events.Writer.TryComplete();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ValidateEnvelope(RendererEnvelope envelope)
    {
        if (envelope.Protocol.Major != ProtocolVersion.Version1.Major)
        {
            throw new InvalidOperationException(
                $"Renderer protocol major {envelope.Protocol.Major} is not supported.");
        }

        if (!StringComparer.Ordinal.Equals(envelope.SessionId, Id.Value))
        {
            throw new InvalidOperationException("Renderer event belongs to another session.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.MessageId);
        lock (stateGate)
        {
            if (!receivedMessageIds.Add(envelope.MessageId))
            {
                throw new InvalidOperationException(
                    $"Renderer reused messageId '{envelope.MessageId}'.");
            }
        }
    }

    private void PublishProtocolFailure(Exception exception)
    {
        long generation;
        lock (stateGate)
        {
            if (disposed || stateMachine.IsClosed)
            {
                events.Writer.TryComplete();
                return;
            }
            if (terminalPublished)
            {
                return;
            }

            terminalPublished = true;
            generation = stateMachine.Generation;
        }

        RendererFailed failure = new(
            generation,
            "renderer.protocol.invalid_state",
            exception.Message,
            Recoverable: false);
        activeFailure.TrySetResult(failure);
        events.Writer.TryWrite(failure);
        events.Writer.TryComplete(exception);
    }

    private void PublishProcessFailure()
    {
        long generation;
        lock (stateGate)
        {
            if (disposed || stateMachine.IsClosed)
            {
                events.Writer.TryComplete();
                return;
            }
            if (terminalPublished)
            {
                return;
            }

            terminalPublished = true;
            generation = stateMachine.Generation;
        }

        RendererFailed failure = new(
            generation,
            "renderer.process.exited",
            "Renderer process exited with code " +
            $"{process.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.",
            Recoverable: true);
        activeFailure.TrySetResult(failure);
        events.Writer.TryWrite(failure);
        events.Writer.TryComplete();
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
