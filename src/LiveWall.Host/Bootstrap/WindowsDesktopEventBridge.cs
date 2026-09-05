using LiveWall.Application.Sessions;
using LiveWall.Host.CommandLoop;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Displays;

namespace LiveWall.Host.Bootstrap;

internal sealed class WindowsDesktopEventBridge : IAsyncDisposable
{
    private readonly WindowsDesktopHost desktopHost;
    private readonly WindowsDisplayTopologySource displayTopologySource;
    private readonly HostCommandLoop commandLoop;
    private readonly ExplorerMonitor explorerMonitor;
    private readonly CancellationTokenSource shutdown = new();
    private Task? signalTask;
    private bool started;

    public WindowsDesktopEventBridge(
        WindowsDesktopHost desktopHost,
        WindowsDisplayTopologySource displayTopologySource,
        HostCommandLoop commandLoop)
    {
        this.desktopHost = desktopHost ?? throw new ArgumentNullException(nameof(desktopHost));
        this.displayTopologySource = displayTopologySource ??
            throw new ArgumentNullException(nameof(displayTopologySource));
        this.commandLoop = commandLoop ?? throw new ArgumentNullException(nameof(commandLoop));
        explorerMonitor = new ExplorerMonitor(() => desktopHost.CurrentAttachPointHandle);
    }

    public void Start()
    {
        if (started)
        {
            throw new InvalidOperationException("The Windows desktop event bridge is already running.");
        }

        started = true;
        explorerMonitor.Restarted += OnExplorerRestarted;
        displayTopologySource.Changed += OnDisplayTopologyChanged;
        explorerMonitor.Start();
        signalTask = ConsumeWindowSignalsAsync(shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (!started)
        {
            explorerMonitor.Dispose();
            shutdown.Dispose();
            return;
        }

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

        explorerMonitor.Restarted -= OnExplorerRestarted;
        displayTopologySource.Changed -= OnDisplayTopologyChanged;
        explorerMonitor.Dispose();
        shutdown.Dispose();
    }

    private async Task ConsumeWindowSignalsAsync(CancellationToken cancellationToken)
    {
        await foreach (DesktopWindowSignal signal in desktopHost
                           .ReadWindowSignalsAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            switch (signal.Kind)
            {
                case DesktopWindowSignalKind.TaskbarCreated:
                    explorerMonitor.RequestValidation();
                    break;
                case DesktopWindowSignalKind.DisplayChanged:
                case DesktopWindowSignalKind.DpiChanged:
                    displayTopologySource.RequestRefresh();
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown Desktop Window signal '{signal.Kind}'.");
            }
        }
    }

    private void OnExplorerRestarted(object? sender, ExplorerRestartedEventArgs args) =>
        commandLoop.TryPost(new ExplorerRestartedCommand());

    private void OnDisplayTopologyChanged(object? sender, DisplayTopologyChangedEventArgs args) =>
        commandLoop.TryPost(new DisplayTopologyChangedCommand(args.Current));
}
