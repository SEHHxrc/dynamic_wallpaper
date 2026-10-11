using LiveWall.Application.Sessions;
using LiveWall.Application.Policies;
using LiveWall.Host.CommandLoop;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Displays;
using LiveWall.Platform.Windows.Power;

namespace LiveWall.Host.Bootstrap;

internal interface IWindowsEnvironmentEventBridge : IAsyncDisposable
{
    void Start();

    void RequestEnvironmentValidation();
}

internal sealed class WindowsDesktopEventBridge : IWindowsEnvironmentEventBridge
{
    private readonly WindowsDesktopHost desktopHost;
    private readonly WindowsDisplayTopologySource displayTopologySource;
    private readonly HostCommandLoop commandLoop;
    private readonly DisplayTopologyEventForwarder displayTopologyForwarder;
    private readonly IExplorerChangeMonitor explorerMonitor;
    private readonly IWindowsSystemStateSource systemStateSource;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object systemStateHistoryGate = new();
    private readonly List<SystemStateChange> systemStateHistory = [];
    private Task? signalTask;
    private bool started;
    private bool stopping;
    private bool disposed;
    private long rawTaskbarSignalCount;
    private long validatedShellChangeCount;
    private long forwardedRecoveryCount;
    private ExplorerValidationFailure? lastExplorerValidationFailure;
    private ExplorerEventForwardingFailure? lastExplorerForwardingFailure;
    private SystemStateForwardingFailure? lastSystemStateForwardingFailure;
    private long forwardedSystemStateCount;

    public WindowsDesktopEventBridge(
        WindowsDesktopHost desktopHost,
        WindowsDisplayTopologySource displayTopologySource,
        HostCommandLoop commandLoop)
    {
        this.desktopHost = desktopHost ?? throw new ArgumentNullException(nameof(desktopHost));
        this.displayTopologySource = displayTopologySource ??
            throw new ArgumentNullException(nameof(displayTopologySource));
        this.commandLoop = commandLoop ?? throw new ArgumentNullException(nameof(commandLoop));
        displayTopologyForwarder = new DisplayTopologyEventForwarder(commandLoop);
        explorerMonitor = new ExplorerMonitor(() => desktopHost.CurrentAttachPointHandle);
        systemStateSource = new WindowsSystemStateSource();
    }

    internal WindowsDesktopEventBridge(
        WindowsDesktopHost desktopHost,
        WindowsDisplayTopologySource displayTopologySource,
        HostCommandLoop commandLoop,
        IExplorerChangeMonitor explorerMonitor,
        IWindowsSystemStateSource? systemStateSource = null)
        : this(desktopHost, displayTopologySource, commandLoop)
    {
        this.explorerMonitor.Dispose();
        this.explorerMonitor = explorerMonitor ??
            throw new ArgumentNullException(nameof(explorerMonitor));
        if (systemStateSource is not null)
        {
            this.systemStateSource = systemStateSource;
        }
    }

    internal DisplayTopologyForwardingFailure? LastDisplayTopologyForwardingFailure =>
        displayTopologyForwarder.LastFailure;

    internal ExplorerValidationFailure? LastExplorerValidationFailure =>
        Volatile.Read(ref lastExplorerValidationFailure);

    internal ExplorerEventForwardingFailure? LastExplorerForwardingFailure =>
        Volatile.Read(ref lastExplorerForwardingFailure);

    internal long RawTaskbarSignalCount => Interlocked.Read(ref rawTaskbarSignalCount);

    internal long ValidatedShellChangeCount => Interlocked.Read(ref validatedShellChangeCount);

    internal long ForwardedRecoveryCount => Interlocked.Read(ref forwardedRecoveryCount);

    internal long ForwardedSystemStateCount => Interlocked.Read(ref forwardedSystemStateCount);

    internal SystemStateForwardingFailure? LastSystemStateForwardingFailure =>
        Volatile.Read(ref lastSystemStateForwardingFailure);

    internal IReadOnlyList<SystemStateChange> GetSystemStateHistoryForDiagnostics()
    {
        lock (systemStateHistoryGate)
        {
            return systemStateHistory.ToArray();
        }
    }

    internal event EventHandler<ExplorerValidationFailure>? ExplorerValidationFailed;

    internal event EventHandler<ExplorerEventForwardingFailure>? ExplorerForwardingFailed;

    public void Start()
    {
        if (started)
        {
            throw new InvalidOperationException("The Windows desktop event bridge is already running.");
        }

        started = true;
        explorerMonitor.Restarted += OnExplorerRestarted;
        explorerMonitor.ValidationFailed += OnExplorerValidationFailed;
        explorerMonitor.ValidationCompleted += OnExplorerValidationCompleted;
        displayTopologySource.Changed += OnDisplayTopologyChanged;
        systemStateSource.Changed += OnSystemStateChanged;
        explorerMonitor.Start();
        ForwardSystemState(systemStateSource.Current);
        signalTask = ConsumeWindowSignalsAsync(shutdown.Token);
    }

    public void RequestEnvironmentValidation()
    {
        if (!started)
        {
            throw new InvalidOperationException("The Windows desktop event bridge is not running.");
        }

        explorerMonitor.RequestValidation();
        displayTopologySource.Refresh();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        stopping = true;
        if (!started)
        {
            explorerMonitor.Dispose();
            shutdown.Dispose();
            return;
        }

        explorerMonitor.Restarted -= OnExplorerRestarted;
        explorerMonitor.ValidationFailed -= OnExplorerValidationFailed;
        explorerMonitor.ValidationCompleted -= OnExplorerValidationCompleted;
        displayTopologySource.Changed -= OnDisplayTopologyChanged;
        systemStateSource.Changed -= OnSystemStateChanged;
        explorerMonitor.Stop();
        shutdown.Cancel();
        if (signalTask is not null)
        {
            try
            {
                await signalTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
        }

        explorerMonitor.Dispose();
        shutdown.Dispose();
    }

    private async Task ConsumeWindowSignalsAsync(CancellationToken cancellationToken)
    {
        await foreach (DesktopWindowSignal signal in desktopHost
                           .ReadWindowSignalsAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            ProcessWindowSignal(signal);
        }
    }

    internal void ProcessWindowSignalForTests(DesktopWindowSignal signal) =>
        ProcessWindowSignal(signal);

    private void ProcessWindowSignal(DesktopWindowSignal signal)
    {
        switch (signal.Kind)
        {
            case DesktopWindowSignalKind.TaskbarCreated:
                Interlocked.Increment(ref rawTaskbarSignalCount);
                explorerMonitor.RequestValidation();
                break;
            case DesktopWindowSignalKind.DisplayChanged:
            case DesktopWindowSignalKind.DpiChanged:
                displayTopologySource.RequestTransitionRefresh();
                break;
            case DesktopWindowSignalKind.SessionLocked:
            case DesktopWindowSignalKind.SessionUnlocked:
            case DesktopWindowSignalKind.SessionDisconnected:
            case DesktopWindowSignalKind.SessionReconnected:
            case DesktopWindowSignalKind.RemoteSessionChanged:
            case DesktopWindowSignalKind.DisplayPoweredOff:
            case DesktopWindowSignalKind.DisplayPoweredOn:
            case DesktopWindowSignalKind.SystemSuspending:
            case DesktopWindowSignalKind.SystemResuming:
            case DesktopWindowSignalKind.PowerSourceChanged:
                systemStateSource.ProcessSignal(signal);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown Desktop Window signal '{signal.Kind}'.");
        }
    }

    private void OnExplorerRestarted(object? sender, ExplorerRestartedEventArgs args)
    {
        Interlocked.Increment(ref validatedShellChangeCount);
        ExplorerEventForwardingFailureKind? failureKind = stopping ||
            commandLoop.IsStoppingForEnvironmentEvents
                ? ExplorerEventForwardingFailureKind.HostStopping
                : commandLoop.TryPost(new ExplorerRestartedCommand())
                    ? null
                    : ExplorerEventForwardingFailureKind.CommandChannelClosed;
        if (failureKind is null)
        {
            Interlocked.Increment(ref forwardedRecoveryCount);
            return;
        }

        ExplorerEventForwardingFailure failure = new(
            failureKind.Value,
            args.Reason,
            DateTimeOffset.UtcNow);
        Volatile.Write(ref lastExplorerForwardingFailure, failure);
        ExplorerForwardingFailed?.Invoke(this, failure);
    }

    private void OnExplorerValidationFailed(
        object? sender,
        ExplorerValidationFailedEventArgs args)
    {
        ExplorerValidationFailure failure = new(
            DateTimeOffset.UtcNow,
            args.Exception.GetType().FullName ?? args.Exception.GetType().Name,
            args.Exception.Message);
        Volatile.Write(ref lastExplorerValidationFailure, failure);
        ExplorerValidationFailed?.Invoke(this, failure);
    }

    private void OnExplorerValidationCompleted(
        object? sender,
        ExplorerValidationCompletedEventArgs args) =>
        systemStateSource.CompleteEnvironmentValidation(args.ShellAvailable);

    private void OnSystemStateChanged(object? sender, SystemStateChangedEventArgs args)
    {
        ForwardSystemState(args.Current);
        if (!args.Current.RequiresEnvironmentValidation)
        {
            return;
        }

        // Refresh is synchronous. Playback remains suspended until the debounced Shell
        // validation completes and releases the source's pending resume snapshot.
        displayTopologySource.Refresh();
        explorerMonitor.RequestValidation();
    }

    private void ForwardSystemState(SystemStateChange change)
    {
        SystemStateForwardingFailureKind? failureKind = stopping ||
            commandLoop.IsStoppingForEnvironmentEvents
                ? SystemStateForwardingFailureKind.HostStopping
                : commandLoop.TryPost(new SystemStateChangedCommand(change))
                    ? null
                    : SystemStateForwardingFailureKind.CommandChannelClosed;
        if (failureKind is null)
        {
            Interlocked.Increment(ref forwardedSystemStateCount);
            lock (systemStateHistoryGate)
            {
                systemStateHistory.Add(change);
                if (systemStateHistory.Count > 64)
                {
                    systemStateHistory.RemoveRange(0, systemStateHistory.Count - 64);
                }
            }
            return;
        }

        Volatile.Write(
            ref lastSystemStateForwardingFailure,
            new SystemStateForwardingFailure(
                failureKind.Value,
                change.Revision,
                change.Reason,
                DateTimeOffset.UtcNow));
    }

    private void OnDisplayTopologyChanged(object? sender, DisplayTopologyChangedEventArgs args) =>
        displayTopologyForwarder.TryForward(args.Current);
}

internal sealed record ExplorerValidationFailure(
    DateTimeOffset OccurredAt,
    string ExceptionType,
    string Message);

internal enum ExplorerEventForwardingFailureKind
{
    HostStopping,
    CommandChannelClosed,
}

internal sealed record ExplorerEventForwardingFailure(
    ExplorerEventForwardingFailureKind Kind,
    ExplorerChangeReason ChangeReason,
    DateTimeOffset OccurredAt);

internal enum SystemStateForwardingFailureKind
{
    HostStopping,
    CommandChannelClosed,
}

internal sealed record SystemStateForwardingFailure(
    SystemStateForwardingFailureKind Kind,
    long Revision,
    SystemStateChangeReason Reason,
    DateTimeOffset OccurredAt);
