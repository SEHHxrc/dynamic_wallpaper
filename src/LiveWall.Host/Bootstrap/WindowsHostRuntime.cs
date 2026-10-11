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
        Transition(WindowsHostRuntimeState.Created, WindowsHostRuntimeState.Starting);
        try
        {
            SetState(WindowsHostRuntimeState.Restoring);
            BootstrapResult = await bootstrapper.InitializeAsync(
                    commandLoop,
                    displayTopologySource.Current,
                    cancellationToken)
                .ConfigureAwait(false);

            eventBridge.Start();
            eventBridge.RequestEnvironmentValidation();
            _ = await commandLoop.SendAsync(new GetHostStateCommand(), cancellationToken)
                .ConfigureAwait(false);
            SetState(WindowsHostRuntimeState.Running);
        }
        catch (Exception exception)
        {
            LastFault = exception;
            SetState(WindowsHostRuntimeState.Faulted);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (State != WindowsHostRuntimeState.Running)
        {
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
                return stopTask;
            }
            if (state == WindowsHostRuntimeState.Stopped)
            {
                return Task.CompletedTask;
            }

            state = WindowsHostRuntimeState.Stopping;
            stopTask = StopCoreAsync(cancellationToken);
            return stopTask;
        }
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
            if (persistConfiguration && finalState is not null)
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

    private void Transition(WindowsHostRuntimeState expected, WindowsHostRuntimeState next)
    {
        lock (gate)
        {
            if (state != expected)
            {
                throw new InvalidOperationException(
                    $"Host runtime cannot transition from {state} to {next}.");
            }
            state = next;
        }
    }

    private void SetState(WindowsHostRuntimeState next)
    {
        lock (gate)
        {
            state = next;
        }
    }
}
