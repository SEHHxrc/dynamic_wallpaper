using LiveWall.Application.Sessions;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Displays;

namespace LiveWall.DesktopHostDiagnostics;

internal static class Program
{
    private const int DefaultColorBlockSeconds = 10;
    private const int MaximumColorBlockSeconds = 30;
    private const int ShellUnavailableExitCode = 3;
    private const int AttachPointUnavailableExitCode = 4;
    private const int RaisedDesktopRejectedExitCode = 5;
    private const int CleanupIncompleteExitCode = 6;
    private const int ExplorerRecoveryFailedExitCode = 7;
    private static readonly uint[] TestColors =
    [
        0x00FF00FF,
        0x0000FFFF,
        0x00FFFF00,
        0x000080FF,
    ];

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            DiagnosticOptions options = DiagnosticOptions.Parse(args);
            using WindowsDisplayTopologySource displaySource = new();
            DisplayTopology topology = displaySource.Current;
            PrintTopology(topology);
            if (options.RaisedDesktopProbeEnabled || options.PresentationKind is not null ||
                options.ExplorerRecoveryProbeEnabled)
            {
                return await RunRaisedDesktopDiagnosticAsync(topology, options)
                    .ConfigureAwait(false);
            }

            ShellTopologySnapshot? shellTopology = null;
            if (options.ShellTopologyEnabled || options.ColorBlockEnabled)
            {
                shellTopology = WindowsShellTopologyDiagnostics.CaptureSnapshot();
                PrintShellTopology(shellTopology);
            }

            if (shellTopology?.Availability == ShellTopologyAvailability.ShellUnavailable)
            {
                Console.Error.WriteLine(
                    "Inconclusive / Shell unavailable: this process desktop cannot access the Explorer Shell.");
                return ShellUnavailableExitCode;
            }

            if (!options.ColorBlockEnabled)
            {
                return topology.Displays.Count > 0 ? 0 : 2;
            }

            WindowsBuildIdentity buildIdentity = shellTopology?.BuildIdentity ??
                WindowsShellTopologyDiagnostics.CaptureBuildIdentity();
            return await RunColorBlockAsync(
                    topology,
                    buildIdentity,
                    options.DurationSeconds)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Color block diagnostic canceled; temporary windows were removed.");
            return 130;
        }
        catch (DesktopAttachPointUnavailableException exception)
        {
            Console.Error.WriteLine(
                $"Not supported / No compliant desktop attach point: {exception.Message}");
            return AttachPointUnavailableExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Desktop Host diagnostics failed: {FormatException(exception)}");
            return 1;
        }
    }

    private static void PrintTopology(DisplayTopology topology)
    {
        Console.WriteLine($"Topology revision: {topology.Revision}");
        Console.WriteLine($"Displays: {topology.Displays.Count}");
        foreach (DisplayDescriptor display in topology.Displays)
        {
            Console.WriteLine(
                $"{display.Id} | {display.Bounds.X},{display.Bounds.Y} " +
                $"{display.Bounds.Width}x{display.Bounds.Height} | " +
                $"scale={display.ScaleFactor:0.##} | {display.RefreshRateHz} Hz | " +
                $"primary={display.IsPrimary}");
        }
    }

    private static void PrintShellTopology(ShellTopologySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Console.WriteLine(
            $"Windows version: {snapshot.BuildIdentity.Version} | " +
            $"displayVersion={snapshot.BuildIdentity.DisplayVersion ?? "unavailable"} | " +
            $"fullBuild={snapshot.BuildIdentity.FullBuild} | " +
            $"buildLab={snapshot.BuildIdentity.BuildLabIdentity ?? "unavailable"}");
        Console.WriteLine(
            $"Process context: session={snapshot.ExecutionContext.SessionId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"} | " +
            $"windowStation={snapshot.ExecutionContext.WindowStationName ?? "unavailable"} | " +
            $"desktop={snapshot.ExecutionContext.DesktopName ?? "unavailable"}");
        Console.WriteLine($"Shell status: {snapshot.Availability}");
        Console.WriteLine($"Shell anchor: 0x{snapshot.ShellWindowHandle:X}");
        PrintShellWindows("Top-level Shell windows", snapshot.TopLevelWindows);
        PrintShellWindows("Shell descendants", snapshot.ShellDescendants);
    }

    private static void PrintShellWindows(
        string label,
        IReadOnlyList<ShellWindowFingerprint> windows)
    {
        Console.WriteLine($"{label}: {windows.Count}");
        foreach (ShellWindowFingerprint window in windows)
        {
            string bounds = window.Bounds is null
                ? "unavailable"
                : $"{window.Bounds.X},{window.Bounds.Y} " +
                    $"{window.Bounds.Width}x{window.Bounds.Height}";
            Console.WriteLine(
                $"scope={window.Scope} | depth={window.Depth} | " +
                $"siblingZ={window.SiblingZOrderIndex} | 0x{window.WindowHandle:X} | " +
                $"class={window.ClassName} | process={window.ProcessName ?? "unavailable"} " +
                $"({window.ProcessId}:{window.ThreadId}) | visible={window.IsVisible} | " +
                $"bounds={bounds} | parent=0x{window.ParentWindowHandle:X} | " +
                $"owner=0x{window.OwnerWindowHandle:X} | style=0x{window.Style:X} | " +
                $"exStyle=0x{window.ExtendedStyle:X} | " +
                $"nextSibling=0x{window.NextSiblingWindowHandle:X} | " +
                $"defView=0x{window.ShellDefViewWindowHandle:X} | " +
                $"sysListView=0x{window.SysListViewWindowHandle:X} | " +
                $"nextWorker=0x{window.NextWorkerWindowHandle:X}");
        }
    }

    private static async Task<int> RunColorBlockAsync(
        DisplayTopology topology,
        WindowsBuildIdentity buildIdentity,
        int durationSeconds)
    {
        if (topology.Displays.Count == 0)
        {
            return 2;
        }

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        WindowsShellSnapshot shell = new(
            buildIdentity.Version,
            buildIdentity.Build,
            RaisedDesktopEnabled: false,
            buildIdentity.UpdateBuildRevision);
        await using WindowsDesktopHost desktopHost =
            WindowsDesktopHost.CreateExperimentalDiagnostics(shell);
        List<DesktopSurface> surfaces = [];
        List<DesktopColorBlockSession> colorBlocks = [];
        Exception? operationFailure = null;

        try
        {
            DesktopTopology desktopTopology = await desktopHost.EnsureTopologyAsync(
                    topology,
                    cancellation.Token)
                .ConfigureAwait(false);
            for (int index = 0; index < topology.Displays.Count; index++)
            {
                DisplayDescriptor display = topology.Displays[index];
                DesktopSurface surface = await desktopHost.CreateSurfaceAsync(
                        new SurfaceRequest([display.Id], display.Bounds, FitMode.Cover),
                        cancellation.Token)
                    .ConfigureAwait(false);
                surfaces.Add(surface);
                colorBlocks.Add(await DesktopColorBlockSession.CreateAsync(
                        surface.WindowHandle,
                        display.Bounds.Width,
                        display.Bounds.Height,
                        TestColors[index % TestColors.Length],
                        cancellation.Token)
                    .ConfigureAwait(false));
            }

            await desktopHost.ReplaceSurfacesAsync(
                    new SurfaceReplacement(
                        surfaces.Select(surface => surface.Id).ToArray(),
                        [],
                        desktopTopology.Revision),
                    cancellation.Token)
                .ConfigureAwait(false);
            Console.WriteLine(
                $"LIVEWALL TEST color blocks are active for {durationSeconds} seconds. " +
                "Confirm that desktop icons and the taskbar remain above them.");
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds), cancellation.Token)
                .ConfigureAwait(false);
            Console.WriteLine("Color block interval completed; cleaning up temporary windows.");
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        Console.CancelKeyPress -= cancelHandler;
        List<Exception> cleanupFailures = [];
        for (int index = colorBlocks.Count - 1; index >= 0; index--)
        {
            try
            {
                await colorBlocks[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        foreach (DesktopSurface surface in surfaces)
        {
            try
            {
                await desktopHost.DestroySurfaceAsync(surface.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (cleanupFailures.Count > 0)
        {
            if (operationFailure is not null)
            {
                cleanupFailures.Insert(0, operationFailure);
            }

            throw new AggregateException(
                "One or more diagnostic windows could not be removed.",
                cleanupFailures);
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        return 0;
    }

    private static async Task<int> RunRaisedDesktopDiagnosticAsync(
        DisplayTopology topology,
        DiagnosticOptions options)
    {
        if (topology.Displays.Count == 0)
        {
            return 2;
        }

        int left = topology.Displays.Min(display => display.Bounds.X);
        int top = topology.Displays.Min(display => display.Bounds.Y);
        int right = topology.Displays.Max(display =>
            checked(display.Bounds.X + display.Bounds.Width));
        int bottom = topology.Displays.Max(display =>
            checked(display.Bounds.Y + display.Bounds.Height));
        ShellWindowBounds virtualBounds = new(
            left,
            top,
            checked(right - left),
            checked(bottom - top));
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (options.ExplorerRecoveryProbeEnabled)
            {
                return await RunRaisedDesktopExplorerRecoveryAsync(
                        virtualBounds,
                        options.DurationSeconds,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            Action<RaisedDesktopSnapshotStage, ShellTopologySnapshot> observer =
                (stage, snapshot) =>
                {
                    Console.WriteLine($"=== Raised Desktop snapshot: {stage} ===");
                    PrintShellTopology(snapshot);
                };
            RaisedDesktopDiagnosticResult result = options.PresentationKind is { } presentationKind
                ? await RaisedDesktopDiagnostics.RunPresentationProbeAsync(
                        virtualBounds,
                        TestColors[0],
                        presentationKind,
                        TimeSpan.FromSeconds(options.DurationSeconds),
                        observer,
                        cancellation.Token)
                    .ConfigureAwait(false)
                : await RaisedDesktopDiagnostics.RunProbeAsync(
                        virtualBounds,
                        observer,
                        cancellation.Token)
                    .ConfigureAwait(false);
            PrintRaisedDesktopResult(result);
            if (result.Status == RaisedDesktopDiagnosticStatus.CleanupIncomplete)
            {
                return CleanupIncompleteExitCode;
            }

            if (result.Status == RaisedDesktopDiagnosticStatus.Rejected)
            {
                return result.Rejections.Any(rejection =>
                    rejection.Code == RaisedDesktopRejectionCode.ShellUnavailable)
                    ? ShellUnavailableExitCode
                    : RaisedDesktopRejectedExitCode;
            }

            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> RunRaisedDesktopExplorerRecoveryAsync(
        ShellWindowBounds virtualBounds,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<ShellTopologySnapshot> activeSurface =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellTopologySnapshot? initialBefore = null;
        Action<RaisedDesktopSnapshotStage, ShellTopologySnapshot> initialObserver =
            (stage, snapshot) =>
            {
                Console.WriteLine($"=== Explorer recovery initial snapshot: {stage} ===");
                PrintShellTopology(snapshot);
                if (stage == RaisedDesktopSnapshotStage.Before)
                {
                    initialBefore = snapshot;
                }
                else if (stage == RaisedDesktopSnapshotStage.ColorBlockActive)
                {
                    activeSurface.TrySetResult(snapshot);
                }
            };

        Task<RaisedDesktopDiagnosticResult> initialProbe =
            RaisedDesktopDiagnostics.RunPresentationProbeAsync(
                virtualBounds,
                TestColors[0],
                RaisedDesktopPresentationKind.DirectCompositionSwapChain,
                TimeSpan.FromSeconds(durationSeconds),
                initialObserver,
                cancellationToken);

        Task timeout = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        Task firstCompletion = await Task.WhenAny(activeSurface.Task, initialProbe, timeout)
            .ConfigureAwait(false);
        if (firstCompletion == initialProbe)
        {
            RaisedDesktopDiagnosticResult rejected = await initialProbe.ConfigureAwait(false);
            Console.WriteLine("=== Initial probe ended before Explorer restart ===");
            PrintRaisedDesktopResult(rejected);
            return ExplorerRecoveryFailedExitCode;
        }
        if (firstCompletion == timeout)
        {
            throw new TimeoutException(
                "The initial DirectComposition Surface did not become active within 10 seconds.");
        }

        ShellTopologySnapshot active = await activeSurface.Task.ConfigureAwait(false);
        ShellWindowFingerprint oldProgman = FindProgman(active);
        Console.WriteLine(
            $"EXPLICIT EXPLORER RESTART: stopping Shell PID {oldProgman.ProcessId}; " +
            $"oldShell=0x{active.ShellWindowHandle:X}.");
        await RestartExplorerAsync(oldProgman.ProcessId, cancellationToken)
            .ConfigureAwait(false);

        ShellTopologySnapshot recovered = await WaitForNewShellGenerationAsync(
                active.ShellWindowHandle,
                oldProgman.ProcessId,
                cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine("=== New Explorer Shell generation detected ===");
        PrintShellTopology(recovered);

        RaisedDesktopDiagnosticResult initialResult = await initialProbe.ConfigureAwait(false);
        Console.WriteLine("=== Initial probe result after Explorer restart ===");
        PrintRaisedDesktopResult(initialResult);

        Action<RaisedDesktopSnapshotStage, ShellTopologySnapshot> recoveredObserver =
            (stage, snapshot) =>
            {
                Console.WriteLine($"=== Explorer recovery rebuilt snapshot: {stage} ===");
                PrintShellTopology(snapshot);
            };
        RaisedDesktopDiagnosticResult rebuilt =
            await RaisedDesktopDiagnostics.RunPresentationProbeAsync(
                    virtualBounds,
                    TestColors[1],
                    RaisedDesktopPresentationKind.DirectCompositionSwapChain,
                    TimeSpan.FromSeconds(durationSeconds),
                    recoveredObserver,
                    cancellationToken)
                .ConfigureAwait(false);
        Console.WriteLine("=== Rebuilt probe result ===");
        PrintRaisedDesktopResult(rebuilt);

        ShellWindowFingerprint newProgman = FindProgman(recovered);
        bool generationChanged = initialBefore is not null &&
            initialBefore.ShellWindowHandle != recovered.ShellWindowHandle &&
            oldProgman.ProcessId != newProgman.ProcessId;
        bool rebuiltSuccessfully = rebuilt.Status ==
                RaisedDesktopDiagnosticStatus.PresentationProbeCompleted &&
            rebuilt.SurfaceCreated &&
            rebuilt.OwnedResourcesCleanup == RaisedDesktopOwnedResourcesCleanup.Complete;
        Console.WriteLine(
            $"Explorer recovery result: generationChanged={generationChanged} | " +
            $"oldPid={oldProgman.ProcessId} | newPid={newProgman.ProcessId} | " +
            $"oldShell=0x{active.ShellWindowHandle:X} | " +
            $"newShell=0x{recovered.ShellWindowHandle:X} | rebuilt={rebuiltSuccessfully}");
        return generationChanged && rebuiltSuccessfully
            ? 0
            : ExplorerRecoveryFailedExitCode;
    }

    private static ShellWindowFingerprint FindProgman(ShellTopologySnapshot snapshot) =>
        snapshot.TopLevelWindows.Single(window =>
            window.WindowHandle == snapshot.ShellWindowHandle &&
            string.Equals(window.ClassName, "Progman", StringComparison.Ordinal));

    private static async Task RestartExplorerAsync(
        uint shellProcessId,
        CancellationToken cancellationToken)
    {
        using Process explorer = Process.GetProcessById(checked((int)shellProcessId));
        explorer.Kill(entireProcessTree: false);
        await explorer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        DateTime automaticRestartDeadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < automaticRestartDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShellTopologySnapshot snapshot = WindowsShellTopologyDiagnostics.CaptureSnapshot();
            if (snapshot.Availability == ShellTopologyAvailability.Available)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        }

        using Process? started = Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
        });
    }

    private static async Task<ShellTopologySnapshot> WaitForNewShellGenerationAsync(
        ulong oldShellWindowHandle,
        uint oldProcessId,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShellTopologySnapshot snapshot = WindowsShellTopologyDiagnostics.CaptureSnapshot();
            if (snapshot.Availability == ShellTopologyAvailability.Available &&
                snapshot.ShellWindowHandle != oldShellWindowHandle)
            {
                ShellWindowFingerprint? progman = snapshot.TopLevelWindows.SingleOrDefault(window =>
                    window.WindowHandle == snapshot.ShellWindowHandle &&
                    string.Equals(window.ClassName, "Progman", StringComparison.Ordinal));
                if (progman is not null && progman.ProcessId != oldProcessId)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken)
                        .ConfigureAwait(false);
                    ShellTopologySnapshot stable = WindowsShellTopologyDiagnostics.CaptureSnapshot();
                    if (stable.Availability == ShellTopologyAvailability.Available &&
                        stable.ShellWindowHandle == snapshot.ShellWindowHandle)
                    {
                        return stable;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("A stable new Explorer Shell generation did not appear within 20 seconds.");
    }

    private static void PrintRaisedDesktopResult(RaisedDesktopDiagnosticResult result)
    {
        Console.WriteLine($"Raised Desktop status: {result.Status}");
        Console.WriteLine(
            $"requestSent={result.RequestSent} | surfaceCreated={result.SurfaceCreated} | " +
            $"presentationKind={result.PresentationKind?.ToString() ?? "none"}");
        Console.WriteLine(
            $"ownedResourcesCleanup={result.OwnedResourcesCleanup} | " +
            $"shellMutationRecovery={result.ShellMutationRecovery}");
        Console.WriteLine($"beforeFingerprint={result.BeforeFingerprint.Value}");
        Console.WriteLine($"afterFingerprint={result.AfterFingerprint.Value}");
        Console.WriteLine($"cleanupFingerprint={result.CleanupFingerprint.Value}");
        foreach (RaisedDesktopRejection rejection in result.Rejections)
        {
            Console.WriteLine($"reject={rejection.Code} | {rejection.Detail}");
        }
    }

    private static string FormatException(Exception exception)
    {
        List<string> messages = [];
        AppendException(exception, messages);
        return string.Join(" | ", messages);
    }

    private static void AppendException(Exception exception, List<string> messages)
    {
        string nativeCode = exception is System.ComponentModel.Win32Exception win32
            ? $" (nativeError={win32.NativeErrorCode})"
            : string.Empty;
        messages.Add($"{exception.GetType().Name}{nativeCode}: {exception.Message}");
        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                AppendException(inner, messages);
            }
        }
        else if (exception.InnerException is not null)
        {
            AppendException(exception.InnerException, messages);
        }
    }

    private sealed record DiagnosticOptions(
        bool ShellTopologyEnabled,
        bool ColorBlockEnabled,
        bool RaisedDesktopProbeEnabled,
        bool ExplorerRecoveryProbeEnabled,
        RaisedDesktopPresentationKind? PresentationKind,
        int DurationSeconds)
    {
        public static DiagnosticOptions Parse(string[] args)
        {
            bool colorBlock = false;
            bool shellTopology = false;
            bool raisedDesktopProbe = false;
            bool explorerRecoveryProbe = false;
            bool confirmExplorerRestart = false;
            RaisedDesktopPresentationKind? raisedDesktopPresentationKind = null;
            bool durationSpecified = false;
            int duration = DefaultColorBlockSeconds;
            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--color-block":
                        colorBlock = true;
                        break;
                    case "--shell-topology":
                        shellTopology = true;
                        break;
                    case "--raised-desktop-probe":
                        raisedDesktopProbe = true;
                        break;
                    case "--raised-desktop-explorer-recovery-probe":
                        explorerRecoveryProbe = true;
                        break;
                    case "--confirm-explorer-restart":
                        confirmExplorerRestart = true;
                        break;
                    case "--raised-desktop-color-block":
                        raisedDesktopPresentationKind = RaisedDesktopPresentationKind.LayeredGdi;
                        break;
                    case "--raised-desktop-presentation-probe" when index + 1 < args.Length:
                        raisedDesktopPresentationKind = args[++index] switch
                        {
                            "layered-gdi" => RaisedDesktopPresentationKind.LayeredGdi,
                            "non-layered-gdi" => RaisedDesktopPresentationKind.NonLayeredGdi,
                            "direct-composition-swap-chain" =>
                                RaisedDesktopPresentationKind.DirectCompositionSwapChain,
                            string value => throw new ArgumentException(
                                $"Unknown Raised Desktop presentation kind '{value}'."),
                        };
                        break;
                    case "--duration-seconds" when index + 1 < args.Length:
                        durationSpecified = true;
                        if (!int.TryParse(
                                args[++index],
                                System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out duration) ||
                            duration is < 1 or > MaximumColorBlockSeconds)
                        {
                            throw new ArgumentException(
                                $"Color block duration must be between 1 and {MaximumColorBlockSeconds} seconds.");
                        }

                        break;
                    default:
                        throw new ArgumentException($"Unknown diagnostics option '{args[index]}'.");
                }
            }

            if (!colorBlock && raisedDesktopPresentationKind is null &&
                !explorerRecoveryProbe && durationSpecified)
            {
                throw new ArgumentException(
                    "--duration-seconds requires --color-block, --raised-desktop-color-block, " +
                    "or --raised-desktop-presentation-probe.");
            }

            if (explorerRecoveryProbe != confirmExplorerRestart)
            {
                throw new ArgumentException(
                    "--raised-desktop-explorer-recovery-probe requires " +
                    "--confirm-explorer-restart, and that confirmation flag is not valid alone.");
            }

            int modes = Convert.ToInt32(shellTopology) +
                Convert.ToInt32(colorBlock) +
                Convert.ToInt32(raisedDesktopProbe) +
                Convert.ToInt32(explorerRecoveryProbe) +
                Convert.ToInt32(raisedDesktopPresentationKind is not null);
            if (modes > 1)
            {
                throw new ArgumentException("Diagnostic modes cannot be combined.");
            }

            return new DiagnosticOptions(
                shellTopology,
                colorBlock,
                raisedDesktopProbe,
                explorerRecoveryProbe,
                raisedDesktopPresentationKind,
                duration);
        }
    }
}
