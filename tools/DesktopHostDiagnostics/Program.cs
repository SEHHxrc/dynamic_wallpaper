using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using LiveWall.Application.Sessions;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Diagnostics;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Host.Orchestration;
using LiveWall.Infrastructure.Ipc;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Displays;

namespace LiveWall.DesktopHostDiagnostics;

internal static class Program
{
    private const int DefaultColorBlockSeconds = 10;
    private const int MaximumColorBlockSeconds = 30;
    private const int MaximumProductCandidateSoakSeconds = 3600;
    private const int ShellUnavailableExitCode = 3;
    private const int AttachPointUnavailableExitCode = 4;
    private const int RaisedDesktopRejectedExitCode = 5;
    private const int CleanupIncompleteExitCode = 6;
    private const int ExplorerRecoveryFailedExitCode = 7;
    private const int RendererChildProbeFailedExitCode = 8;
    private static readonly JsonSerializerOptions RendererSerializerOptions =
        new(JsonSerializerDefaults.Web);
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
        DiagnosticOptions? options = null;
        DisplayTopology? topology = null;
        Guid runId = Guid.NewGuid();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        try
        {
            options = DiagnosticOptions.Parse(args);
            using WindowsDisplayTopologySource displaySource = new();
            topology = displaySource.Current;
            PrintTopology(topology);
            if (options.RaisedDesktopProbeEnabled || options.PresentationKind is not null ||
                options.ExplorerRecoveryProbeEnabled || options.RendererChildProbeEnabled ||
                options.RendererChildRecoveryProbeEnabled || options.ProductCandidateEnabled ||
                options.ProductCandidateLoopEnabled || options.ProductCandidateFaultEnabled)
            {
                return await RunRaisedDesktopDiagnosticAsync(
                        topology,
                        options,
                        runId,
                        startedAt)
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
            await TryWriteProductCandidateFailureAsync(
                    options, topology, runId, startedAt, new OperationCanceledException())
                .ConfigureAwait(false);
            Console.WriteLine("Color block diagnostic canceled; temporary windows were removed.");
            return 130;
        }
        catch (DesktopAttachPointUnavailableException exception)
        {
            await TryWriteProductCandidateFailureAsync(
                    options, topology, runId, startedAt, exception)
                .ConfigureAwait(false);
            Console.Error.WriteLine(
                $"Not supported / No compliant desktop attach point: {exception.Message}");
            return AttachPointUnavailableExitCode;
        }
        catch (RendererProductCandidateFailureException exception)
        {
            await TryWriteProductCandidateFailureAsync(
                    options, topology, runId, startedAt, exception)
                .ConfigureAwait(false);
            PrintApplyGenerationResult(exception.GenerationResult, exception.Timeline);
            Console.Error.WriteLine(
                $"Desktop Host product candidate failed: {exception.Message}");
            return RendererChildProbeFailedExitCode;
        }
        catch (Exception exception)
        {
            await TryWriteProductCandidateFailureAsync(
                    options, topology, runId, startedAt, exception)
                .ConfigureAwait(false);
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
        DiagnosticOptions options,
        Guid runId,
        DateTimeOffset startedAt)
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
            if (options.ProductCandidateEnabled)
            {
                return await RunProductCandidateAsync(
                        topology,
                        options.SoakSeconds ?? options.DurationSeconds,
                        options.SoakSeconds is not null,
                        options.TargetDisplay,
                        options.ResultJsonPath,
                        runId,
                        startedAt,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.ProductCandidateFaultEnabled)
            {
                return await RunProductCandidateFaultAsync(
                        topology,
                        options.TargetDisplay,
                        options.FaultPlan ?? throw new InvalidOperationException(
                            "Product candidate fault mode requires a fault plan."),
                        options.ResultJsonPath,
                        runId,
                        startedAt,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.ProductCandidateLoopEnabled)
            {
                return await RunProductCandidateLoopAsync(
                        topology,
                        options.DurationSeconds,
                        options.Iterations,
                        options.TargetDisplay,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.ExplorerRecoveryProbeEnabled)
            {
                return await RunRaisedDesktopExplorerRecoveryAsync(
                        virtualBounds,
                        options.DurationSeconds,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.RendererChildRecoveryProbeEnabled)
            {
                return await RunRendererChildRecoveryAsync(
                        virtualBounds,
                        options.DurationSeconds,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.RendererChildProbeEnabled)
            {
                return await RunRendererChildProbeAsync(
                        virtualBounds,
                        options.DurationSeconds,
                        options.SimulateRendererCrash,
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

    private static async Task<int> RunRendererChildProbeAsync(
        ShellWindowBounds virtualBounds,
        int durationSeconds,
        bool simulateRendererCrash,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            "Starting isolated Renderer-child probe: only the LiveWall Host Surface HWND " +
            "will cross the process boundary.");
        Action<RaisedDesktopSnapshotStage, ShellTopologySnapshot> observer = (stage, snapshot) =>
        {
            Console.WriteLine($"=== Renderer-child Raised snapshot: {stage} ===");
            PrintShellTopology(snapshot);
        };

        await using RaisedDesktopHostSurfaceLease host =
            await RaisedDesktopDiagnostics.AcquireHostSurfaceAsync(
                    virtualBounds, observer, cancellationToken)
                .ConfigureAwait(false);
        RendererConnectionCredentials credentials = RendererConnectionCredentials.Create("diagnostics");
        using OneTimeAuthenticationToken authentication =
            new(credentials.AuthenticationToken);
        await using System.IO.Pipes.NamedPipeServerStream server =
            LocalNamedPipeFactory.CreateServer(credentials.PipeName, 1, isFirstInstance: true);
        string sessionId = $"renderer-child-{Guid.NewGuid():N}";
        using Process renderer = StartRendererChildProcess(credentials, sessionId);
        bool gracefulShutdown = false;
        bool expectedCrashObserved = false;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(durationSeconds + 30));
            await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using LengthPrefixedJsonChannel channel = new(server, leaveOpen: true);

            RendererEnvelope hello = await channel.ReadAsync<RendererEnvelope>(timeout.Token)
                .ConfigureAwait(false);
            HelloPayload helloPayload = ReadPayload<HelloPayload>(hello);
            if (hello.Type != RendererEventTypes.Hello || hello.SessionId != sessionId ||
                hello.Protocol.Major != ProtocolVersion.Version1.Major ||
                !authentication.TryConsume(helloPayload.AuthenticationToken))
            {
                throw new InvalidOperationException("Renderer Hello authentication or protocol validation failed.");
            }

            const long generation = 1;
            await InitializeRendererChildAsync(
                    channel,
                    sessionId,
                    generation,
                    timeout.Token)
                .ConfigureAwait(false);
            await channel.WriteAsync(CreateRendererEnvelope(
                    sessionId,
                    generation,
                    RendererCommandTypes.AttachSurface,
                    new AttachSurfacePayload(host.WindowHandle, host.Width, host.Height, 1.0)),
                timeout.Token).ConfigureAwait(false);

            RendererEnvelope attached = await channel.ReadAsync<RendererEnvelope>(timeout.Token)
                .ConfigureAwait(false);
            ValidateRendererEvent(attached, sessionId, generation, RendererEventTypes.SurfaceAttached);
            ulong childWindowHandle = ReadPayload<SurfaceAttachedPayload>(attached).WindowHandle;
            await channel.WriteAsync(CreateRendererEnvelope(
                    sessionId,
                    generation,
                    RendererCommandTypes.LoadWallpaper,
                    CreateSyntheticLoadPayload()),
                timeout.Token).ConfigureAwait(false);
            RendererEnvelope contentLoaded = await channel.ReadAsync<RendererEnvelope>(timeout.Token)
                .ConfigureAwait(false);
            ValidateRendererEvent(
                contentLoaded, sessionId, generation, RendererEventTypes.ContentLoaded);
            RendererEnvelope firstFrame = await channel.ReadAsync<RendererEnvelope>(timeout.Token)
                .ConfigureAwait(false);
            ValidateRendererEvent(
                firstFrame, sessionId, generation, RendererEventTypes.FirstFramePresented);
            _ = ReadPayload<FirstFramePresentedPayload>(firstFrame);

            ShellTopologySnapshot active = WindowsShellTopologyDiagnostics.CaptureSnapshot();
            PrintShellTopology(active);
            ValidateRendererChildTopology(
                active, host.WindowHandle, childWindowHandle, renderer.Id, virtualBounds);
            Console.WriteLine(
                $"Renderer-child DComp is active for {durationSeconds} seconds | " +
                $"hostPid={Environment.ProcessId} | rendererPid={renderer.Id} | " +
                $"host=0x{host.WindowHandle:X} | child=0x{childWindowHandle:X}.");
            Console.WriteLine(
                "Expected: orange desktop content, icons above it, taskbar unchanged.");
            await ObserveRendererChildIntervalAsync(
                    host,
                    childWindowHandle,
                    renderer.Id,
                    virtualBounds,
                    TimeSpan.FromSeconds(durationSeconds),
                    "single-generation",
                    cancellationToken)
                .ConfigureAwait(false);

            if (simulateRendererCrash)
            {
                renderer.Kill(entireProcessTree: true);
                await renderer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                expectedCrashObserved = renderer.ExitCode != 0;
                Console.WriteLine(
                    $"Intentional Renderer crash observed | exitCode={renderer.ExitCode}.");
            }
            else
            {
                await channel.WriteAsync(CreateRendererEnvelope(
                        sessionId,
                        generation,
                        RendererCommandTypes.Shutdown,
                        new ShutdownPayload("Diagnostic interval completed.")),
                    cancellationToken).ConfigureAwait(false);
                RendererEnvelope completed = await channel.ReadAsync<RendererEnvelope>(timeout.Token)
                    .ConfigureAwait(false);
                ValidateRendererEvent(
                    completed, sessionId, generation, RendererEventTypes.ShutdownCompleted);
                await renderer.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                gracefulShutdown = renderer.ExitCode == 0;
                if (!gracefulShutdown)
                {
                    throw new InvalidOperationException($"Renderer helper exited with code {renderer.ExitCode}.");
                }
            }
        }
        finally
        {
            if (!renderer.HasExited)
            {
                renderer.Kill(entireProcessTree: true);
                await renderer.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        await host.DisposeAsync().ConfigureAwait(false);
        ShellTopologySnapshot cleaned = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        bool ownedWindowsRemain = cleaned.ShellDescendants.Any(window =>
            window.ClassName.StartsWith(RaisedDesktopHostSurfaceLease.WindowClassPrefix, StringComparison.Ordinal) ||
            window.ClassName.StartsWith(RendererChildDirectCompositionSession.WindowClassPrefix, StringComparison.Ordinal));
        Console.WriteLine(
            $"Renderer-child result: attached=True | firstFrame=True | " +
            $"gracefulShutdown={gracefulShutdown} | expectedCrashObserved={expectedCrashObserved} | " +
            $"ownedResourcesCleanup={!ownedWindowsRemain}");
        bool lifecycleValidated = simulateRendererCrash ? expectedCrashObserved : gracefulShutdown;
        return lifecycleValidated && !ownedWindowsRemain ? 0 : RendererChildProbeFailedExitCode;
    }

    private static Process StartRendererChildProcess(
        RendererConnectionCredentials credentials,
        string sessionId)
    {
        string executable = FindRendererChildExecutable();
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(credentials.PipeName);
        startInfo.ArgumentList.Add(sessionId);
        startInfo.Environment["LIVEWALL_RENDERER_AUTH_TOKEN"] = credentials.AuthenticationToken;
        return Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start RendererChildProbe.");
    }

    private static string FindRendererChildExecutable()
    {
        DirectoryInfo targetFrameworkDirectory = new(AppContext.BaseDirectory);
        string configuration = targetFrameworkDirectory.Parent?.Name ?? "Debug";
        DirectoryInfo toolsDirectory = targetFrameworkDirectory.Parent?.Parent?.Parent?.Parent ??
            throw new InvalidOperationException("Could not locate the tools directory.");
        string executable = Path.Combine(
            toolsDirectory.FullName,
            "RendererChildProbe",
            "bin",
            configuration,
            targetFrameworkDirectory.Name,
            "RendererChildProbe.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Build RendererChildProbe before running the diagnostic.", executable);
        }
        return executable;
    }

    private static async Task<int> RunProductCandidateAsync(
        DisplayTopology topology,
        int durationSeconds,
        bool extendedSoak,
        string targetDisplay,
        string? resultJsonPath,
        Guid runId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Product candidate snapshot: Before ===");
        PrintShellTopology(before);
        WindowsBuildIdentity build = WindowsShellTopologyDiagnostics.CaptureBuildIdentity();
        WindowsShellSnapshot shell = new(
            build.Version,
            build.Build,
            RaisedDesktopEnabled: true,
            build.UpdateBuildRevision);
        Console.WriteLine(
            "Starting the formal product candidate chain: HostCommandLoop -> SessionCoordinator -> " +
            "Raised Desktop experimental override -> RendererProtocolSession -> RendererChildProbe.");
        RendererProductCandidateResult result;
        try
        {
            result = await RendererProductCandidateDiagnostics.RunAsync(
                    new RendererProductCandidateRequest(
                        FindRendererChildExecutable(),
                        shell,
                        topology,
                        TimeSpan.FromSeconds(durationSeconds),
                        extendedSoak,
                        ParseProductCandidateDisplaySelection(targetDisplay)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(resultJsonPath))
            {
                ShellTopologySnapshot? afterFailure = null;
                try
                {
                    afterFailure = WindowsShellTopologyDiagnostics.CaptureSnapshot();
                }
                catch
                {
                }
                ProductCandidateStructuredResult failed =
                    ProductCandidateStructuredResultWriter.CreateFailure(
                        runId,
                        startedAt,
                        topology,
                        before,
                        exception,
                        before,
                        afterFailure);
                await ProductCandidateStructuredResultWriter.WriteAsync(
                        resultJsonPath,
                        failed,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            throw;
        }
        Console.WriteLine(
            $"Product candidate result: generation={result.HostGeneration} | " +
            $"sessions={result.Sessions.Count} | " +
            $"surfaces={result.SurfaceCount} | firstFrame={result.FirstFrameCommitted} | " +
            $"ownedResourcesCleanup={result.OwnedResourcesCleaned}.");
        foreach (ProductCandidateSessionResult session in result.Sessions)
        {
            Console.WriteLine(
                $"Product candidate session={session.SessionId} | renderer={session.RendererId} | " +
                $"surface={session.SurfaceOrdinal} | displays=" +
                $"{string.Join(",", session.DisplayIds.Select(id => id.Value))}");
        }
        PrintApplyGenerationResult(result.GenerationResult, result.Timeline);
        PrintSessionRetirementResult(result.RetirementResult);
        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        using WindowsDisplayTopologySource afterDisplaySource = new();
        DisplayTopology afterTopology = afterDisplaySource.Current;
        Console.WriteLine("=== Product candidate snapshot: After cleanup ===");
        PrintShellTopology(after);
        HashSet<ulong> beforeWorkers = DirectShellWorkers(before);
        HashSet<ulong> afterWorkers = DirectShellWorkers(after);
        ulong[] addedWorkers = afterWorkers.Except(beforeWorkers).ToArray();
        bool ownedWindowsRemain = after.ShellDescendants.Any(window =>
            window.ClassName.StartsWith(
                RaisedDesktopHostSurfaceLease.WindowClassPrefix,
                StringComparison.Ordinal) ||
            window.ClassName.StartsWith(
                RendererChildDirectCompositionSession.WindowClassPrefix,
                StringComparison.Ordinal));
        Console.WriteLine(
            $"Product candidate cleanup verification: ownedWindowsRemain={ownedWindowsRemain} | " +
            $"directShellWorkersBefore={beforeWorkers.Count} | " +
            $"directShellWorkersAfter={afterWorkers.Count} | " +
            $"addedWorkerHandles={FormatHandles(addedWorkers)} | " +
            $"shellMutationRecovery={(addedWorkers.Length == 0 ? "NoNewWorkerObserved" : "CleanupIncomplete")}.");
        if (!string.IsNullOrWhiteSpace(resultJsonPath))
        {
            ProductCandidateStructuredResult structured =
                ProductCandidateStructuredResultWriter.CreateSuccess(
                    runId,
                    startedAt,
                    topology,
                    afterTopology,
                    before,
                    after,
                    result,
                    ownedWindowsRemain,
                    beforeWorkers.Count,
                    afterWorkers.Count,
                    addedWorkers.Length);
            await ProductCandidateStructuredResultWriter.WriteAsync(
                    resultJsonPath,
                    structured,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Console.WriteLine($"Structured result: {Path.GetFullPath(resultJsonPath)}");
        }
        if (!result.FirstFrameCommitted || !result.OwnedResourcesCleaned || ownedWindowsRemain)
        {
            return RendererChildProbeFailedExitCode;
        }

        return addedWorkers.Length == 0 ? 0 : CleanupIncompleteExitCode;
    }

    private static async Task<int> RunProductCandidateFaultAsync(
        DisplayTopology topology,
        string targetDisplay,
        ProbeFaultPlan plan,
        string? resultJsonPath,
        Guid runId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        plan.Validate();
        ProductCandidateDisplaySelection selection =
            ParseProductCandidateDisplaySelection(targetDisplay);
        IReadOnlyList<DisplayId> selectedDisplays =
            RendererProductCandidateDiagnostics.ResolveDisplayIds(topology, selection);
        if (plan.TargetSurfaceOrdinal > selectedDisplays.Count)
        {
            throw new ArgumentException(
                $"Fault target Surface ordinal {plan.TargetSurfaceOrdinal} exceeds the " +
                $"{selectedDisplays.Count} selected displays.");
        }

        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Product candidate expected-failure snapshot: Before ===");
        PrintShellTopology(before);
        WindowsBuildIdentity build = WindowsShellTopologyDiagnostics.CaptureBuildIdentity();
        Console.WriteLine(
            $"Injecting expected Renderer fault: iteration={plan.TargetIteration} | " +
            $"surface={plan.TargetSurfaceOrdinal} | phase={plan.Phase} | action={plan.Action}.");

        RendererProductCandidateFailureException failure;
        try
        {
            _ = await RendererProductCandidateDiagnostics.RunAsync(
                    new RendererProductCandidateRequest(
                        FindRendererChildExecutable(),
                        new WindowsShellSnapshot(
                            build.Version,
                            build.Build,
                            RaisedDesktopEnabled: true,
                            build.UpdateBuildRevision),
                        topology,
                        TimeSpan.FromSeconds(1),
                        DisplaySelection: selection,
                        RendererEnvironment: new DiagnosticRendererEnvironmentTarget(
                            plan.TargetIteration,
                            plan.TargetSurfaceOrdinal,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                [ProbeFaultPlan.EnvironmentVariableName] = plan.Serialize(),
                            }),
                        RendererStageTimeout: TimeSpan.FromSeconds(2)),
                    cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The injected Renderer fault unexpectedly produced a successful product candidate.");
        }
        catch (RendererProductCandidateFailureException exception)
        {
            failure = exception;
        }

        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        using WindowsDisplayTopologySource afterDisplaySource = new();
        DisplayTopology afterTopology = afterDisplaySource.Current;
        Console.WriteLine("=== Product candidate expected-failure snapshot: After cleanup ===");
        PrintShellTopology(after);

        (ApplyGenerationPhase expectedPhase, ApplyFailureReason expectedReason) =
            ExpectedFaultResult(plan);
        DisplayId[] targetDisplayIds = failure.Timeline
            .Where(entry => entry.SurfaceOrdinal == plan.TargetSurfaceOrdinal)
            .SelectMany(entry => entry.DisplayIds)
            .Distinct()
            .ToArray();
        int successfulReplacements = failure.Timeline.Count(entry =>
            entry.Phase == ApplyGenerationPhase.SurfacesReplaced &&
            entry.Outcome == ApplyStageOutcome.Success);
        int successfulSurfaceCreations = failure.Timeline.Count(entry =>
            entry.Phase == ApplyGenerationPhase.SurfaceCreated &&
            entry.Outcome == ApplyStageOutcome.Success);
        int successfulCleanup = failure.Timeline.Count(entry =>
            entry.Phase == ApplyGenerationPhase.CleanupVerified &&
            entry.Outcome == ApplyStageOutcome.Success);
        List<string> validationErrors = [];
        if (failure.GenerationResult.State != ApplyGenerationTerminalState.Failed)
        {
            validationErrors.Add($"terminal={failure.GenerationResult.State}");
        }
        if (failure.GenerationResult.TerminalPhase != expectedPhase)
        {
            validationErrors.Add(
                $"phase={failure.GenerationResult.TerminalPhase}, expected={expectedPhase}");
        }
        if (failure.GenerationResult.FailureReason != expectedReason)
        {
            validationErrors.Add(
                $"reason={failure.GenerationResult.FailureReason}, expected={expectedReason}");
        }
        if (successfulReplacements != 0)
        {
            validationErrors.Add($"successfulReplaceCount={successfulReplacements}");
        }
        if (failure.ActiveSessionIds.Count != 0)
        {
            validationErrors.Add($"activeSessionCount={failure.ActiveSessionIds.Count}");
        }
        if (failure.PreparedSurfaceCount != 0)
        {
            validationErrors.Add($"preparedSurfaceCount={failure.PreparedSurfaceCount}");
        }
        if (successfulSurfaceCreations != selectedDisplays.Count)
        {
            validationErrors.Add(
                $"successfulSurfaceCreations={successfulSurfaceCreations}, " +
                $"expected={selectedDisplays.Count}");
        }
        if (successfulCleanup != selectedDisplays.Count)
        {
            validationErrors.Add(
                $"successfulCleanupCount={successfulCleanup}, expected={selectedDisplays.Count}");
        }
        if (targetDisplayIds.Length != 1 || !selectedDisplays.Contains(targetDisplayIds[0]))
        {
            validationErrors.Add(
                $"targetDisplayMapping={string.Join(",", targetDisplayIds.Select(id => id.Value))}");
        }

        bool baseValidated = validationErrors.Count == 0;
        string? validationError = baseValidated ? null : string.Join("; ", validationErrors);
        ProductCandidateStructuredResult structured =
            ProductCandidateStructuredResultWriter.CreateExpectedFailure(
                runId,
                startedAt,
                topology,
                afterTopology,
                before,
                after,
                failure,
                plan,
                Array.AsReadOnly(targetDisplayIds),
                baseValidated,
                validationError);
        if (!string.IsNullOrWhiteSpace(resultJsonPath))
        {
            await ProductCandidateStructuredResultWriter.WriteAsync(
                    resultJsonPath,
                    structured,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Console.WriteLine($"Structured result: {Path.GetFullPath(resultJsonPath)}");
        }

        PrintApplyGenerationResult(failure.GenerationResult, failure.Timeline);
        Console.WriteLine(
            $"Expected failure verification: classification={structured.Classification} | " +
            $"targetDisplay={string.Join(",", targetDisplayIds.Select(id => id.Value))} | " +
            $"activeSessions={failure.ActiveSessionIds.Count} | " +
            $"preparedSurfaces={failure.PreparedSurfaceCount} | " +
            $"successfulReplacements={successfulReplacements} | " +
            $"successfulCleanup={successfulCleanup}.");
        if (structured.Classification != ProductCandidateClassification.ExpectedFailureValidated)
        {
            Console.Error.WriteLine(
                $"Expected failure invariants were not satisfied: " +
                $"{validationError ?? "environment changed or cleanup failed"}.");
            return RendererChildProbeFailedExitCode;
        }

        return 0;
    }

    private static (ApplyGenerationPhase Phase, ApplyFailureReason Reason) ExpectedFaultResult(
        ProbeFaultPlan plan)
    {
        bool timeout = plan.Action == ProbeFaultAction.SuppressEvent;
        return plan.Phase switch
        {
            ProbeFaultPhase.SurfaceAttached => (
                ApplyGenerationPhase.SurfaceAttached,
                timeout
                    ? ApplyFailureReason.SurfaceAttachedTimeout
                    : ApplyFailureReason.SurfaceAttachmentRejected),
            ProbeFaultPhase.ContentLoaded => (
                ApplyGenerationPhase.ContentLoaded,
                timeout
                    ? ApplyFailureReason.ContentLoadedTimeout
                    : ApplyFailureReason.ContentLoadRejected),
            ProbeFaultPhase.FirstFramePresented => (
                ApplyGenerationPhase.FirstFramePresented,
                timeout
                    ? ApplyFailureReason.FirstFrameTimeout
                    : ApplyFailureReason.FirstFrameRejected),
            _ => throw new ArgumentException(
                $"Fault phase {plan.Phase} is not valid for an initial Apply matrix."),
        };
    }

    private static ProductCandidateDisplaySelection ParseProductCandidateDisplaySelection(
        string value)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(value, "primary"))
        {
            return ProductCandidateDisplaySelection.Primary;
        }
        if (StringComparer.OrdinalIgnoreCase.Equals(value, "all"))
        {
            return ProductCandidateDisplaySelection.All;
        }
        return ProductCandidateDisplaySelection.Explicit(new DisplayId(value));
    }

    private static async Task<int> RunProductCandidateLoopAsync(
        DisplayTopology topology,
        int durationSeconds,
        int iterations,
        string targetDisplay,
        CancellationToken cancellationToken)
    {
        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Product candidate loop snapshot: Before ===");
        PrintShellTopology(before);
        WindowsBuildIdentity build = WindowsShellTopologyDiagnostics.CaptureBuildIdentity();
        RendererProductCandidateLoopResult result =
            await RendererProductCandidateDiagnostics.RunLoopAsync(
                    new RendererProductCandidateLoopRequest(
                        FindRendererChildExecutable(),
                        new WindowsShellSnapshot(
                            build.Version,
                            build.Build,
                            RaisedDesktopEnabled: true,
                            build.UpdateBuildRevision),
                        topology,
                        TimeSpan.FromSeconds(durationSeconds),
                        iterations,
                        ParseProductCandidateDisplaySelection(targetDisplay)),
                    cancellationToken)
                .ConfigureAwait(false);
        foreach (RendererProductCandidateIterationResult iteration in result.Iterations)
        {
            Console.WriteLine(
                $"Loop iteration={iteration.Iteration} | generation={iteration.HostGeneration} | " +
                $"sessions={iteration.Sessions.Count} | " +
                $"surfaces={iteration.SurfaceCount} | elapsedMs={iteration.GenerationResult.Elapsed.TotalMilliseconds:0.###}");
            foreach (ProductCandidateSessionResult session in iteration.Sessions)
            {
                Console.WriteLine(
                    $"Loop session={session.SessionId} | renderer={session.RendererId} | " +
                    $"surface={session.SurfaceOrdinal} | displays=" +
                    $"{string.Join(",", session.DisplayIds.Select(id => id.Value))}");
            }
        }
        foreach (RendererProductCandidateLoopRetirement retirement in result.Retirements)
        {
            Console.WriteLine(
                $"Loop retirement generation={retirement.Result.Generation} | " +
                $"session={retirement.Result.SessionId} | trigger={retirement.Trigger} | " +
                $"graceful={retirement.Result.Graceful} | " +
                $"ownedResourcesCleanup={retirement.Result.OwnedResourcesCleaned}");
        }
        PrintSessionRetirementResult(result.RetirementResult);

        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Product candidate loop snapshot: After cleanup ===");
        PrintShellTopology(after);
        HashSet<ulong> beforeWorkers = DirectShellWorkers(before);
        HashSet<ulong> afterWorkers = DirectShellWorkers(after);
        bool ownedWindowsRemain = after.ShellDescendants.Any(window =>
            window.ClassName.StartsWith(
                RaisedDesktopHostSurfaceLease.WindowClassPrefix,
                StringComparison.Ordinal) ||
            window.ClassName.StartsWith(
                RendererChildDirectCompositionSession.WindowClassPrefix,
                StringComparison.Ordinal));
        int addedWorkerCount = afterWorkers.Except(beforeWorkers).Count();
        bool allSucceeded = result.Iterations.Count == iterations &&
            result.Iterations.All(item =>
                item.GenerationResult.State == ApplyGenerationTerminalState.Succeeded);
        int sessionsPerIteration = result.Iterations.Count == 0
            ? 0
            : result.Iterations[0].Sessions.Count;
        int expectedRetirements = iterations * sessionsPerIteration;
        bool allRetired = sessionsPerIteration > 0 &&
            result.RetirementResult.Sessions.Count == expectedRetirements &&
            result.RetirementResult.Graceful &&
            result.RetirementResult.OwnedResourcesCleaned;
        bool triggersCorrect = result.Retirements.Count(item =>
                StringComparer.Ordinal.Equals(item.Trigger, "Replacement")) ==
                    (iterations - 1) * sessionsPerIteration &&
            result.Retirements.Count(item =>
                StringComparer.Ordinal.Equals(item.Trigger, "Shutdown")) == sessionsPerIteration &&
            result.Retirements.TakeLast(sessionsPerIteration).All(item =>
                StringComparer.Ordinal.Equals(item.Trigger, "Shutdown"));
        Console.WriteLine(
            $"Loop verification: allSucceeded={allSucceeded} | allRetired={allRetired} | " +
            $"triggersCorrect={triggersCorrect} | " +
            $"ownedWindowsRemain={ownedWindowsRemain} | addedWorkerCount={addedWorkerCount}.");
        return allSucceeded && allRetired && triggersCorrect &&
            !ownedWindowsRemain && addedWorkerCount == 0
            ? 0
            : RendererChildProbeFailedExitCode;
    }

    private static async Task TryWriteProductCandidateFailureAsync(
        DiagnosticOptions? options,
        DisplayTopology? topology,
        Guid runId,
        DateTimeOffset startedAt,
        Exception exception)
    {
        if (options is null ||
            (!options.ProductCandidateEnabled && !options.ProductCandidateFaultEnabled) ||
            string.IsNullOrWhiteSpace(options.ResultJsonPath) ||
            HasStructuredResultForRun(options.ResultJsonPath, runId))
        {
            return;
        }

        try
        {
            ShellTopologySnapshot? snapshot = null;
            try
            {
                snapshot = WindowsShellTopologyDiagnostics.CaptureSnapshot();
            }
            catch
            {
            }
            ProductCandidateStructuredResult result =
                ProductCandidateStructuredResultWriter.CreateFailure(
                    runId,
                    startedAt,
                    topology,
                    snapshot,
                    exception);
            await ProductCandidateStructuredResultWriter.WriteAsync(
                    options.ResultJsonPath,
                    result,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception writeException)
        {
            Console.Error.WriteLine(
                $"Could not write structured product candidate failure: {FormatException(writeException)}");
        }
    }

    private static bool HasStructuredResultForRun(string path, Guid runId)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(path)));
            return document.RootElement.TryGetProperty("runId", out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                Guid.TryParse(value.GetString(), out Guid existing) &&
                existing == runId;
        }
        catch
        {
            return false;
        }
    }

    private static void PrintApplyGenerationResult(
        ApplyGenerationResult result,
        IReadOnlyList<ApplyTimelineEntry> timeline)
    {
        Console.WriteLine(
            $"Apply generation terminal: state={result.State} | " +
            $"deadlineMs={result.TotalDeadline.TotalMilliseconds:0} | " +
            $"elapsedMs={result.Elapsed.TotalMilliseconds:0.###} | " +
            $"phase={result.TerminalPhase?.ToString() ?? "none"} | " +
            $"reason={result.FailureReason}.");
        Console.WriteLine("Apply generation timeline:");
        foreach (ApplyTimelineEntry entry in timeline)
        {
            string displays = entry.DisplayIds.Count == 0
                ? "none"
                : string.Join(",", entry.DisplayIds.Select(displayId => displayId.Value));
            Console.WriteLine(
                $"generation={entry.Generation} | surface={entry.SurfaceOrdinal} | " +
                $"displays={displays} | elapsedMs={entry.Elapsed.TotalMilliseconds:0.###} | " +
                $"phase={entry.Phase} | outcome={entry.Outcome} | " +
                $"reason={entry.FailureReason}" +
                (string.IsNullOrWhiteSpace(entry.Detail) ? string.Empty : $" | detail={entry.Detail}"));
        }
    }

    private static void PrintSessionRetirementResult(SessionRetirementBatchResult result)
    {
        Console.WriteLine(
            $"Session retirement: graceful={result.Graceful} | " +
            $"ownedResourcesCleanup={result.OwnedResourcesCleaned}.");
        foreach (SessionRetirementResult session in result.Sessions)
        {
            string issues = session.Issues.Count == 0
                ? "none"
                : string.Join(
                    ",",
                    session.Issues.Select(issue => $"{issue.Kind}:{issue.Detail}"));
            Console.WriteLine(
                $"retirement session={session.SessionId} | generation={session.Generation} | " +
                $"surface={session.SurfaceOrdinal} | elapsedMs={session.Elapsed.TotalMilliseconds:0.###} | " +
                $"shutdownSent={session.ShutdownSent} | " +
                $"shutdownCompleted={session.ShutdownCompleted} | " +
                $"rendererDisposed={session.RendererDisposed} | " +
                $"surfacesCleaned={session.SurfacesCleaned} | issues={issues}");
        }
    }

    private static HashSet<ulong> DirectShellWorkers(ShellTopologySnapshot snapshot) =>
        snapshot.ShellDescendants
            .Where(window =>
                window.ParentWindowHandle == snapshot.ShellWindowHandle &&
                StringComparer.Ordinal.Equals(window.ClassName, "WorkerW"))
            .Select(window => window.WindowHandle)
            .ToHashSet();

    private static string FormatHandles(IEnumerable<ulong> handles)
    {
        string[] formatted = handles.Select(handle => $"0x{handle:X}").ToArray();
        return formatted.Length == 0 ? "none" : string.Join(",", formatted);
    }

    private static async Task<int> RunRendererChildRecoveryAsync(
        ShellWindowBounds virtualBounds,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            "Starting explicit Explorer recovery with one persistent Renderer process. " +
            "Only LiveWall Host Surface HWNDs cross the process boundary.");
        RendererConnectionCredentials credentials = RendererConnectionCredentials.Create("diagnostics");
        using OneTimeAuthenticationToken authentication = new(credentials.AuthenticationToken);
        await using System.IO.Pipes.NamedPipeServerStream server =
            LocalNamedPipeFactory.CreateServer(credentials.PipeName, 1, isFirstInstance: true);
        string sessionId = $"renderer-child-recovery-{Guid.NewGuid():N}";
        using Process renderer = StartRendererChildProcess(credentials, sessionId);
        RaisedDesktopHostSurfaceLease? oldHost = null;
        RaisedDesktopHostSurfaceLease? newHost = null;
        ulong oldHostHandle = 0;
        ulong newHostHandle = 0;
        ulong oldChild = 0;
        ulong newChild = 0;
        int rendererProcessId = renderer.Id;
        bool gracefulShutdown = false;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(durationSeconds + 50));
            await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using LengthPrefixedJsonChannel channel = new(server, leaveOpen: true);
            RendererEnvelope hello = await channel.ReadAsync<RendererEnvelope>(timeout.Token)
                .ConfigureAwait(false);
            HelloPayload helloPayload = ReadPayload<HelloPayload>(hello);
            if (hello.Type != RendererEventTypes.Hello || hello.SessionId != sessionId ||
                hello.Protocol.Major != ProtocolVersion.Version1.Major ||
                !authentication.TryConsume(helloPayload.AuthenticationToken))
            {
                throw new InvalidOperationException("Renderer Hello authentication or protocol validation failed.");
            }
            await InitializeRendererChildAsync(
                    channel,
                    sessionId,
                    generation: 1,
                    timeout.Token)
                .ConfigureAwait(false);

            oldHost = await RaisedDesktopDiagnostics.AcquireHostSurfaceAsync(
                    virtualBounds,
                    (stage, snapshot) =>
                    {
                        Console.WriteLine($"=== Renderer recovery old generation: {stage} ===");
                        PrintShellTopology(snapshot);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            oldChild = await AttachRendererChildAsync(
                    channel, sessionId, generation: 1, oldHost, loadContent: true, timeout.Token)
                .ConfigureAwait(false);
            oldHostHandle = oldHost.WindowHandle;
            ShellTopologySnapshot oldActive = WindowsShellTopologyDiagnostics.CaptureSnapshot();
            ValidateRendererChildTopology(
                oldActive, oldHost.WindowHandle, oldChild, rendererProcessId, virtualBounds);
            ShellWindowFingerprint oldProgman = FindProgman(oldActive);
            Console.WriteLine(
                $"Old generation active: rendererPid={rendererProcessId} | " +
                $"host=0x{oldHost.WindowHandle:X} | child=0x{oldChild:X}. " +
                "Expected first color: orange for 2 seconds.");
            await ObserveRendererChildIntervalAsync(
                    oldHost,
                    oldChild,
                    rendererProcessId,
                    virtualBounds,
                    TimeSpan.FromSeconds(2),
                    "old-generation",
                    cancellationToken)
                .ConfigureAwait(false);

            await RestartExplorerAsync(oldProgman.ProcessId, cancellationToken)
                .ConfigureAwait(false);
            ShellTopologySnapshot recovered = await WaitForNewShellGenerationAsync(
                    oldActive.ShellWindowHandle, oldProgman.ProcessId, cancellationToken)
                .ConfigureAwait(false);
            await oldHost.DisposeAsync().ConfigureAwait(false);

            newHost = await RaisedDesktopDiagnostics.AcquireHostSurfaceAsync(
                    virtualBounds,
                    (stage, snapshot) =>
                    {
                        Console.WriteLine($"=== Renderer recovery new generation: {stage} ===");
                        PrintShellTopology(snapshot);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            newChild = await AttachRendererChildAsync(
                    channel, sessionId, generation: 2, newHost, loadContent: false, timeout.Token)
                .ConfigureAwait(false);
            newHostHandle = newHost.WindowHandle;
            if (renderer.HasExited || renderer.Id != rendererProcessId ||
                oldHostHandle == newHostHandle || oldChild == newChild)
            {
                throw new InvalidOperationException(
                    "Renderer recovery reused a stale HWND or restarted the Renderer process.");
            }

            ShellTopologySnapshot newActive = WindowsShellTopologyDiagnostics.CaptureSnapshot();
            ValidateRendererChildTopology(
                newActive, newHostHandle, newChild, rendererProcessId, virtualBounds);
            ShellWindowFingerprint newProgman = FindProgman(recovered);
            bool shellGenerationChanged =
                oldActive.ShellWindowHandle != recovered.ShellWindowHandle &&
                oldProgman.ProcessId != newProgman.ProcessId;
            Console.WriteLine(
                $"New generation active for {durationSeconds} seconds: " +
                $"sameRendererPid={!renderer.HasExited && renderer.Id == rendererProcessId} | " +
                $"shellGenerationChanged={shellGenerationChanged} | " +
                $"newHost=0x{newHostHandle:X} | newChild=0x{newChild:X}. " +
                "Expected second color: yellow; icons and taskbar remain above it.");
            await ObserveRendererChildIntervalAsync(
                    newHost,
                    newChild,
                    rendererProcessId,
                    virtualBounds,
                    TimeSpan.FromSeconds(durationSeconds),
                    "new-generation",
                    cancellationToken)
                .ConfigureAwait(false);

            await channel.WriteAsync(CreateRendererEnvelope(
                    sessionId,
                    generation: 2,
                    RendererCommandTypes.Shutdown,
                    new ShutdownPayload("Renderer-child recovery probe completed.")),
                timeout.Token).ConfigureAwait(false);
            RendererEnvelope completed = await channel.ReadAsync<RendererEnvelope>(timeout.Token)
                .ConfigureAwait(false);
            ValidateRendererEvent(
                completed, sessionId, generation: 2, RendererEventTypes.ShutdownCompleted);
            await renderer.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            gracefulShutdown = renderer.ExitCode == 0;
            if (!shellGenerationChanged || !gracefulShutdown)
            {
                return ExplorerRecoveryFailedExitCode;
            }
        }
        finally
        {
            if (!renderer.HasExited)
            {
                renderer.Kill(entireProcessTree: true);
                await renderer.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            if (newHost is not null)
            {
                await newHost.DisposeAsync().ConfigureAwait(false);
            }
            if (oldHost is not null)
            {
                await oldHost.DisposeAsync().ConfigureAwait(false);
            }
        }

        ShellTopologySnapshot cleaned = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        bool ownedWindowsRemain = cleaned.ShellDescendants.Any(window =>
            window.ClassName.StartsWith(RaisedDesktopHostSurfaceLease.WindowClassPrefix, StringComparison.Ordinal) ||
            window.ClassName.StartsWith(RendererChildDirectCompositionSession.WindowClassPrefix, StringComparison.Ordinal));
        Console.WriteLine(
            $"Renderer-child recovery result: sameRendererPid=True | generations=1,2 | " +
            $"oldHost=0x{oldHostHandle:X} | newHost=0x{newHostHandle:X} | " +
            $"oldChild=0x{oldChild:X} | newChild=0x{newChild:X} | " +
            $"gracefulShutdown={gracefulShutdown} | ownedResourcesCleanup={!ownedWindowsRemain}");
        return gracefulShutdown && !ownedWindowsRemain ? 0 : RendererChildProbeFailedExitCode;
    }

    private static async Task<ulong> AttachRendererChildAsync(
        LengthPrefixedJsonChannel channel,
        string sessionId,
        long generation,
        RaisedDesktopHostSurfaceLease host,
        bool loadContent,
        CancellationToken cancellationToken)
    {
        await channel.WriteAsync(CreateRendererEnvelope(
                sessionId,
                generation,
                RendererCommandTypes.AttachSurface,
                new AttachSurfacePayload(host.WindowHandle, host.Width, host.Height, 1.0)),
            cancellationToken).ConfigureAwait(false);
        RendererEnvelope attached = await channel.ReadAsync<RendererEnvelope>(cancellationToken)
            .ConfigureAwait(false);
        ValidateRendererEvent(attached, sessionId, generation, RendererEventTypes.SurfaceAttached);
        ulong child = ReadPayload<SurfaceAttachedPayload>(attached).WindowHandle;
        if (loadContent)
        {
            await channel.WriteAsync(CreateRendererEnvelope(
                    sessionId,
                    generation,
                    RendererCommandTypes.LoadWallpaper,
                    CreateSyntheticLoadPayload()),
                cancellationToken).ConfigureAwait(false);
            RendererEnvelope contentLoaded = await channel.ReadAsync<RendererEnvelope>(cancellationToken)
                .ConfigureAwait(false);
            ValidateRendererEvent(
                contentLoaded, sessionId, generation, RendererEventTypes.ContentLoaded);
        }
        RendererEnvelope firstFrame = await channel.ReadAsync<RendererEnvelope>(cancellationToken)
            .ConfigureAwait(false);
        ValidateRendererEvent(
            firstFrame, sessionId, generation, RendererEventTypes.FirstFramePresented);
        _ = ReadPayload<FirstFramePresentedPayload>(firstFrame);
        return child;
    }

    private static async Task InitializeRendererChildAsync(
        LengthPrefixedJsonChannel channel,
        string sessionId,
        long generation,
        CancellationToken cancellationToken)
    {
        await channel.WriteAsync(CreateRendererEnvelope(
                sessionId,
                generation,
                RendererCommandTypes.Initialize,
                new InitializePayload(
                    "renderer-child-dcomp-probe",
                    ["HwndChild", "DirectComposition"],
                    "en-US")),
            cancellationToken).ConfigureAwait(false);
        RendererEnvelope initialized = await channel.ReadAsync<RendererEnvelope>(cancellationToken)
            .ConfigureAwait(false);
        ValidateRendererEvent(initialized, sessionId, generation, RendererEventTypes.Initialized);
        _ = ReadPayload<InitializedPayload>(initialized);
    }

    private static LoadWallpaperPayload CreateSyntheticLoadPayload() =>
        new(
            "diagnostic.synthetic.color",
            "Scene",
            AppContext.BaseDirectory,
            "synthetic-color",
            new Dictionary<string, JsonElement>());

    private static async Task ObserveRendererChildIntervalAsync(
        RaisedDesktopHostSurfaceLease host,
        ulong childWindowHandle,
        int rendererProcessId,
        ShellWindowBounds virtualBounds,
        TimeSpan duration,
        string label,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int lastReportedSecond = -1;
        while (stopwatch.Elapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = duration - stopwatch.Elapsed;
            await Task.Delay(
                    remaining < TimeSpan.FromSeconds(1)
                        ? remaining
                        : TimeSpan.FromSeconds(1),
                    cancellationToken)
                .ConfigureAwait(false);
            ShellTopologySnapshot snapshot = WindowsShellTopologyDiagnostics.CaptureSnapshot();
            ValidateRendererChildTopology(
                snapshot, host.WindowHandle, childWindowHandle, rendererProcessId, virtualBounds);
            int elapsedSecond = Math.Min(
                (int)Math.Ceiling(stopwatch.Elapsed.TotalSeconds),
                (int)Math.Ceiling(duration.TotalSeconds));
            if (elapsedSecond != lastReportedSecond)
            {
                lastReportedSecond = elapsedSecond;
                Console.WriteLine(
                    $"Presentation stable: {label} | elapsed={stopwatch.Elapsed.TotalSeconds:0.0}s/" +
                    $"{duration.TotalSeconds:0.0}s.");
            }
        }
    }

    private static RendererEnvelope CreateRendererEnvelope<T>(
        string sessionId,
        long generation,
        string type,
        T payload) =>
        new(
            ProtocolVersion.Version1,
            Guid.NewGuid().ToString("N"),
            sessionId,
            generation,
            type,
            JsonSerializer.SerializeToElement(payload, RendererSerializerOptions));

    private static T ReadPayload<T>(RendererEnvelope envelope) =>
        envelope.Payload.Deserialize<T>(RendererSerializerOptions) ??
        throw new InvalidOperationException($"Renderer event {envelope.Type} had no payload.");

    private static void ValidateRendererEvent(
        RendererEnvelope envelope,
        string sessionId,
        long generation,
        string expectedType)
    {
        if (envelope.Protocol.Major != ProtocolVersion.Version1.Major ||
            envelope.SessionId != sessionId || envelope.Generation != generation ||
            envelope.Type != expectedType)
        {
            throw new InvalidOperationException(
                $"Expected {expectedType} for generation {generation}, received " +
                $"{envelope.Type} for generation {envelope.Generation}.");
        }
    }

    private static void ValidateRendererChildTopology(
        ShellTopologySnapshot snapshot,
        ulong hostWindowHandle,
        ulong childWindowHandle,
        int rendererProcessId,
        ShellWindowBounds expectedBounds)
    {
        ShellWindowFingerprint host = snapshot.ShellDescendants.Single(window =>
            window.WindowHandle == hostWindowHandle &&
            window.ClassName.StartsWith(RaisedDesktopHostSurfaceLease.WindowClassPrefix, StringComparison.Ordinal));
        ShellWindowFingerprint child = snapshot.ShellDescendants.Single(window =>
            window.WindowHandle == childWindowHandle &&
            window.ClassName.StartsWith(RendererChildDirectCompositionSession.WindowClassPrefix, StringComparison.Ordinal));
        ShellWindowFingerprint defView = snapshot.ShellDescendants.Single(window =>
            window.ClassName == "SHELLDLL_DefView" &&
            window.ParentWindowHandle == snapshot.ShellWindowHandle);
        ShellWindowFingerprint worker = snapshot.ShellDescendants.Single(window =>
            window.ClassName == "WorkerW" &&
            window.ParentWindowHandle == snapshot.ShellWindowHandle);

        if (host.ParentWindowHandle != snapshot.ShellWindowHandle ||
            host.ProcessId != Environment.ProcessId || host.Bounds != expectedBounds ||
            host.SiblingZOrderIndex != defView.SiblingZOrderIndex + 1 ||
            host.SiblingZOrderIndex >= worker.SiblingZOrderIndex ||
            child.ParentWindowHandle != hostWindowHandle ||
            child.ProcessId != checked((uint)rendererProcessId) ||
            child.ProcessId == host.ProcessId || child.Bounds != host.Bounds)
        {
            string interveningWindows = string.Join(
                ", ",
                snapshot.ShellDescendants
                    .Where(window =>
                        window.ParentWindowHandle == snapshot.ShellWindowHandle &&
                        window.SiblingZOrderIndex > defView.SiblingZOrderIndex &&
                        window.SiblingZOrderIndex < host.SiblingZOrderIndex)
                    .Select(window =>
                        $"{window.ClassName}/{window.ProcessName ?? "unknown"}(z{window.SiblingZOrderIndex})"));
            throw new InvalidOperationException(
                "Renderer-child topology did not satisfy DefView > Host Surface > WorkerW " +
                "and Host Surface > Renderer child ownership/bounds invariants. " +
                $"defViewZ={defView.SiblingZOrderIndex}, hostZ={host.SiblingZOrderIndex}, " +
                $"workerZ={worker.SiblingZOrderIndex}, intervening=[{interveningWindows}].");
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
        bool RendererChildProbeEnabled,
        bool RendererChildRecoveryProbeEnabled,
        bool ProductCandidateEnabled,
        bool ProductCandidateLoopEnabled,
        bool ProductCandidateFaultEnabled,
        bool SimulateRendererCrash,
        RaisedDesktopPresentationKind? PresentationKind,
        int DurationSeconds,
        int? SoakSeconds,
        int Iterations,
        string TargetDisplay,
        string? ResultJsonPath,
        ProbeFaultPlan? FaultPlan)
    {
        public static DiagnosticOptions Parse(string[] args)
        {
            bool colorBlock = false;
            bool shellTopology = false;
            bool raisedDesktopProbe = false;
            bool explorerRecoveryProbe = false;
            bool confirmExplorerRestart = false;
            bool rendererChildProbe = false;
            bool rendererChildRecoveryProbe = false;
            bool productCandidate = false;
            bool productCandidateLoop = false;
            bool productCandidateFault = false;
            bool simulateRendererCrash = false;
            RaisedDesktopPresentationKind? raisedDesktopPresentationKind = null;
            bool durationSpecified = false;
            bool iterationsSpecified = false;
            int duration = DefaultColorBlockSeconds;
            int? soakSeconds = null;
            int iterations = 20;
            string targetDisplay = "primary";
            bool targetDisplaySpecified = false;
            string? resultJsonPath = null;
            ProbeFaultPhase? faultPhase = null;
            ProbeFaultAction? faultAction = null;
            int targetSurfaceOrdinal = 2;
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
                    case "--raised-desktop-renderer-child-probe":
                        rendererChildProbe = true;
                        break;
                    case "--simulate-renderer-crash":
                        simulateRendererCrash = true;
                        break;
                    case "--raised-desktop-renderer-child-recovery-probe":
                        rendererChildRecoveryProbe = true;
                        break;
                    case "--raised-desktop-product-candidate":
                        productCandidate = true;
                        break;
                    case "--raised-desktop-product-candidate-loop":
                        productCandidateLoop = true;
                        break;
                    case "--raised-desktop-product-candidate-fault":
                        productCandidateFault = true;
                        break;
                    case "--fault-phase" when index + 1 < args.Length:
                        faultPhase = ParseFaultPhase(args[++index]);
                        break;
                    case "--fault-action" when index + 1 < args.Length:
                        faultAction = ParseFaultAction(args[++index]);
                        break;
                    case "--target-surface-ordinal" when index + 1 < args.Length:
                        if (!int.TryParse(args[++index], out targetSurfaceOrdinal) ||
                            targetSurfaceOrdinal < 1)
                        {
                            throw new ArgumentException(
                                "Target Surface ordinal must be a positive integer.");
                        }
                        break;
                    case "--soak-seconds" when index + 1 < args.Length:
                        if (!int.TryParse(args[++index], out int parsedSoak) ||
                            parsedSoak is < 31 or > MaximumProductCandidateSoakSeconds)
                        {
                            throw new ArgumentException(
                                $"Product candidate soak must be between 31 and {MaximumProductCandidateSoakSeconds} seconds.");
                        }
                        soakSeconds = parsedSoak;
                        break;
                    case "--iterations" when index + 1 < args.Length:
                        iterationsSpecified = true;
                        if (!int.TryParse(args[++index], out iterations) || iterations is < 2 or > 100)
                        {
                            throw new ArgumentException("Loop iterations must be between 2 and 100.");
                        }
                        break;
                    case "--result-json" when index + 1 < args.Length:
                        resultJsonPath = args[++index];
                        break;
                    case "--target-display" when index + 1 < args.Length:
                        targetDisplaySpecified = true;
                        targetDisplay = args[++index];
                        if (string.IsNullOrWhiteSpace(targetDisplay))
                        {
                            throw new ArgumentException("--target-display requires a value.");
                        }
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
                !explorerRecoveryProbe && !rendererChildProbe &&
                !rendererChildRecoveryProbe && !productCandidate &&
                !productCandidateLoop && !productCandidateFault && durationSpecified)
            {
                throw new ArgumentException(
                    "--duration-seconds requires --color-block, --raised-desktop-color-block, " +
                    "or --raised-desktop-presentation-probe.");
            }

            bool recoveryRequested = explorerRecoveryProbe || rendererChildRecoveryProbe;
            if (recoveryRequested != confirmExplorerRestart)
            {
                throw new ArgumentException(
                    "A Raised Desktop Explorer recovery probe requires " +
                    "--confirm-explorer-restart, and that confirmation flag is not valid alone.");
            }

            if (simulateRendererCrash && !rendererChildProbe)
            {
                throw new ArgumentException(
                    "--simulate-renderer-crash requires --raised-desktop-renderer-child-probe.");
            }

            if (soakSeconds is not null && !productCandidate)
            {
                throw new ArgumentException(
                    "--soak-seconds requires --raised-desktop-product-candidate.");
            }
            if (resultJsonPath is not null && !productCandidate && !productCandidateFault)
            {
                throw new ArgumentException(
                    "--result-json requires --raised-desktop-product-candidate or " +
                    "--raised-desktop-product-candidate-fault.");
            }
            if (targetDisplaySpecified && !productCandidate && !productCandidateLoop &&
                !productCandidateFault)
            {
                throw new ArgumentException(
                    "--target-display requires --raised-desktop-product-candidate or " +
                    "--raised-desktop-product-candidate-loop/fault.");
            }
            if (iterationsSpecified && !productCandidateLoop)
            {
                throw new ArgumentException(
                    "--iterations requires --raised-desktop-product-candidate-loop.");
            }
            if (soakSeconds is not null && durationSpecified)
            {
                throw new ArgumentException(
                    "--soak-seconds and --duration-seconds cannot be combined.");
            }
            if (productCandidateFault && (faultPhase is null || faultAction is null))
            {
                throw new ArgumentException(
                    "Product candidate fault mode requires --fault-phase and --fault-action.");
            }
            if (!productCandidateFault &&
                (faultPhase is not null || faultAction is not null || targetSurfaceOrdinal != 2))
            {
                throw new ArgumentException(
                    "Fault options require --raised-desktop-product-candidate-fault.");
            }
            if (productCandidateFault && !targetDisplaySpecified)
            {
                targetDisplay = "all";
            }

            int modes = Convert.ToInt32(shellTopology) +
                Convert.ToInt32(colorBlock) +
                Convert.ToInt32(raisedDesktopProbe) +
                Convert.ToInt32(explorerRecoveryProbe) +
                Convert.ToInt32(rendererChildProbe) +
                Convert.ToInt32(rendererChildRecoveryProbe) +
                Convert.ToInt32(productCandidate) +
                Convert.ToInt32(productCandidateLoop) +
                Convert.ToInt32(productCandidateFault) +
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
                rendererChildProbe,
                rendererChildRecoveryProbe,
                productCandidate,
                productCandidateLoop,
                productCandidateFault,
                simulateRendererCrash,
                raisedDesktopPresentationKind,
                duration,
                soakSeconds,
                iterations,
                targetDisplay,
                resultJsonPath,
                productCandidateFault
                    ? new ProbeFaultPlan(
                        1,
                        targetSurfaceOrdinal,
                        faultPhase!.Value,
                        faultAction!.Value)
                    : null);
        }

        private static ProbeFaultPhase ParseFaultPhase(string value) => value switch
        {
            "surface-attached" => ProbeFaultPhase.SurfaceAttached,
            "content-loaded" => ProbeFaultPhase.ContentLoaded,
            "first-frame-presented" => ProbeFaultPhase.FirstFramePresented,
            "shutdown-completed" => ProbeFaultPhase.ShutdownCompleted,
            _ => throw new ArgumentException($"Unknown Renderer probe fault phase '{value}'."),
        };

        private static ProbeFaultAction ParseFaultAction(string value) => value switch
        {
            "fatal" => ProbeFaultAction.FatalEvent,
            "exit" => ProbeFaultAction.ExitProcess,
            "suppress" => ProbeFaultAction.SuppressEvent,
            _ => throw new ArgumentException($"Unknown Renderer probe fault action '{value}'."),
        };
    }
}
