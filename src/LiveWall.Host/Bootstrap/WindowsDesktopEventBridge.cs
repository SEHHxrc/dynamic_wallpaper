using LiveWall.Application.Sessions;
using LiveWall.Application.Policies;
using LiveWall.Host.CommandLoop;
using LiveWall.Host.Orchestration;
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
    private readonly List<SessionEventEvidence> sessionEventHistory = [];
    private long sessionEventSequence;
    private readonly long sessionJournalStarted = System.Diagnostics.Stopwatch.GetTimestamp();

    internal IReadOnlyList<SessionEventEvidence> GetSessionEventHistoryForDiagnostics()
    {
        lock (systemStateHistoryGate)
        {
            return sessionEventHistory.ToArray();
        }
    }
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
    private readonly TimeSpan sessionValidationBudget = TimeSpan.FromSeconds(10);
    private readonly bool consumeNativeWindowSignals = true;

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
        IWindowsSystemStateSource? systemStateSource = null,
        bool consumeNativeWindowSignals = true,
        TimeSpan? sessionValidationBudget = null)
        : this(desktopHost, displayTopologySource, commandLoop)
    {
        this.consumeNativeWindowSignals = consumeNativeWindowSignals;
        this.sessionValidationBudget = sessionValidationBudget ?? TimeSpan.FromSeconds(10);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.sessionValidationBudget, TimeSpan.Zero);
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
        if (consumeNativeWindowSignals)
        {
            signalTask = ConsumeWindowSignalsAsync(shutdown.Token);
        }
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
        if (signal.Kind is DesktopWindowSignalKind.SessionDisconnected or
            DesktopWindowSignalKind.SessionReconnected)
        {
            bool matching = signal.SessionId is null ||
                signal.SessionId == System.Diagnostics.Process.GetCurrentProcess().SessionId;
            lock (systemStateHistoryGate)
            {
                sessionEventHistory.Add(new SessionEventEvidence(++sessionEventSequence,
                    signal.Kind, matching, systemStateSource.Current.Revision,
                    stopping || commandLoop.IsStoppingForEnvironmentEvents,
                    System.Diagnostics.Stopwatch.GetElapsedTime(sessionJournalStarted)));
                if (sessionEventHistory.Count > 64)
                {
                    sessionEventHistory.RemoveAt(0);
                }
            }
            if (!matching)
            {
                return;
            }
        }
        if (stopping || commandLoop.IsStoppingForEnvironmentEvents)
        {
            Volatile.Write(ref lastSystemStateForwardingFailure,
                new SystemStateForwardingFailure(SystemStateForwardingFailureKind.HostStopping,
                    systemStateSource.Current.Revision, systemStateSource.Current.Reason, DateTimeOffset.UtcNow));
            return;
        }
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
        RejectSessionValidation(SessionEnvironmentFailureReason.ValidationFailed, args.SystemRevision);
        ExplorerValidationFailure failure = new(
            DateTimeOffset.UtcNow,
            args.Exception.GetType().FullName ?? args.Exception.GetType().Name,
            args.Exception.Message);
        Volatile.Write(ref lastExplorerValidationFailure, failure);
        ExplorerValidationFailed?.Invoke(this, failure);
    }

    private void OnExplorerValidationCompleted(
        object? sender,
        ExplorerValidationCompletedEventArgs args)
    {
        if (!stopping && !commandLoop.IsStoppingForEnvironmentEvents)
        {
            systemStateSource.CompleteEnvironmentValidation(args.ShellAvailable, args.SystemRevision);
        }
    }

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
        explorerMonitor.RequestValidation(args.Current.Revision);
        if (args.Current.Snapshot.IsSessionDisconnected)
        {
            _ = ObserveSessionValidationDeadlineAsync(args.Current.Revision);
        }
    }

    private async Task ObserveSessionValidationDeadlineAsync(long revision)
    {
        try
        {
            await Task.Delay(sessionValidationBudget, shutdown.Token).ConfigureAwait(false);
            if (systemStateSource.RejectEnvironmentValidation(revision))
            {
                ForwardSessionValidationFailure(revision, SessionEnvironmentFailureReason.ValidationTimeout);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private void RejectSessionValidation(SessionEnvironmentFailureReason reason, long expectedRevision)
    {
        SystemStateChange current = systemStateSource.Current;
        if (current.Snapshot.IsSessionDisconnected &&
            systemStateSource.RejectEnvironmentValidation(expectedRevision))
        {
            ForwardSessionValidationFailure(current.Revision, reason);
        }
    }

    private void ForwardSessionValidationFailure(long revision, SessionEnvironmentFailureReason reason)
    {
        SystemStateForwardingFailureKind? failure = stopping || commandLoop.IsStoppingForEnvironmentEvents
            ? SystemStateForwardingFailureKind.HostStopping
            : commandLoop.TryPost(new SessionEnvironmentFailedCommand(revision, reason))
                ? null : SystemStateForwardingFailureKind.CommandChannelClosed;
        if (failure is not null)
        {
            Volatile.Write(ref lastSystemStateForwardingFailure, new SystemStateForwardingFailure(
                failure.Value, revision, SystemStateChangeReason.SessionReconnectRequested, DateTimeOffset.UtcNow));
        }
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

internal sealed record SessionEventEvidence(long Sequence, DesktopWindowSignalKind Kind,
    bool CurrentSessionMatches, long SystemRevisionBeforeEvent, bool HostStopping, TimeSpan Elapsed);
