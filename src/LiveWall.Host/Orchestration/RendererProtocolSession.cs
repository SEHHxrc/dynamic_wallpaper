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
    private readonly Lock stateGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task receiveTask;
    private readonly Task processTask;
    private int readerClaimed;
    private bool disposed;
    private bool terminalPublished;

    public RendererProtocolSession(
        SessionId id,
        long generation,
        LengthPrefixedJsonChannel protocolChannel,
        IRendererProcessHandle process,
        IEnumerable<string>? receivedHandshakeMessageIds = null)
    {
        Id = id;
        this.protocolChannel = protocolChannel ?? throw new ArgumentNullException(nameof(protocolChannel));
        this.process = process ?? throw new ArgumentNullException(nameof(process));
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
            envelope = RendererProtocolMapper.ToEnvelope(Id.Value, command);
        }

        await protocolChannel.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stateMachine.Close();
        }

        lifetime.Cancel();
        await protocolChannel.DisposeAsync().ConfigureAwait(false);
        if (!process.HasExited)
        {
            process.Terminate();
        }

        await IgnoreCancellationAsync(receiveTask).ConfigureAwait(false);
        await IgnoreCancellationAsync(processTask).ConfigureAwait(false);
        await process.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
        events.Writer.TryComplete();
    }

    private async IAsyncEnumerable<RendererEvent> ReadAndReleaseAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (RendererEvent rendererEvent in events.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
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
                    stateMachine.ValidateAndApply(rendererEvent);
                }

                await events.Writer.WriteAsync(rendererEvent, cancellationToken)
                    .ConfigureAwait(false);
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
            if (disposed || stateMachine.IsClosed || terminalPublished)
            {
                events.Writer.TryComplete();
                return;
            }

            terminalPublished = true;
            generation = stateMachine.Generation;
        }

        events.Writer.TryWrite(new RendererFailed(
            generation,
            "renderer.protocol.invalid_state",
            exception.Message,
            Recoverable: false));
        events.Writer.TryComplete(exception);
    }

    private void PublishProcessFailure()
    {
        long generation;
        lock (stateGate)
        {
            if (disposed || stateMachine.IsClosed || terminalPublished)
            {
                events.Writer.TryComplete();
                return;
            }

            terminalPublished = true;
            generation = stateMachine.Generation;
        }

        events.Writer.TryWrite(new RendererFailed(
            generation,
            "renderer.process.exited",
            $"Renderer process {process.Id} exited with code " +
            $"{process.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.",
            Recoverable: true));
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
