using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using LiveWall.Application.Sessions;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Diagnostics;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Sessions;
using LiveWall.Host.Orchestration;
using LiveWall.Infrastructure.Ipc;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Displays;

namespace LiveWall.DesktopHostDiagnostics;

internal static class Program
{
    private const int DefaultColorBlockSeconds = 10;
    private static readonly JsonSerializerOptions SystemTransitionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
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
                options.ProductCandidateLoopEnabled || options.ProductCandidateFaultEnabled ||
                options.ProductCandidateReplacementFaultEnabled ||
                options.ProductCandidateRetirementFaultEnabled ||
                options.ProductCandidateRecoveryEnabled ||
                options.ProductCandidateTopologyEnabled ||
                options.ProductCandidateTopologyChurnEnabled ||
                options.HostTaskbarNoopEnabled ||
                options.HostExplorerEventRecoveryEnabled ||
                options.HostEnvironmentRecoveryEnabled ||
                options.HostSessionLockEnabled ||
                options.HostSleepResumeEnabled ||
                options.HostSessionReconnectEnabled ||
                options.TransitionGuardProbeEnabled)
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

            if (options.ProductCandidateReplacementFaultEnabled)
            {
                return await RunProductCandidateReplacementFaultAsync(
                        topology,
                        options.DurationSeconds,
                        options.TargetDisplay,
                        options.FaultPlan ?? throw new InvalidOperationException(
                            "Replacement fault mode requires a fault plan."),
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.ProductCandidateRetirementFaultEnabled)
            {
                return await RunProductCandidateRetirementFaultAsync(
                        topology,
                        options.DurationSeconds,
                        options.TargetDisplay,
                        options.FaultPlan ?? throw new InvalidOperationException(
                            "Retirement fault mode requires a fault plan."),
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.ProductCandidateRecoveryEnabled)
            {
                return await RunProductCandidateRecoveryAsync(
                        topology,
                        options.DurationSeconds,
                        options.TargetDisplay,
                        options.FaultPlan,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.HostTaskbarNoopEnabled)
            {
                return await RunHostTaskbarNoopAsync(
                        topology,
                        options.DurationSeconds,
                        options.TargetDisplay,
                        options.SignalCount,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.HostExplorerEventRecoveryEnabled)
            {
                return await RunHostExplorerEventRecoveryAsync(
                        topology,
                        options.DurationSeconds,
                        options.TargetDisplay,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.HostEnvironmentRecoveryEnabled)
            {
                return await RunHostEnvironmentRecoveryAsync(topology, options.DurationSeconds,
                    options.TargetDisplay, options.ResultJsonPath, cancellation.Token).ConfigureAwait(false);
            }

            if (options.HostSessionLockEnabled || options.HostSleepResumeEnabled || options.HostSessionReconnectEnabled)
            {
                return await RunHostSystemTransitionAsync(
                        topology,
                        options.DurationSeconds,
                        options.TargetDisplay,
                        options.HostSessionLockEnabled
                            ? WindowsHostSystemTransitionKind.SessionLock
                            : options.HostSessionReconnectEnabled
                                ? WindowsHostSystemTransitionKind.SessionReconnect
                                : WindowsHostSystemTransitionKind.SleepResume,
                        options.PauseBeforeTransition,
                        options.ResultJsonPath,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.ProductCandidateTopologyEnabled)
            {
                return await RunProductCandidateTopologyAsync(
                        topology,
                        options.DurationSeconds,
                        options.TopologyChanges,
                        transitionGuardProbe: false,
                        options.TargetDisplay,
                        options.LayoutMode,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.TransitionGuardProbeEnabled)
            {
                return await RunProductCandidateTopologyAsync(
                        topology,
                        options.DurationSeconds,
                        topologyChanges: 1,
                        transitionGuardProbe: true,
                        options.TargetDisplay,
                        LayoutMode.PerDisplay,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }

            if (options.ProductCandidateTopologyChurnEnabled)
            {
                return await RunProductCandidateTopologyChurnAsync(
                        topology,
                        options.DurationSeconds,
                        options.TopologyChanges,
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
                        options.LayoutSequence,
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

    private static async Task<int> RunProductCandidateRecoveryAsync(
        DisplayTopology topology,
        int durationSeconds,
        string targetDisplay,
        ProbeFaultPlan? faultPlan,
        CancellationToken cancellationToken)
    {
        ProductCandidateDisplaySelection selection =
            ParseProductCandidateDisplaySelection(targetDisplay);
        IReadOnlyList<DisplayId> selectedDisplays =
            RendererProductCandidateDiagnostics.ResolveDisplayIds(topology, selection);
        if (selectedDisplays.Count < 2)
        {
            throw new InvalidOperationException(
                "Dual-display product recovery requires at least two selected displays.");
        }

        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Product recovery snapshot: Before ===");
        PrintShellTopology(before);
        WindowsBuildIdentity build = WindowsShellTopologyDiagnostics.CaptureBuildIdentity();
        ShellTopologySnapshot? oldActive = null;
        ShellTopologySnapshot? newShell = null;
        RendererProductCandidateRecoveryResult result =
            await RendererProductCandidateDiagnostics.RunRecoveryAsync(
                    new RendererProductCandidateRecoveryRequest(
                        FindRendererChildExecutable(),
                        new WindowsShellSnapshot(
                            build.Version,
                            build.Build,
                            RaisedDesktopEnabled: true,
                            build.UpdateBuildRevision),
                        topology,
                        TimeSpan.FromSeconds(durationSeconds),
                        TimeSpan.FromSeconds(durationSeconds),
                        async token =>
                        {
                            oldActive = WindowsShellTopologyDiagnostics.CaptureSnapshot();
                            ShellWindowFingerprint oldProgman = FindProgman(oldActive);
                            Console.WriteLine(
                                $"EXPLICIT PRODUCT EXPLORER RESTART: stopping Shell PID " +
                                $"{oldProgman.ProcessId}; oldShell=0x{oldActive.ShellWindowHandle:X}.");
                            await RestartExplorerAsync(oldProgman.ProcessId, token)
                                .ConfigureAwait(false);
                            newShell = await WaitForNewShellGenerationAsync(
                                    oldActive.ShellWindowHandle,
                                    oldProgman.ProcessId,
                                    token)
                                .ConfigureAwait(false);
                        },
                        selection,
                        faultPlan is null
                            ? null
                            : new DiagnosticRendererEnvironmentTarget(
                                faultPlan.TargetIteration,
                                faultPlan.TargetSurfaceOrdinal,
                                new Dictionary<string, string>
                                {
                                    [ProbeFaultPlan.EnvironmentVariableName] =
                                        faultPlan.Serialize(),
                                }),
                        faultPlan is null
                            ? DesktopRecoveryCompletionMode.Reattached
                            : DesktopRecoveryCompletionMode.Rebuilt,
                        faultPlan is null ? null : TimeSpan.FromSeconds(3)),
                    cancellationToken)
                .ConfigureAwait(false);

        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Product recovery snapshot: After cleanup ===");
        PrintShellTopology(after);
        Dictionary<SessionId, ProductCandidateSessionResult> initial =
            result.Initial.Sessions.ToDictionary(item => item.SessionId);
        bool sameRendererProcesses = result.RecoveredSessions.All(item =>
        {
            if (!initial.TryGetValue(item.SessionId, out ProductCandidateSessionResult? previous))
            {
                return false;
            }
            return previous.RendererProcessId is not null &&
                previous.RendererProcessId == item.RendererProcessId;
        });
        bool generationChanged = oldActive is not null && newShell is not null &&
            oldActive.ShellWindowHandle != newShell.ShellWindowHandle &&
            FindProgman(oldActive).ProcessId != FindProgman(newShell).ProcessId;
        bool reattachedTimelineComplete = result.Recovery.Timeline.Count(entry =>
                    entry.Phase == DesktopRecoveryPhase.SurfaceAttached &&
                    entry.Outcome == ApplyStageOutcome.Success) == selectedDisplays.Count &&
                result.Recovery.Timeline.Count(entry =>
                    entry.Phase == DesktopRecoveryPhase.FirstFramePresented &&
                    entry.Outcome == ApplyStageOutcome.Success) == selectedDisplays.Count;
        bool rebuiltTimelineComplete = result.Recovery.Timeline.Any(entry =>
                entry.Phase == DesktopRecoveryPhase.BatchAbandoned) &&
            result.Recovery.Timeline.Any(entry =>
                entry.Phase == DesktopRecoveryPhase.RebuildScheduled) &&
            result.Recovery.Timeline.Any(entry =>
                entry.Phase == DesktopRecoveryPhase.RebuildStarted);
        bool timelineComplete = result.Recovery.Timeline.Count(entry =>
                entry.Phase == DesktopRecoveryPhase.SurfacesReplaced &&
                entry.Outcome == ApplyStageOutcome.Success) == 1 &&
            (result.Recovery.CompletionMode == DesktopRecoveryCompletionMode.Reattached
                ? reattachedTimelineComplete
                : rebuiltTimelineComplete);
        bool ownedWindowsRemain = after.ShellDescendants.Any(window =>
            window.ClassName.StartsWith(
                RaisedDesktopHostSurfaceLease.WindowClassPrefix,
                StringComparison.Ordinal) ||
            window.ClassName.StartsWith(
                RendererChildDirectCompositionSession.WindowClassPrefix,
                StringComparison.Ordinal));
        DesktopRecoveryCompletionMode expectedMode = faultPlan is null
            ? DesktopRecoveryCompletionMode.Reattached
            : DesktopRecoveryCompletionMode.Rebuilt;
        bool rendererIdentityValid = expectedMode == DesktopRecoveryCompletionMode.Reattached
            ? sameRendererProcesses
            : result.RecoveredSessions.All(item => !initial.ContainsKey(item.SessionId));
        bool valid = generationChanged &&
            result.Recovery.TerminalState == DesktopRecoveryTerminalState.Succeeded &&
            result.Recovery.CompletionMode == expectedMode &&
            rendererIdentityValid && timelineComplete &&
            result.FinalRetirement.Graceful &&
            result.FinalRetirement.OwnedResourcesCleaned &&
            !ownedWindowsRemain;
        Console.WriteLine(
            $"Product recovery result: valid={valid} | terminal={result.Recovery.TerminalState} | " +
            $"mode={result.Recovery.CompletionMode} | shellGenerationChanged={generationChanged} | " +
            $"rendererIdentityValid={rendererIdentityValid} | " +
            $"sameRendererProcesses={sameRendererProcesses} | timelineComplete={timelineComplete} | " +
            $"finalGraceful={result.FinalRetirement.Graceful} | " +
            $"ownedWindowsRemain={ownedWindowsRemain}.");
        foreach (DesktopRecoveryTimelineEntry entry in result.Recovery.Timeline)
        {
            Console.WriteLine(
                $"recovery={entry.RecoveryId} | generation={entry.HostGeneration} | " +
                $"attempt={entry.Attempt} | surface={entry.SurfaceOrdinal} | " +
                $"phase={entry.Phase} | outcome={entry.Outcome} | " +
                $"reason={entry.FailureReason} | elapsedMs={entry.Elapsed.TotalMilliseconds:0.###}");
        }
        return valid ? 0 : ExplorerRecoveryFailedExitCode;
    }

    private static async Task<int> RunHostTaskbarNoopAsync(
        DisplayTopology topology,
        int durationSeconds,
        string targetDisplay,
        int signalCount,
        CancellationToken cancellationToken)
    {
        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Host TaskbarCreated No-op snapshot: Before ===");
        PrintShellTopology(before);
        ProductCandidateDisplaySelection selection =
            ParseProductCandidateDisplaySelection(targetDisplay);
        TimeSpan observation = TimeSpan.FromSeconds(Math.Max(durationSeconds, 3));
        WindowsHostTaskbarNoopResult result =
            await WindowsHostEventChainDiagnostics.RunTaskbarNoopAsync(
                    new WindowsHostTaskbarNoopRequest(
                        FindRendererChildExecutable(),
                        topology,
                        selection,
                        signalCount,
                        observation),
                    cancellationToken)
                .ConfigureAwait(false);
        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Host TaskbarCreated No-op snapshot: After cleanup ===");
        PrintShellTopology(after);
        bool ownedWindowsRemain = HasLiveWallOwnedWindows(after);
        int addedWorkerCount = DirectShellWorkers(after)
            .Except(DirectShellWorkers(before))
            .Count();
        bool valid = result.RawTaskbarSignals == signalCount &&
            result.ValidatedShellChanges == 0 &&
            result.ForwardedRecoveryCommands == 0 &&
            result.RecoveryOperationsAdded == 0 &&
            result.InitialGeneration == result.FinalGeneration &&
            result.BatchReplaceCount == 0 &&
            result.RetirementCount == 0 &&
            result.SessionIdentityPreserved &&
            result.OwnedResourcesCleaned &&
            !ownedWindowsRemain &&
            addedWorkerCount == 0;
        Console.WriteLine(
            $"HOST_TASKBAR_NOOP valid={valid} | rawTaskbarSignals={result.RawTaskbarSignals} | " +
            $"validatedShellChanges={result.ValidatedShellChanges} | " +
            $"forwardedRecoveryCommands={result.ForwardedRecoveryCommands} | " +
            $"recoveryOperationsAdded={result.RecoveryOperationsAdded} | " +
            $"generation={result.InitialGeneration}->{result.FinalGeneration} | " +
            $"batchReplaceCount={result.BatchReplaceCount} | " +
            $"retirementCount={result.RetirementCount} | " +
            $"sessionIdentityPreserved={result.SessionIdentityPreserved} | " +
            $"ownedWindowsRemain={ownedWindowsRemain} | addedWorkerCount={addedWorkerCount}.");
        return valid ? 0 : ExplorerRecoveryFailedExitCode;
    }

    private static async Task<int> RunHostExplorerEventRecoveryAsync(
        DisplayTopology topology,
        int durationSeconds,
        string targetDisplay,
        CancellationToken cancellationToken)
    {
        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Host Explorer event-chain snapshot: Before ===");
        PrintShellTopology(before);
        ProductCandidateDisplaySelection selection =
            ParseProductCandidateDisplaySelection(targetDisplay);
        ShellTopologySnapshot? oldActive = null;
        ShellTopologySnapshot? newShell = null;
        WindowsHostExplorerEventRecoveryResult result =
            await WindowsHostEventChainDiagnostics.RunExplorerRecoveryAsync(
                    new WindowsHostExplorerEventRecoveryRequest(
                        FindRendererChildExecutable(),
                        topology,
                        selection,
                        TimeSpan.FromSeconds(durationSeconds),
                        async token =>
                        {
                            oldActive = WindowsShellTopologyDiagnostics.CaptureSnapshot();
                            ShellWindowFingerprint oldProgman = FindProgman(oldActive);
                            Console.WriteLine(
                                $"EXPLICIT HOST EVENT-CHAIN EXPLORER RESTART: stopping Shell " +
                                $"PID {oldProgman.ProcessId}; oldShell=0x" +
                                $"{oldActive.ShellWindowHandle:X}. No recovery command will be " +
                                "sent directly.");
                            await RestartExplorerAsync(oldProgman.ProcessId, token)
                                .ConfigureAwait(false);
                            newShell = await WaitForNewShellGenerationAsync(
                                    oldActive.ShellWindowHandle,
                                    oldProgman.ProcessId,
                                    token)
                                .ConfigureAwait(false);
                        }),
                    cancellationToken)
                .ConfigureAwait(false);
        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Host Explorer event-chain snapshot: After cleanup ===");
        PrintShellTopology(after);
        bool shellGenerationChanged = oldActive is not null && newShell is not null &&
            oldActive.ShellWindowHandle != newShell.ShellWindowHandle &&
            FindProgman(oldActive).ProcessId != FindProgman(newShell).ProcessId;
        bool ownedWindowsRemain = HasLiveWallOwnedWindows(after);
        bool successfulMode = result.Recovery.TerminalState ==
                DesktopRecoveryTerminalState.Succeeded &&
            result.Recovery.CompletionMode is
                DesktopRecoveryCompletionMode.Reattached or
                DesktopRecoveryCompletionMode.Rebuilt;
        bool identityValid = result.Recovery.CompletionMode ==
                DesktopRecoveryCompletionMode.Reattached
            ? result.SessionIdentityPreserved
            : !result.SessionIdentityPreserved;
        bool valid = shellGenerationChanged && successfulMode && identityValid &&
            result.ValidatedShellChanges == 1 &&
            result.ForwardedRecoveryCommands == 1 &&
            result.RecoveryOperationsAdded == 1 &&
            result.DuplicateRecoveryCount == 0 &&
            result.BatchReplaceCount == 1 &&
            result.OwnedResourcesCleaned &&
            !ownedWindowsRemain;
        Console.WriteLine(
            $"HOST_EXPLORER_EVENT_RECOVERY valid={valid} | " +
            $"rawTaskbarSignals={result.RawTaskbarSignals} | " +
            $"validatedShellChanges={result.ValidatedShellChanges} | " +
            $"forwardedRecoveryCommands={result.ForwardedRecoveryCommands} | " +
            $"recoveryOperationsAdded={result.RecoveryOperationsAdded} | " +
            $"duplicateRecoveryCount={result.DuplicateRecoveryCount} | " +
            $"terminal={result.Recovery.TerminalState} | " +
            $"mode={result.Recovery.CompletionMode} | " +
            $"batchReplaceCount={result.BatchReplaceCount} | " +
            $"sessionIdentityPreserved={result.SessionIdentityPreserved} | " +
            $"shellGenerationChanged={shellGenerationChanged} | " +
            $"ownedWindowsRemain={ownedWindowsRemain}.");
        return valid ? 0 : ExplorerRecoveryFailedExitCode;
    }

    private static async Task<int> RunHostEnvironmentRecoveryAsync(
        DisplayTopology topology, int durationSeconds, string targetDisplay,
        string? resultJsonPath, CancellationToken cancellationToken)
    {
        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        PrintShellTopology(before);
        Guid runId = Guid.NewGuid();
        ShellTopologySnapshot? oldActive = null;
        ShellTopologySnapshot? newShell = null;
        WindowsHostEnvironmentRecoveryResult? result = null;
        string classification;
        string? failurePhase = null;
        string? failureType = null;
        try
        {
            result = await WindowsHostEventChainDiagnostics.RunEnvironmentRecoveryAsync(new(
                FindRendererChildExecutable(), topology, ParseProductCandidateDisplaySelection(targetDisplay),
                TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(durationSeconds), async token =>
                {
                    oldActive = WindowsShellTopologyDiagnostics.CaptureSnapshot();
                    ShellWindowFingerprint oldProgman = FindProgman(oldActive);
                    await RestartExplorerAsync(oldProgman.ProcessId, token).ConfigureAwait(false);
                    newShell = await WaitForNewShellGenerationAsync(oldActive.ShellWindowHandle,
                        oldProgman.ProcessId, token).ConfigureAwait(false);
                },
                Armed: () => Console.WriteLine("ENVIRONMENT_RECOVERY_ARMED: Orange is active. " +
                    "You have 180 seconds to manually change ONE selected display's scale and keep the change. " +
                    "Do not unplug, rotate, lock, sleep, or make a second change. " +
                    "Explorer will restart ONCE only if topology preparation is still pending."),
                Restarting: () => Console.WriteLine("ENVIRONMENT_RECOVERY_RESTARTING: " +
                    "Topology preparation is pending; explicitly restarting Explorer now. " +
                    "A brief black desktop and returning icons/taskbar are expected. " +
                    "Wait for orange to return; do not change settings again.")), cancellationToken).ConfigureAwait(false);
            classification = result.Classification.ToString();
        }
        catch (EnvironmentRecoveryDiagnosticException exception)
        {
            failurePhase = exception.Phase;
            failureType = exception.InnerException?.GetType().Name;
            classification = cancellationToken.IsCancellationRequested ? "Cancelled" :
                exception.Phase == "AwaitingTopologyChange" && exception.InnerException is OperationCanceledException
                    ? "EnvironmentInconclusive" : "DiagnosticIncomplete";
            Console.WriteLine($"ENVIRONMENT_RECOVERY_INCOMPLETE phase={failurePhase} | type={failureType}.");
        }
        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Environment recovery: After cleanup ===");
        PrintShellTopology(after);
        bool shellGenerationChanged = oldActive is not null && newShell is not null &&
            oldActive.ShellWindowHandle != newShell.ShellWindowHandle &&
            FindProgman(oldActive).ProcessId != FindProgman(newShell).ProcessId;
        bool contextValid = before.ExecutionContext.SessionId is not null &&
            before.ExecutionContext == after.ExecutionContext &&
            string.Equals(after.ExecutionContext.WindowStationName, "WinSta0", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(after.ExecutionContext.DesktopName, "Default", StringComparison.OrdinalIgnoreCase);
        bool ownedWindowsRemain = HasLiveWallOwnedWindows(after);
        int addedWorkerCount = Math.Max(0, DirectShellWorkers(after).Count - DirectShellWorkers(before).Count);
        if (result is not null && (ownedWindowsRemain || addedWorkerCount != 0 || !contextValid ||
            result.ExplorerRestartInvoked && !shellGenerationChanged)) classification = "ProductFailure";
        Console.WriteLine($"HOST_ENVIRONMENT_RECOVERY classification={classification} | " +
            $"overlapProven={result?.OverlapProven} | shellGenerationChanged={shellGenerationChanged} | " +
            $"replaceCount={result?.BatchReplaceCount} | finalRevisionValidated={result?.FinalRevisionValidated} | " +
            $"ownedWindowsRemain={ownedWindowsRemain} | addedWorkerCount={addedWorkerCount}.");
        if (resultJsonPath is not null)
        {
            string absolutePath = Path.GetFullPath(resultJsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllTextAsync(absolutePath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, runId, recordedAt = DateTimeOffset.UtcNow, classification,
                fullBuild = WindowsShellTopologyDiagnostics.CaptureBuildIdentity().FullBuild,
                executionContext = before.ExecutionContext, executionContextAfter = after.ExecutionContext,
                shellGenerationChanged, addedWorkerCount, ownedWindowsRemain,
                shellMutationRecovery = addedWorkerCount == 0 ? "NoAdditionalWorkerObserved" : "CleanupIncomplete",
                failurePhase, failureType, result,
            }, SystemTransitionJsonOptions), CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"Structured result: {absolutePath}");
        }
        // 9 is deliberately distinct from both success and product failure: no overlap sample.
        return classification == "ValidSuccess" ? 0 :
            classification == "EnvironmentInconclusive" ? 9 : ExplorerRecoveryFailedExitCode;
    }

    private static async Task<int> RunHostSystemTransitionAsync(
        DisplayTopology topology,
        int durationSeconds,
        string targetDisplay,
        WindowsHostSystemTransitionKind transitionKind,
        bool pauseBeforeTransition,
        string? resultJsonPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunHostSystemTransitionCoreAsync(topology, durationSeconds, targetDisplay,
                transitionKind, pauseBeforeTransition, resultJsonPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (resultJsonPath is not null)
            {
                string absolutePath = Path.GetFullPath(resultJsonPath);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
                await File.WriteAllTextAsync(absolutePath, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    runId = Guid.NewGuid().ToString("N"),
                    recordedAt = DateTimeOffset.UtcNow,
                    classification = cancellationToken.IsCancellationRequested ? "Cancelled" :
                        exception is SystemTransitionDiagnosticException { SessionReconnect: not null }
                            ? "ProductFailure" : "TransitionIncomplete",
                    transitionKind,
                    pauseBeforeTransition,
                    failureType = exception.GetType().Name,
                    failurePhase = (exception as SystemTransitionDiagnosticException)?.Phase,
                    sessionReconnect = (exception as SystemTransitionDiagnosticException)?.SessionReconnect,
                    sessionNotifications = (exception as SystemTransitionDiagnosticException)?.Notifications,
                    cleanupVerification = "Unavailable",
                }, SystemTransitionJsonOptions), CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    private static async Task<int> RunHostSystemTransitionCoreAsync(
        DisplayTopology topology,
        int durationSeconds,
        string targetDisplay,
        WindowsHostSystemTransitionKind transitionKind,
        bool pauseBeforeTransition,
        string? resultJsonPath,
        CancellationToken cancellationToken)
    {
        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine($"=== Host {transitionKind} snapshot: Before ===");
        PrintShellTopology(before);
        ProductCandidateDisplaySelection selection =
            ParseProductCandidateDisplaySelection(targetDisplay);
        WindowsHostSystemTransitionResult result =
            await WindowsHostEventChainDiagnostics.RunSystemTransitionAsync(
                    new WindowsHostSystemTransitionRequest(
                        FindRendererChildExecutable(),
                        topology,
                        selection,
                        transitionKind,
                        transitionKind == WindowsHostSystemTransitionKind.SessionReconnect
                            ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(3),
                        TimeSpan.FromSeconds(durationSeconds),
                        () => Console.WriteLine(transitionKind ==
                                WindowsHostSystemTransitionKind.SessionLock
                            ? "SYSTEM_TRANSITION_ARMED: lock Windows now, then unlock and " +
                                "return within three minutes. LiveWall will not lock it for you."
                            : transitionKind == WindowsHostSystemTransitionKind.SessionReconnect
                                ? "SESSION_RECONNECT_ARMED: manually disconnect this Windows session using " +
                                    "the agreed RDP/session-switch method, then return to the SAME user and " +
                                    "session within ten minutes. Lock/unlock alone is NOT a disconnect sample. " +
                                    "LiveWall will not disconnect, switch users, or enable RDP for you."
                            : "SYSTEM_TRANSITION_ARMED: put Windows to sleep now, then wake " +
                                "and sign in within three minutes. LiveWall will not initiate sleep."),
                        PauseBeforeTransition: pauseBeforeTransition),
                    cancellationToken)
                .ConfigureAwait(false);
        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine($"=== Host {transitionKind} snapshot: After cleanup ===");
        PrintShellTopology(after);
        bool ownedWindowsRemain = HasLiveWallOwnedWindows(after);
        bool timelineValid = result.SystemStateTimeline.Any(change =>
                change.RequiresEnvironmentValidation) &&
            result.SystemStateTimeline[^1].RequiresEnvironmentValidation == false;
        int addedWorkerCount = result.Recovery.Count == 0
            ? DirectShellWorkers(after).Except(DirectShellWorkers(before)).Count()
            : Math.Max(0, DirectShellWorkers(after).Count - DirectShellWorkers(before).Count);
        bool stableEnvironment = result.Recovery.Count == 0 && result.Reconciliation is null;
        bool environmentValid = stableEnvironment
            ? result.InitialGeneration == result.FinalGeneration &&
            result.InitialReplaceCount == result.FinalReplaceCount &&
            result.InitialRetirementCount == result.FinalRetirementCount &&
            result.SessionIdentityPreserved
            : (transitionKind == WindowsHostSystemTransitionKind.SleepResume ||
                transitionKind == WindowsHostSystemTransitionKind.SessionReconnect) &&
                result.Recovery.All(item => item.TerminalState == DesktopRecoveryTerminalState.Succeeded) &&
                (result.Reconciliation is null || result.Reconciliation.TerminalState == DisplayReconciliationTerminalState.Succeeded);
        bool reconnectValid = transitionKind != WindowsHostSystemTransitionKind.SessionReconnect ||
            WindowsHostEventChainDiagnostics.HasValidatedSessionReconnect(
                result.SessionReconnect, result.SessionNotifications) &&
            before.ExecutionContext.SessionId is not null &&
            before.ExecutionContext.SessionId == after.ExecutionContext.SessionId &&
            string.Equals(after.ExecutionContext.WindowStationName, "WinSta0", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(after.ExecutionContext.DesktopName, "Default", StringComparison.OrdinalIgnoreCase) &&
            after.Availability == ShellTopologyAvailability.Available;
        bool valid = reconnectValid && result.SuspendedObserved && result.ResumedObserved &&
            environmentValid && timelineValid &&
            result.OwnedResourcesCleaned && !ownedWindowsRemain && addedWorkerCount == 0 &&
            result.PlaybackFeedbackValidated && result.UserPausePreserved &&
            result.Retirement.Count > 0 && result.Retirement.All(item =>
                item.ShutdownSent && item.ShutdownCompleted && item.RendererDisposed &&
                item.SurfacesCleaned && item.Issues.Count == 0);
        Console.WriteLine(
            $"HOST_SYSTEM_TRANSITION valid={valid} | kind={transitionKind} | " +
            $"suspendedObserved={result.SuspendedObserved} | " +
            $"resumedObserved={result.ResumedObserved} | " +
            $"generation={result.InitialGeneration}->{result.FinalGeneration} | " +
            $"replace={result.InitialReplaceCount}->{result.FinalReplaceCount} | " +
            $"retirement={result.InitialRetirementCount}->{result.FinalRetirementCount} | " +
            $"sessionIdentityPreserved={result.SessionIdentityPreserved} | " +
            $"ownedWindowsRemain={ownedWindowsRemain}.");
        Console.WriteLine($"playbackFeedbackValidated={result.PlaybackFeedbackValidated} | " +
            $"userPausePreserved={result.UserPausePreserved}.");
        if (result.SessionReconnect is not null)
            Console.WriteLine($"sessionValidation={result.SessionReconnect.ValidationRevision} | " +
                $"terminal={result.SessionReconnect.TerminalState} | path={result.SessionReconnect.Path} | " +
                $"reason={result.SessionReconnect.FailureReason}.");
        foreach (RendererPlaybackEvidence evidence in result.PolicyEvidence.Concat(result.PlaybackEvidence))
        {
            Console.WriteLine($"playbackEvidence={evidence.Kind} | session={evidence.SessionId} | " +
                $"generation={evidence.Generation} | command={evidence.Command} | " +
                $"state={evidence.State} | policyReason={evidence.PolicyReason} | " +
                $"elapsedMs={evidence.Elapsed.TotalMilliseconds:F1} | failure={evidence.FailureType}.");
        }
        if (resultJsonPath is not null)
        {
            string absolutePath = Path.GetFullPath(resultJsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllTextAsync(absolutePath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                runId = Guid.NewGuid().ToString("N"),
                recordedAt = DateTimeOffset.UtcNow,
                classification = valid ? "ValidSuccess" : "ProductFailure",
                fullBuild = WindowsShellTopologyDiagnostics.CaptureBuildIdentity().FullBuild,
                executionContext = before.ExecutionContext,
                executionContextAfter = after.ExecutionContext,
                displays = topology.Displays.Select(display => new
                {
                    displayId = display.Id.Value,
                    display.Bounds,
                    display.ScaleFactor,
                    display.IsPrimary,
                }),
                pauseBeforeTransition,
                addedWorkerCount,
                directShellWorkerCountBefore = DirectShellWorkers(before).Count,
                directShellWorkerCountAfter = DirectShellWorkers(after).Count,
                ownedWindowsRemain,
                result,
            }, SystemTransitionJsonOptions), cancellationToken).ConfigureAwait(false);
        }
        foreach (LiveWall.Application.Policies.SystemStateChange change in
                 result.SystemStateTimeline)
        {
            Console.WriteLine(
                $"systemRevision={change.Revision} | reason={change.Reason} | " +
                $"validationRequired={change.RequiresEnvironmentValidation} | " +
                $"locked={change.Snapshot.IsSessionLocked} | " +
                $"displayOff={change.Snapshot.IsDisplayOff} | " +
                $"suspended={change.Snapshot.IsSystemSuspended} | " +
                $"disconnected={change.Snapshot.IsSessionDisconnected}.");
        }
        return valid ? 0 : ExplorerRecoveryFailedExitCode;
    }

    private static async Task<int> RunProductCandidateTopologyAsync(
        DisplayTopology topology,
        int durationSeconds,
        int topologyChanges,
        bool transitionGuardProbe,
        string targetDisplay,
        LayoutMode layoutMode,
        CancellationToken cancellationToken)
    {
        if (!transitionGuardProbe && topology.Displays.Count < 2)
        {
            throw new InvalidOperationException(
                "The initial topology test requires at least two connected displays.");
        }

        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Product topology snapshot: Before ===");
        PrintShellTopology(before);
        WindowsBuildIdentity build = WindowsShellTopologyDiagnostics.CaptureBuildIdentity();
        using WindowsDisplayTopologySource topologySource = new();
        DisplayTopology observedBaseline = topologySource.Current;
        RendererProductCandidateTransitionGuardRequest? guardRequest = null;
        if (transitionGuardProbe)
        {
            ProductCandidateDisplaySelection selection =
                ParseProductCandidateDisplaySelection(targetDisplay);
            IReadOnlyList<DisplayId> targets =
                RendererProductCandidateDiagnostics.ResolveDisplayIds(observedBaseline, selection);
            if (targets.Count != 1)
            {
                throw new ArgumentException(
                    "Transition guard diagnostics require exactly one target display.");
            }
            guardRequest = new RendererProductCandidateTransitionGuardRequest(
                targets[0],
                TestColors[0],
                result =>
                {
                    string regions = string.Join(
                        ";",
                        result.Plan.GuardRegions.Select(FormatBounds));
                    Console.WriteLine(
                        $"GUARD_{result.State.ToString().ToUpperInvariant()} | " +
                        $"target={result.Plan.TargetDisplayId} | " +
                        $"current={FormatBounds(result.Plan.CurrentBounds)} | " +
                        $"candidate={FormatBounds(result.Plan.CandidateBounds)} | " +
                        $"regions={regions} | area={result.Plan.TotalPixelArea} | " +
                        $"adjacentIntersection={result.Plan.AdjacentDisplayIntersection} | " +
                        $"postArmWindowMutations={result.PostArmWindowMutationCount} | " +
                        $"shellGenerationUnchanged={result.ShellGenerationUnchanged} | " +
                        $"targetReachedCandidate={result.TargetReachedCandidateBounds} | " +
                        $"ownedWindowsRemain={result.OwnedWindowsRemain}.");
                });
        }
        Console.WriteLine($"Product topology layout: {layoutMode}.");
        RendererProductCandidateTopologyResult result =
            await RendererProductCandidateDiagnostics.RunTopologyAsync(
                    new RendererProductCandidateTopologyRequest(
                        FindRendererChildExecutable(),
                        new WindowsShellSnapshot(
                            build.Version,
                            build.Build,
                            RaisedDesktopEnabled: true,
                            build.UpdateBuildRevision),
                        observedBaseline,
                        topologyChanges,
                        TimeSpan.FromSeconds(durationSeconds),
                        async (index, current, token) =>
                        {
                            Console.WriteLine(
                                $"TOPOLOGY_CHANGE_READY index={index} | " +
                                $"currentRevision={current.Revision} | " +
                                $"displays={current.Displays.Count}. " +
                                "Perform the explicitly approved physical display change now; " +
                                "the test color remains active while this tool waits up to 10 minutes.");
                            DateTime deadline = DateTime.UtcNow.AddMinutes(10);
                            while (DateTime.UtcNow < deadline)
                            {
                                token.ThrowIfCancellationRequested();
                                DisplayTopology observed;
                                try
                                {
                                    observed = topologySource.Refresh();
                                }
                                catch (InvalidOperationException exception)
                                {
                                    Console.WriteLine(
                                        $"TOPOLOGY_SNAPSHOT_TRANSIENT index={index} | " +
                                        $"detail={exception.Message}");
                                    await Task.Delay(TimeSpan.FromMilliseconds(350), token)
                                        .ConfigureAwait(false);
                                    continue;
                                }
                                if (!SameDisplayTopology(observedBaseline, observed))
                                {
                                    await Task.Delay(TimeSpan.FromMilliseconds(500), token)
                                        .ConfigureAwait(false);
                                    DisplayTopology stable = topologySource.Refresh();
                                    if (!SameDisplayTopology(observed, stable))
                                    {
                                        continue;
                                    }
                                    Console.WriteLine(
                                        $"TOPOLOGY_CHANGE_OBSERVED index={index} | " +
                                        $"displays={observedBaseline.Displays.Count}->{stable.Displays.Count} | " +
                                        $"before={FormatDisplaySummary(observedBaseline)} | " +
                                        $"after={FormatDisplaySummary(stable)}.");
                                    observedBaseline = stable;
                                    // The source has its own local revision. Host reconciliation only
                                    // requires a strict monotonic revision within this diagnostic run.
                                    return stable with { Revision = checked(current.Revision + 1) };
                                }
                                await Task.Delay(TimeSpan.FromMilliseconds(250), token)
                                    .ConfigureAwait(false);
                            }
                            throw new TimeoutException(
                                $"No physical display topology change was observed for step {index} " +
                                "within 10 minutes.");
                        },
                        ProductCandidateDisplaySelection.All,
                        VerifyTopologyStableAsync: (expected, duration, token) =>
                            VerifyTopologyStableAsync(
                                topologySource,
                                expected,
                                duration,
                                token),
                        TransitionGuard: guardRequest,
                        LayoutMode: layoutMode),
                    cancellationToken)
                .ConfigureAwait(false);

        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Product topology snapshot: After cleanup ===");
        PrintShellTopology(after);
        foreach (RendererProductCandidateTopologyTransition transition in result.Transitions)
        {
            Console.WriteLine(
                $"topologyStep={transition.Index} | " +
                $"revision={transition.PreviousTopology.Revision}->{transition.CurrentTopology.Revision} | " +
                $"displays={transition.PreviousTopology.Displays.Count}->{transition.CurrentTopology.Displays.Count} | " +
                $"kind={transition.Reconciliation.Kind} | " +
                $"terminal={transition.Reconciliation.TerminalState} | " +
                $"activeSessions={transition.ActiveSessions.Count} | " +
                $"retiredSessions={transition.RetiredSessions.Sessions.Count} | " +
                $"retiredClean={transition.RetiredSessions.OwnedResourcesCleaned}.");
            foreach (DisplayReconciliationTimelineEntry entry in
                     transition.Reconciliation.Timeline)
            {
                Console.WriteLine(
                    $"topologyRevision={entry.TopologyRevision} | " +
                    $"generation={entry.HostGeneration?.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) ?? "none"} | " +
                    $"phase={entry.Phase} | outcome={entry.Outcome} | " +
                    $"reason={entry.FailureReason} | elapsedMs={entry.Elapsed.TotalMilliseconds:0.###}");
            }
        }
        bool primarySwitchObserved = false;
        bool primarySwitchInvariantValid = true;
        bool scaleOnlyChangeObserved = false;
        bool scaleOnlyChangeInvariantValid = true;
        IReadOnlyList<ProductCandidateSessionResult> previousSessions = result.Initial.Sessions;
        foreach (RendererProductCandidateTopologyTransition transition in result.Transitions)
        {
            if (IsPrimaryFlagOnlyTopologyChange(
                    transition.PreviousTopology,
                    transition.CurrentTopology))
            {
                primarySwitchObserved = true;
                int replacementCount = transition.Reconciliation.Timeline.Count(entry =>
                    entry.Phase == DisplayReconciliationPhase.SurfacesReplaced &&
                    entry.Outcome == ApplyStageOutcome.Success);
                primarySwitchInvariantValid &=
                    transition.Reconciliation.Kind == DisplayReconciliationKind.NoChange &&
                    transition.Reconciliation.HostGeneration is null &&
                    replacementCount == 0 &&
                    transition.RetiredSessions.Sessions.Count == 0 &&
                    previousSessions.Select(item => item.SessionId).ToHashSet().SetEquals(
                        transition.ActiveSessions.Select(item => item.SessionId));
            }
            HashSet<DisplayId>? scaleChangedDisplayIds =
                GetScaleOnlyChangedDisplayIds(
                    transition.PreviousTopology,
                    transition.CurrentTopology);
            if (scaleChangedDisplayIds is not null)
            {
                scaleOnlyChangeObserved = true;
                ProductCandidateSessionResult[] affectedBefore = previousSessions
                    .Where(session => session.DisplayIds.Any(scaleChangedDisplayIds.Contains))
                    .ToArray();
                ProductCandidateSessionResult[] preservedBefore = previousSessions
                    .Where(session => session.DisplayIds.All(id =>
                        !scaleChangedDisplayIds.Contains(id)))
                    .ToArray();
                HashSet<SessionId> activeIds = transition.ActiveSessions
                    .Select(session => session.SessionId)
                    .ToHashSet();
                int replacementCount = transition.Reconciliation.Timeline.Count(entry =>
                    entry.Phase == DisplayReconciliationPhase.SurfacesReplaced &&
                    entry.Outcome == ApplyStageOutcome.Success);
                scaleOnlyChangeInvariantValid &=
                    transition.Reconciliation.Kind == DisplayReconciliationKind.Reconfigure &&
                    replacementCount == 1 &&
                    transition.RetiredSessions.Sessions.Count == affectedBefore.Length &&
                    affectedBefore.All(session => !activeIds.Contains(session.SessionId)) &&
                    preservedBefore.All(session => activeIds.Contains(session.SessionId));
            }
            previousSessions = transition.ActiveSessions;
        }
        bool ownedWindowsRemain = after.ShellDescendants.Any(window =>
            window.ClassName.StartsWith(
                RaisedDesktopHostSurfaceLease.WindowClassPrefix,
                StringComparison.Ordinal) ||
            window.ClassName.StartsWith(
                RendererChildDirectCompositionSession.WindowClassPrefix,
                StringComparison.Ordinal) ||
            window.ClassName.StartsWith(
                DesktopTransitionGuardProbe.WindowClassPrefix,
                StringComparison.Ordinal));
        bool transitionGuardValid = !transitionGuardProbe ||
            result.TransitionGuard is
            {
                State: DesktopTransitionGuardProbeState.Retired,
                ArmedBeforeChange: true,
                PostArmWindowMutationCount: 0,
                ShellGenerationUnchanged: true,
                TargetReachedCandidateBounds: true,
                OwnedWindowsRemain: false,
                Plan.Availability: DesktopTransitionGuardAvailability.Available,
                Plan.AdjacentDisplayIntersection: 0,
            };
        bool valid = result.Transitions.Count == topologyChanges &&
            result.Transitions.All(item =>
                item.Reconciliation.TerminalState ==
                    DisplayReconciliationTerminalState.Succeeded &&
                item.RetiredSessions.OwnedResourcesCleaned) &&
            primarySwitchInvariantValid &&
            scaleOnlyChangeInvariantValid &&
            transitionGuardValid &&
            result.FinalRetirement.OwnedResourcesCleaned &&
            !ownedWindowsRemain;
        Console.WriteLine(
            $"Product topology result: valid={valid} | transitions={result.Transitions.Count} | " +
            $"primarySwitchObserved={primarySwitchObserved} | " +
            $"primarySwitchInvariantValid={primarySwitchInvariantValid} | " +
            $"scaleOnlyChangeObserved={scaleOnlyChangeObserved} | " +
            $"scaleOnlyChangeInvariantValid={scaleOnlyChangeInvariantValid} | " +
            $"transitionGuardValid={transitionGuardValid} | " +
            $"finalGraceful={result.FinalRetirement.Graceful} | " +
            $"finalClean={result.FinalRetirement.OwnedResourcesCleaned} | " +
            $"ownedWindowsRemain={ownedWindowsRemain}.");
        return valid ? 0 : RendererChildProbeFailedExitCode;
    }

    private static string FormatBounds(DisplayBounds bounds) =>
        $"{bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height}";

    private static async Task<bool> VerifyTopologyStableAsync(
        WindowsDisplayTopologySource topologySource,
        DisplayTopology expected,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        long expectedRevision = expected.Revision;
        DateTime deadline = DateTime.UtcNow.Add(duration);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DisplayTopology observed = topologySource.Refresh() with
                {
                    Revision = expectedRevision,
                };
                if (!SameDisplayTopology(expected, observed))
                {
                    Console.WriteLine(
                        $"TOPOLOGY_OBSERVATION_CHANGED expected={FormatDisplaySummary(expected)} | " +
                        $"observed={FormatDisplaySummary(observed)}.");
                    return false;
                }
            }
            catch (InvalidOperationException exception)
            {
                Console.WriteLine(
                    $"TOPOLOGY_OBSERVATION_TRANSIENT detail={exception.Message}");
            }
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }
            await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(100)
                        ? remaining
                        : TimeSpan.FromMilliseconds(100),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return true;
    }

    private static bool IsPrimaryFlagOnlyTopologyChange(
        DisplayTopology previous,
        DisplayTopology current)
    {
        if (previous.Displays.Count != current.Displays.Count)
        {
            return false;
        }

        Dictionary<DisplayId, DisplayDescriptor> previousById = previous.Displays
            .ToDictionary(display => display.Id);
        bool primaryChanged = false;
        foreach (DisplayDescriptor next in current.Displays)
        {
            if (!previousById.TryGetValue(next.Id, out DisplayDescriptor? old) ||
                old.DevicePath != next.DevicePath ||
                old.Bounds != next.Bounds ||
                old.ScaleFactor != next.ScaleFactor)
            {
                return false;
            }
            primaryChanged |= old.IsPrimary != next.IsPrimary;
        }
        return primaryChanged;
    }

    private static HashSet<DisplayId>? GetScaleOnlyChangedDisplayIds(
        DisplayTopology previous,
        DisplayTopology current)
    {
        if (previous.Displays.Count != current.Displays.Count)
        {
            return null;
        }

        Dictionary<DisplayId, DisplayDescriptor> previousById = previous.Displays
            .ToDictionary(display => display.Id);
        HashSet<DisplayId> changed = [];
        foreach (DisplayDescriptor next in current.Displays)
        {
            if (!previousById.TryGetValue(next.Id, out DisplayDescriptor? old) ||
                old.DevicePath != next.DevicePath ||
                old.Bounds != next.Bounds ||
                old.IsPrimary != next.IsPrimary)
            {
                return null;
            }
            if (old.ScaleFactor != next.ScaleFactor)
            {
                changed.Add(next.Id);
            }
        }
        return changed.Count > 0 ? changed : null;
    }

    private static async Task<int> RunProductCandidateTopologyChurnAsync(
        DisplayTopology topology,
        int durationSeconds,
        int topologyChanges,
        CancellationToken cancellationToken)
    {
        if (topology.Displays.Count < 1)
        {
            throw new InvalidOperationException(
                "Topology churn diagnostics require an initially connected display.");
        }

        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Product topology churn snapshot: Before ===");
        PrintShellTopology(before);
        WindowsBuildIdentity build = WindowsShellTopologyDiagnostics.CaptureBuildIdentity();
        using WindowsDisplayTopologySource topologySource = new();
        DisplayTopology observedBaseline = topologySource.Current;
        RendererProductCandidateTopologyChurnResult result =
            await RendererProductCandidateDiagnostics.RunTopologyChurnAsync(
                    new RendererProductCandidateTopologyChurnRequest(
                        FindRendererChildExecutable(),
                        new WindowsShellSnapshot(
                            build.Version,
                            build.Build,
                            RaisedDesktopEnabled: true,
                            build.UpdateBuildRevision),
                        observedBaseline,
                        topologyChanges,
                        TimeSpan.FromSeconds(durationSeconds),
                        ObserveTopologyChurnAsync(
                            topologySource,
                            observedBaseline,
                            observedBaseline.Revision,
                            topologyChanges,
                            cancellationToken),
                        ProductCandidateDisplaySelection.All,
                        PreparedCommitHold: TimeSpan.FromSeconds(20)),
                    cancellationToken)
                .ConfigureAwait(false);

        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Product topology churn snapshot: After cleanup ===");
        PrintShellTopology(after);
        foreach (RendererProductCandidateTopologyChurnRevision revision in result.Revisions)
        {
            Console.WriteLine(
                $"churnStep={revision.Index} | " +
                $"revision={revision.PreviousTopology.Revision}->{revision.CurrentTopology.Revision} | " +
                $"displays={revision.PreviousTopology.Displays.Count}->{revision.CurrentTopology.Displays.Count} | " +
                $"kind={revision.Reconciliation.Kind} | " +
                $"terminal={revision.Reconciliation.TerminalState} | " +
                $"reason={revision.Reconciliation.FailureReason}.");
            foreach (DisplayReconciliationTimelineEntry entry in revision.Reconciliation.Timeline)
            {
                Console.WriteLine(
                    $"topologyRevision={entry.TopologyRevision} | " +
                    $"generation={entry.HostGeneration?.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) ?? "none"} | " +
                    $"phase={entry.Phase} | outcome={entry.Outcome} | " +
                    $"reason={entry.FailureReason} | elapsedMs={entry.Elapsed.TotalMilliseconds:0.###}");
            }
        }

        bool intermediateValid = result.Revisions.Take(result.Revisions.Count - 1).All(item =>
            (item.Reconciliation.TerminalState == DisplayReconciliationTerminalState.Cancelled &&
                item.Reconciliation.FailureReason == ApplyFailureReason.Superseded) ||
            (item.Reconciliation.Kind is DisplayReconciliationKind.NoChange or
                    DisplayReconciliationKind.Suspend &&
                item.Reconciliation.TerminalState == DisplayReconciliationTerminalState.Succeeded));
        int intermediateReplacements = result.Revisions.Take(result.Revisions.Count - 1)
            .Sum(item => item.Reconciliation.Timeline.Count(entry =>
                entry.Phase == DisplayReconciliationPhase.SurfacesReplaced &&
                entry.Outcome == ApplyStageOutcome.Success));
        RendererProductCandidateTopologyChurnRevision final = result.Revisions[^1];
        int finalReplacements = final.Reconciliation.Timeline.Count(entry =>
            entry.Phase == DisplayReconciliationPhase.SurfacesReplaced &&
            entry.Outcome == ApplyStageOutcome.Success);
        bool finalCommitValid = final.Reconciliation.Kind == DisplayReconciliationKind.Reconfigure
            ? finalReplacements == 1
            : finalReplacements == 0;
        bool ownedWindowsRemain = after.ShellDescendants.Any(window =>
            window.ClassName.StartsWith(
                RaisedDesktopHostSurfaceLease.WindowClassPrefix,
                StringComparison.Ordinal) ||
            window.ClassName.StartsWith(
                RendererChildDirectCompositionSession.WindowClassPrefix,
                StringComparison.Ordinal));
        bool valid = intermediateValid &&
            intermediateReplacements == 0 &&
            final.Reconciliation.TerminalState == DisplayReconciliationTerminalState.Succeeded &&
            finalCommitValid &&
            result.FinalRetirement.Graceful &&
            result.FinalRetirement.OwnedResourcesCleaned &&
            !ownedWindowsRemain;
        Console.WriteLine(
            $"Product topology churn result: valid={valid} | " +
            $"published={result.Revisions.Count} | intermediateValid={intermediateValid} | " +
            $"intermediateReplacements={intermediateReplacements} | " +
            $"finalReplacements={finalReplacements} | finalSessions={result.FinalActiveSessions.Count} | " +
            $"finalGraceful={result.FinalRetirement.Graceful} | " +
            $"finalClean={result.FinalRetirement.OwnedResourcesCleaned} | " +
            $"ownedWindowsRemain={ownedWindowsRemain}.");
        return valid ? 0 : RendererChildProbeFailedExitCode;
    }

    private static async IAsyncEnumerable<DisplayTopology> ObserveTopologyChurnAsync(
        WindowsDisplayTopologySource topologySource,
        DisplayTopology initialBaseline,
        long initialHostRevision,
        int topologyChanges,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"TOPOLOGY_CHURN_READY changes={topologyChanges} | " +
            "perform the explicitly approved physical sequence without waiting for each " +
            "reconciliation; the tool continuously samples for up to 10 minutes.");
        DisplayTopology baseline = initialBaseline;
        long hostRevision = initialHostRevision;
        int published = 0;
        DateTime deadline = DateTime.UtcNow.AddMinutes(10);
        while (published < topologyChanges && DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisplayTopology observed;
            try
            {
                observed = topologySource.Refresh();
            }
            catch (InvalidOperationException exception)
            {
                Console.WriteLine(
                    $"TOPOLOGY_CHURN_SNAPSHOT_TRANSIENT published={published} | " +
                    $"detail={exception.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            if (SameDisplayTopology(baseline, observed))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            // Require a short stable sample, but never wait for Host reconciliation. A later
            // physical state can therefore supersede preparation for the topology just yielded.
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken)
                .ConfigureAwait(false);
            DisplayTopology stable = topologySource.Refresh();
            if (!SameDisplayTopology(observed, stable))
            {
                continue;
            }

            published++;
            hostRevision = checked(hostRevision + 1);
            DisplayTopology normalized = stable with { Revision = hostRevision };
            Console.WriteLine(
                $"TOPOLOGY_CHURN_PUBLISHED index={published} | " +
                $"displays={baseline.Displays.Count}->{normalized.Displays.Count} | " +
                $"revision={normalized.Revision} | before={FormatDisplaySummary(baseline)} | " +
                $"after={FormatDisplaySummary(normalized)}.");
            baseline = stable;
            yield return normalized;
        }

        if (published < topologyChanges)
        {
            throw new TimeoutException(
                $"Topology churn observed {published} of {topologyChanges} required published " +
                "changes within 10 minutes.");
        }
    }

    private static bool SameDisplayTopology(DisplayTopology left, DisplayTopology right) =>
        left.Displays.Count == right.Displays.Count &&
        left.Displays.SequenceEqual(right.Displays);

    private static string FormatDisplaySummary(DisplayTopology topology) =>
        string.Join(
            ";",
            topology.Displays.Select(display =>
                $"{display.Id.Value}:{display.Bounds.X},{display.Bounds.Y}," +
                $"{display.Bounds.Width}x{display.Bounds.Height}@" +
                $"{display.ScaleFactor.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

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

    private static async Task<int> RunProductCandidateReplacementFaultAsync(
        DisplayTopology topology,
        int observationSeconds,
        string targetDisplay,
        ProbeFaultPlan plan,
        CancellationToken cancellationToken)
    {
        plan.Validate();
        ProductCandidateDisplaySelection selection =
            ParseProductCandidateDisplaySelection(targetDisplay);
        IReadOnlyList<DisplayId> selectedDisplays =
            RendererProductCandidateDiagnostics.ResolveDisplayIds(topology, selection);
        if (selectedDisplays.Count < 2 || plan.TargetSurfaceOrdinal > selectedDisplays.Count)
        {
            throw new ArgumentException(
                "Replacement fault mode requires at least two selected displays and a valid Surface ordinal.");
        }

        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Replacement fault snapshot: Before ===");
        PrintShellTopology(before);
        WindowsBuildIdentity build = WindowsShellTopologyDiagnostics.CaptureBuildIdentity();
        Console.WriteLine(
            $"Sequence: Apply A succeeds -> Apply B injects {plan.Phase}/{plan.Action} " +
            $"at Surface {plan.TargetSurfaceOrdinal} -> Apply C succeeds.");
        RendererProductCandidateReplacementFaultResult result =
            await RendererProductCandidateDiagnostics.RunReplacementFaultAsync(
                    new RendererProductCandidateReplacementFaultRequest(
                        FindRendererChildExecutable(),
                        new WindowsShellSnapshot(
                            build.Version,
                            build.Build,
                            RaisedDesktopEnabled: true,
                            build.UpdateBuildRevision),
                        topology,
                        TimeSpan.FromSeconds(observationSeconds),
                        new DiagnosticRendererEnvironmentTarget(
                            plan.TargetIteration,
                            plan.TargetSurfaceOrdinal,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                [ProbeFaultPlan.EnvironmentVariableName] = plan.Serialize(),
                            }),
                        selection,
                        TimeSpan.FromSeconds(2)),
                    cancellationToken)
                .ConfigureAwait(false);

        (ApplyGenerationPhase expectedPhase, ApplyFailureReason expectedReason) =
            ExpectedFaultResult(plan);
        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Replacement fault snapshot: After cleanup ===");
        PrintShellTopology(after);
        bool ownedWindowsRemain = after.ShellDescendants.Any(window =>
            window.ClassName.StartsWith(
                RaisedDesktopHostSurfaceLease.WindowClassPrefix,
                StringComparison.Ordinal) ||
            window.ClassName.StartsWith(
                RendererChildDirectCompositionSession.WindowClassPrefix,
                StringComparison.Ordinal));
        bool valid = result.FailedAttempt.State == ApplyGenerationTerminalState.Failed &&
            result.FailedAttempt.TerminalPhase == expectedPhase &&
            result.FailedAttempt.FailureReason == expectedReason &&
            result.PreviousSessionsPreserved &&
            !result.FailedAttemptReplacedSurfaces &&
            result.FailedAttemptResourcesCleaned &&
            result.PreviousSessionRetirement.Graceful &&
            result.PreviousSessionRetirement.OwnedResourcesCleaned &&
            result.FinalRetirement.Graceful &&
            result.FinalRetirement.OwnedResourcesCleaned &&
            !ownedWindowsRemain;
        Console.WriteLine(
            $"Replacement fault result: valid={valid} | " +
            $"A={result.CommittedBeforeFailure.HostGeneration} | " +
            $"B={result.FailedAttempt.Generation}/{result.FailedAttempt.TerminalPhase}/" +
            $"{result.FailedAttempt.FailureReason} | " +
            $"oldSessionsPreserved={result.PreviousSessionsPreserved} | " +
            $"BReplace={result.FailedAttemptReplacedSurfaces} | " +
            $"BResourcesCleaned={result.FailedAttemptResourcesCleaned} | " +
            $"C={result.CommittedAfterFailure.HostGeneration} | " +
            $"ownedWindowsRemain={ownedWindowsRemain}.");
        return valid ? 0 : RendererChildProbeFailedExitCode;
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
        IReadOnlyList<LayoutMode>? layoutSequence,
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
                        ParseProductCandidateDisplaySelection(targetDisplay),
                        LayoutSequence: layoutSequence),
                    cancellationToken)
                .ConfigureAwait(false);
        foreach (RendererProductCandidateIterationResult iteration in result.Iterations)
        {
            Console.WriteLine(
                $"Loop iteration={iteration.Iteration} | generation={iteration.HostGeneration} | " +
                $"layout={iteration.LayoutMode} | " +
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
        int finalSessionCount = result.Iterations.Count == 0
            ? 0
            : result.Iterations[^1].Sessions.Count;
        int expectedRetirements = result.Iterations.Sum(item => item.Sessions.Count);
        int expectedReplacementRetirements = result.Iterations
            .Take(Math.Max(0, result.Iterations.Count - 1))
            .Sum(item => item.Sessions.Count);
        bool allRetired = finalSessionCount > 0 &&
            result.RetirementResult.Sessions.Count == expectedRetirements &&
            result.RetirementResult.Graceful &&
            result.RetirementResult.OwnedResourcesCleaned;
        bool triggersCorrect = result.Retirements.Count(item =>
                StringComparer.Ordinal.Equals(item.Trigger, "Replacement")) ==
                    expectedReplacementRetirements &&
            result.Retirements.Count(item =>
                StringComparer.Ordinal.Equals(item.Trigger, "Shutdown")) == finalSessionCount &&
            result.Retirements.TakeLast(finalSessionCount).All(item =>
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

    private static async Task<int> RunProductCandidateRetirementFaultAsync(
        DisplayTopology topology,
        int durationSeconds,
        string targetDisplay,
        ProbeFaultPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.TargetIteration != 1 || plan.Phase != ProbeFaultPhase.ShutdownCompleted ||
            plan.Action != ProbeFaultAction.SuppressEvent)
        {
            throw new ArgumentException(
                "Retirement fault mode requires iteration 1, ShutdownCompleted/SuppressEvent.");
        }
        ProductCandidateDisplaySelection selection =
            ParseProductCandidateDisplaySelection(targetDisplay);
        IReadOnlyList<DisplayId> selectedDisplays =
            RendererProductCandidateDiagnostics.ResolveDisplayIds(topology, selection);
        if (selectedDisplays.Count < 2 || plan.TargetSurfaceOrdinal > selectedDisplays.Count)
        {
            throw new ArgumentException(
                "Retirement fault mode requires at least two selected displays and a valid Surface ordinal.");
        }

        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Retirement fault snapshot: Before ===");
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
                        2,
                        selection,
                        new DiagnosticRendererEnvironmentTarget(
                            plan.TargetIteration,
                            plan.TargetSurfaceOrdinal,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                [ProbeFaultPlan.EnvironmentVariableName] = plan.Serialize(),
                            }),
                        RetirementTimeout: TimeSpan.FromSeconds(2)),
                    cancellationToken)
                .ConfigureAwait(false);

        RendererProductCandidateLoopRetirement[] replacementRetirements = result.Retirements
            .Where(item => StringComparer.Ordinal.Equals(item.Trigger, "Replacement"))
            .OrderBy(item => item.Result.SurfaceOrdinal)
            .ToArray();
        RendererProductCandidateLoopRetirement[] finalRetirements = result.Retirements
            .Where(item => StringComparer.Ordinal.Equals(item.Trigger, "Shutdown"))
            .OrderBy(item => item.Result.SurfaceOrdinal)
            .ToArray();
        SessionRetirementResult? faulted = replacementRetirements.FirstOrDefault(item =>
            item.Result.SurfaceOrdinal == plan.TargetSurfaceOrdinal)?.Result;
        bool replacementRetirementValid = replacementRetirements.Length == selectedDisplays.Count &&
            replacementRetirements.Where(item => item.Result != faulted).All(item =>
                item.Result.Graceful && item.Result.OwnedResourcesCleaned) &&
            faulted is not null && !faulted.Graceful && faulted.OwnedResourcesCleaned &&
            faulted.Issues.Any(issue => issue.Kind == SessionRetirementIssueKind.ShutdownTimeout);
        bool finalRetirementValid = finalRetirements.Length == selectedDisplays.Count &&
            finalRetirements.All(item => item.Result.Graceful && item.Result.OwnedResourcesCleaned);

        ShellTopologySnapshot after = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        Console.WriteLine("=== Retirement fault snapshot: After cleanup ===");
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
        bool valid = result.Iterations.Count == 2 &&
            result.Iterations.All(item =>
                item.GenerationResult.State == ApplyGenerationTerminalState.Succeeded) &&
            replacementRetirementValid && finalRetirementValid &&
            result.OwnedResourcesCleaned && !ownedWindowsRemain &&
            !afterWorkers.Except(beforeWorkers).Any();
        Console.WriteLine(
            $"Retirement fault result: valid={valid} | BCommitted=True | " +
            $"replacementGraceful={replacementRetirements.Sum(item => item.Result.Graceful ? 1 : 0)}/" +
            $"{replacementRetirements.Length} | targetShutdownTimeout=" +
            $"{faulted?.Issues.Any(issue => issue.Kind == SessionRetirementIssueKind.ShutdownTimeout)} | " +
            $"targetDisposed={faulted?.RendererDisposed} | targetSurfacesCleaned={faulted?.SurfacesCleaned} | " +
            $"finalGraceful={finalRetirementValid} | ownedWindowsRemain={ownedWindowsRemain}.");
        return valid ? 0 : RendererChildProbeFailedExitCode;
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

    private static bool HasLiveWallOwnedWindows(ShellTopologySnapshot snapshot) =>
        snapshot.ShellDescendants.Any(window =>
            window.ClassName.StartsWith(
                RaisedDesktopHostSurfaceLease.WindowClassPrefix,
                StringComparison.Ordinal) ||
            window.ClassName.StartsWith(
                RendererChildDirectCompositionSession.WindowClassPrefix,
                StringComparison.Ordinal));

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
        bool ProductCandidateReplacementFaultEnabled,
        bool ProductCandidateRetirementFaultEnabled,
        bool ProductCandidateRecoveryEnabled,
        bool ProductCandidateTopologyEnabled,
        bool ProductCandidateTopologyChurnEnabled,
        bool HostTaskbarNoopEnabled,
        bool HostExplorerEventRecoveryEnabled,
        bool HostEnvironmentRecoveryEnabled,
        bool HostSessionLockEnabled,
        bool HostSleepResumeEnabled,
        bool HostSessionReconnectEnabled,
        bool PauseBeforeTransition,
        bool TransitionGuardProbeEnabled,
        bool SimulateRendererCrash,
        RaisedDesktopPresentationKind? PresentationKind,
        int DurationSeconds,
        int? SoakSeconds,
        int Iterations,
        int TopologyChanges,
        int SignalCount,
        string TargetDisplay,
        LayoutMode LayoutMode,
        IReadOnlyList<LayoutMode>? LayoutSequence,
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
            bool productCandidateReplacementFault = false;
            bool productCandidateRetirementFault = false;
            bool productCandidateRecovery = false;
            bool productCandidateTopology = false;
            bool productCandidateTopologyChurn = false;
            bool hostTaskbarNoop = false;
            bool hostExplorerEventRecovery = false;
            bool hostEnvironmentRecovery = false;
            bool hostSessionLock = false;
            bool hostSleepResume = false;
            bool hostSessionReconnect = false;
            bool pauseBeforeTransition = false;
            bool transitionGuardProbe = false;
            bool confirmDisplayChange = false;
            bool confirmDisplayRotation = false;
            bool simulateRendererCrash = false;
            RaisedDesktopPresentationKind? raisedDesktopPresentationKind = null;
            bool durationSpecified = false;
            bool iterationsSpecified = false;
            bool topologyChangesSpecified = false;
            bool signalCountSpecified = false;
            int duration = DefaultColorBlockSeconds;
            int? soakSeconds = null;
            int iterations = 20;
            int topologyChanges = 2;
            int signalCount = 10;
            string targetDisplay = "primary";
            bool targetDisplaySpecified = false;
            LayoutMode layoutMode = LayoutMode.PerDisplay;
            bool layoutModeSpecified = false;
            System.Collections.ObjectModel.ReadOnlyCollection<LayoutMode>? layoutSequence = null;
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
                    case "--raised-desktop-product-candidate-replacement-fault":
                        productCandidateReplacementFault = true;
                        break;
                    case "--raised-desktop-product-candidate-retirement-fault":
                        productCandidateRetirementFault = true;
                        break;
                    case "--raised-desktop-product-candidate-recovery":
                        productCandidateRecovery = true;
                        break;
                    case "--raised-desktop-product-candidate-topology":
                        productCandidateTopology = true;
                        break;
                    case "--raised-desktop-product-candidate-topology-churn":
                        productCandidateTopologyChurn = true;
                        break;
                    case "--raised-desktop-host-taskbar-noop":
                        hostTaskbarNoop = true;
                        break;
                    case "--raised-desktop-host-explorer-event-recovery":
                        hostExplorerEventRecovery = true;
                        break;
                    case "--raised-desktop-host-environment-recovery":
                        hostEnvironmentRecovery = true;
                        break;
                    case "--raised-desktop-host-session-lock":
                        hostSessionLock = true;
                        break;
                    case "--raised-desktop-host-sleep-resume":
                        hostSleepResume = true;
                        break;
                    case "--raised-desktop-host-session-reconnect":
                        hostSessionReconnect = true;
                        break;
                    case "--pause-before-transition":
                        pauseBeforeTransition = true;
                        break;
                    case "--signal-count" when index + 1 < args.Length:
                        signalCountSpecified = true;
                        if (!int.TryParse(args[++index], out signalCount) ||
                            signalCount is < 1 or > 100)
                        {
                            throw new ArgumentException(
                                "Taskbar signal count must be between 1 and 100.");
                        }
                        break;
                    case "--raised-desktop-transition-guard-probe":
                        transitionGuardProbe = true;
                        break;
                    case "--confirm-display-change":
                        confirmDisplayChange = true;
                        break;
                    case "--confirm-display-rotation":
                        confirmDisplayRotation = true;
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
                    case "--topology-changes" when index + 1 < args.Length:
                        topologyChangesSpecified = true;
                        if (!int.TryParse(args[++index], out topologyChanges) ||
                            topologyChanges is < 1 or > 8)
                        {
                            throw new ArgumentException(
                                "Topology changes must be between 1 and 8.");
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
                    case "--layout-mode" when index + 1 < args.Length:
                        layoutModeSpecified = true;
                        layoutMode = args[++index] switch
                        {
                            "per-display" => LayoutMode.PerDisplay,
                            "span" => LayoutMode.Span,
                            string value => throw new ArgumentException(
                                $"Unknown layout mode '{value}'."),
                        };
                        break;
                    case "--layout-sequence" when index + 1 < args.Length:
                        layoutSequence = Array.AsReadOnly(args[++index]
                            .Split(',', StringSplitOptions.RemoveEmptyEntries |
                                StringSplitOptions.TrimEntries)
                            .Select(value => value switch
                            {
                                "per-display" => LayoutMode.PerDisplay,
                                "span" => LayoutMode.Span,
                                _ => throw new ArgumentException(
                                    $"Unknown layout sequence mode '{value}'."),
                            })
                            .ToArray());
                        if (layoutSequence.Count == 0)
                        {
                            throw new ArgumentException(
                                "--layout-sequence requires at least one layout mode.");
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
                !productCandidateLoop && !productCandidateFault &&
                !productCandidateReplacementFault && !productCandidateRetirementFault &&
                !productCandidateRecovery && !productCandidateTopology &&
                !productCandidateTopologyChurn && !hostTaskbarNoop &&
                !hostExplorerEventRecovery && !hostEnvironmentRecovery && !hostSessionReconnect && !hostSessionLock && !hostSleepResume &&
                !transitionGuardProbe && durationSpecified)
            {
                throw new ArgumentException(
                    "--duration-seconds requires a color-block, presentation, product " +
                    "candidate, recovery, topology, or topology-churn diagnostic mode.");
            }

            bool recoveryRequested = explorerRecoveryProbe || rendererChildRecoveryProbe ||
                productCandidateRecovery || hostExplorerEventRecovery || hostEnvironmentRecovery;
            if (recoveryRequested != confirmExplorerRestart)
            {
                throw new ArgumentException(
                    "A Raised Desktop Explorer recovery probe requires " +
                    "--confirm-explorer-restart, and that confirmation flag is not valid alone.");
            }
            if ((productCandidateTopology || productCandidateTopologyChurn || hostEnvironmentRecovery) !=
                confirmDisplayChange)
            {
                throw new ArgumentException(
                    "Product topology diagnostics require --confirm-display-change, and " +
                    "that confirmation flag is not valid alone.");
            }
            if (transitionGuardProbe != confirmDisplayRotation)
            {
                throw new ArgumentException(
                    "Transition guard diagnostics require --confirm-display-rotation, and " +
                    "that confirmation flag is not valid alone.");
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
            if (pauseBeforeTransition && !hostSessionLock && !hostSleepResume && !hostSessionReconnect)
            {
                throw new ArgumentException("--pause-before-transition requires a Host system transition diagnostic.");
            }
            if (resultJsonPath is not null && !productCandidate && !productCandidateFault &&
                !hostSessionLock && !hostSleepResume && !hostSessionReconnect && !hostEnvironmentRecovery)
            {
                throw new ArgumentException(
                    "--result-json requires --raised-desktop-product-candidate or " +
                    "--raised-desktop-product-candidate-fault or a Host system transition diagnostic.");
            }
            if (targetDisplaySpecified && !productCandidate && !productCandidateLoop &&
                !productCandidateFault && !productCandidateReplacementFault &&
                !productCandidateRetirementFault && !productCandidateRecovery &&
                !hostTaskbarNoop && !hostExplorerEventRecovery && !hostEnvironmentRecovery && !hostSessionLock &&
                !hostSleepResume && !hostSessionReconnect && !transitionGuardProbe)
            {
                throw new ArgumentException(
                    "--target-display requires a product candidate, loop/fault, or " +
                    "transition guard diagnostic.");
            }
            if (transitionGuardProbe && !targetDisplaySpecified)
            {
                throw new ArgumentException(
                    "Transition guard diagnostics require an explicit --target-display.");
            }
            if (layoutModeSpecified && !productCandidateTopology)
            {
                throw new ArgumentException(
                    "--layout-mode currently requires " +
                    "--raised-desktop-product-candidate-topology.");
            }
            if (layoutSequence is not null && !productCandidateLoop)
            {
                throw new ArgumentException(
                    "--layout-sequence requires " +
                    "--raised-desktop-product-candidate-loop.");
            }
            if (layoutSequence is not null && layoutSequence.Count != iterations)
            {
                throw new ArgumentException(
                    "--layout-sequence must contain exactly one mode per iteration.");
            }
            if (iterationsSpecified && !productCandidateLoop)
            {
                throw new ArgumentException(
                    "--iterations requires --raised-desktop-product-candidate-loop.");
            }
            if (topologyChangesSpecified && !productCandidateTopology &&
                !productCandidateTopologyChurn)
            {
                throw new ArgumentException(
                    "--topology-changes requires --raised-desktop-product-candidate-topology.");
            }
            if (signalCountSpecified && !hostTaskbarNoop)
            {
                throw new ArgumentException(
                    "--signal-count requires --raised-desktop-host-taskbar-noop.");
            }
            if (soakSeconds is not null && durationSpecified)
            {
                throw new ArgumentException(
                    "--soak-seconds and --duration-seconds cannot be combined.");
            }
            bool requiredProductFault = productCandidateFault ||
                productCandidateReplacementFault || productCandidateRetirementFault;
            bool hasFaultOptions = faultPhase is not null || faultAction is not null ||
                targetSurfaceOrdinal != 2;
            if (requiredProductFault && (faultPhase is null || faultAction is null))
            {
                throw new ArgumentException(
                    "Product candidate fault mode requires --fault-phase and --fault-action.");
            }
            if (hasFaultOptions && (faultPhase is null) != (faultAction is null))
            {
                throw new ArgumentException(
                    "--fault-phase and --fault-action must be specified together.");
            }
            if (hasFaultOptions && !requiredProductFault && !productCandidateRecovery)
            {
                throw new ArgumentException(
                    "Fault options require a product candidate fault or recovery mode.");
            }
            if ((requiredProductFault || hasFaultOptions) && !targetDisplaySpecified)
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
                Convert.ToInt32(productCandidateReplacementFault) +
                Convert.ToInt32(productCandidateRetirementFault) +
                Convert.ToInt32(productCandidateRecovery) +
                Convert.ToInt32(productCandidateTopology) +
                Convert.ToInt32(productCandidateTopologyChurn) +
                Convert.ToInt32(hostTaskbarNoop) +
                Convert.ToInt32(hostExplorerEventRecovery) +
                Convert.ToInt32(hostEnvironmentRecovery) +
                Convert.ToInt32(hostSessionLock) +
                Convert.ToInt32(hostSleepResume) +
                Convert.ToInt32(hostSessionReconnect) +
                Convert.ToInt32(transitionGuardProbe) +
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
                productCandidateReplacementFault,
                productCandidateRetirementFault,
                productCandidateRecovery,
                productCandidateTopology,
                productCandidateTopologyChurn,
                hostTaskbarNoop,
                hostExplorerEventRecovery,
                hostEnvironmentRecovery,
                hostSessionLock,
                hostSleepResume,
                hostSessionReconnect,
                pauseBeforeTransition,
                transitionGuardProbe,
                simulateRendererCrash,
                raisedDesktopPresentationKind,
                duration,
                soakSeconds,
                iterations,
                topologyChanges,
                signalCount,
                targetDisplay,
                layoutMode,
                layoutSequence,
                resultJsonPath,
                requiredProductFault || hasFaultOptions
                    ? new ProbeFaultPlan(
                        productCandidateReplacementFault ? 2 : 1,
                        targetSurfaceOrdinal,
                        faultPhase!.Value,
                        faultAction!.Value,
                        TargetOccurrence: productCandidateRecovery ? 2 : 1)
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
