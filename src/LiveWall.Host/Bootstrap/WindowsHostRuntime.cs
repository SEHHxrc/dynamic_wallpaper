using LiveWall.Application.Sessions;
using LiveWall.Host.CommandLoop;

namespace LiveWall.Host.Bootstrap;

internal enum WindowsHostRuntimeState
{
    Created,
    Starting,
    Restoring,
    Running,
    Stopping,
    Faulted,
    Stopped,
}

internal sealed class WindowsHostRuntime : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly HostCommandLoop commandLoop;
    private readonly HostBootstrapper bootstrapper;
    private readonly IDisplayTopologySource displayTopologySource;
    private readonly IWindowsEnvironmentEventBridge eventBridge;
    private readonly Func<ValueTask> disposePlatformAsync;
    private readonly bool persistConfiguration;
    private WindowsHostRuntimeState state = WindowsHostRuntimeState.Created;
    private Task? stopTask;
    private CancellationTokenSource? startingCancellation;

    public WindowsHostRuntime(
        HostCommandLoop commandLoop,
        HostBootstrapper bootstrapper,
        IDisplayTopologySource displayTopologySource,
        IWindowsEnvironmentEventBridge eventBridge,
        Func<ValueTask>? disposePlatformAsync = null,
        bool persistConfiguration = true)
    {
        this.commandLoop = commandLoop ?? throw new ArgumentNullException(nameof(commandLoop));
        this.bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
        this.displayTopologySource = displayTopologySource ??
            throw new ArgumentNullException(nameof(displayTopologySource));
        this.eventBridge = eventBridge ?? throw new ArgumentNullException(nameof(eventBridge));
        this.disposePlatformAsync = disposePlatformAsync ?? (() => ValueTask.CompletedTask);
        this.persistConfiguration = persistConfiguration;
    }

    public WindowsHostRuntimeState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public Exception? LastFault { get; private set; }

    public HostBootstrapResult? BootstrapResult { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource startup;
        lock (gate)
        {
            if (state != WindowsHostRuntimeState.Created)
                throw new InvalidOperationException($"Host runtime cannot start from {state}.");
            state = WindowsHostRuntimeState.Starting;
            startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startingCancellation = startup;
        }
        try
        {
            lock (gate)
            {
                startup.Token.ThrowIfCancellationRequested();
                state = WindowsHostRuntimeState.Restoring;
            }
            BootstrapResult = await bootstrapper.InitializeAsync(
                    commandLoop,
                    displayTopologySource.Current,
                    startup.Token)
                .ConfigureAwait(false);

            lock (gate)
            {
                startup.Token.ThrowIfCancellationRequested();
                eventBridge.Start();
                eventBridge.RequestEnvironmentValidation();
            }
            _ = await commandLoop.SendAsync(new GetHostStateCommand(), startup.Token)
                .ConfigureAwait(false);
            lock (gate)
            {
                startup.Token.ThrowIfCancellationRequested();
                state = WindowsHostRuntimeState.Running;
            }
        }
        catch (Exception exception)
        {
            bool stoppedDuringStartup;
            lock (gate)
            {
                stoppedDuringStartup = state is WindowsHostRuntimeState.Stopping or WindowsHostRuntimeState.Stopped;
                if (!stoppedDuringStartup) { LastFault = exception; state = WindowsHostRuntimeState.Faulted; }
            }
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (stoppedDuringStartup) throw new OperationCanceledException("Host stopped during startup.", exception, startup.Token);
            throw;
        }
        finally
        {
            lock (gate) { startingCancellation = null; }
            startup.Dispose();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (State != WindowsHostRuntimeState.Running)
        {
            if (State is WindowsHostRuntimeState.Stopping or WindowsHostRuntimeState.Stopped)
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException("The Windows Host runtime is not running.");
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (stopTask is not null)
            {
                return stopTask.WaitAsync(cancellationToken);
            }
            if (state == WindowsHostRuntimeState.Stopped)
            {
                return Task.CompletedTask;
            }

            state = WindowsHostRuntimeState.Stopping;
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = completion.Task;
            startingCancellation?.Cancel();
            // A caller may stop waiting, but must not cancel the shared resource
            // cleanup or skip persistence for every other Shutdown/Dispose waiter.
            _ = CompleteStopAsync(completion);
            return stopTask.WaitAsync(cancellationToken);
        }
    }

    private async Task CompleteStopAsync(TaskCompletionSource completion)
    {
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    public async ValueTask DisposeAsync() =>
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Exception? firstFailure = null;
        try
        {
            await eventBridge.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            firstFailure = exception;
        }

        HostStateSnapshot? finalState = null;
        try
        {
            HostCommandResult shutdownResult = await commandLoop.SendAsync(
                    new ShutdownCommand(),
                    cancellationToken)
                .ConfigureAwait(false);
            finalState = shutdownResult.State;
            _ = await commandLoop.WaitForPendingRetirementsForDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
            finalState ??= (await commandLoop.SendAsync(
                    new GetHostStateCommand(),
                    cancellationToken)
                .ConfigureAwait(false)).State;
            // Before initialization completes, an empty/partial Host snapshot must
            // not overwrite the user's existing persisted Assignment.
            if (persistConfiguration && finalState is not null && BootstrapResult is not null)
            {
                await bootstrapper.PersistAsync(finalState, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        try
        {
            await commandLoop.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        try
        {
            await disposePlatformAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        LastFault ??= firstFailure;
        SetState(WindowsHostRuntimeState.Stopped);
    }

    private void SetState(WindowsHostRuntimeState next)
    {
        lock (gate)
        {
            state = next;
        }
    }
}
