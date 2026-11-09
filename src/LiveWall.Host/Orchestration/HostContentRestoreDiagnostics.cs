using System.Diagnostics;
using LiveWall.Application.Importing;
using LiveWall.Application.Layouts;
using LiveWall.Application.Library;
using LiveWall.Application.Policies;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.Bootstrap;
using LiveWall.Host.CommandLoop;
using LiveWall.Infrastructure.Configuration;
using LiveWall.Infrastructure.Persistence;

namespace LiveWall.Host.Orchestration;

public enum HostContentRestorePhase
{
    /// <summary>First Host on a fresh data root: the video is applied as the UI would, then the Host stops.</summary>
    Applied,

    /// <summary>Second Host on the same data root: the persisted assignment is restored at startup only.</summary>
    Restored,
}

public sealed record HostContentRestoreRequest(
    string RunDirectory,
    string SourceVideoPath,
    TimeSpan ObservationDuration,
    TimeSpan PauseBetweenHosts,
    Action<HostContentRestorePhase, IReadOnlyList<ProductCandidateSessionResult>>? PhaseCommitted = null,
    Action<HostContentRestorePhase>? HostStopped = null);

public sealed record HostContentRestorePhaseResult(
    HostContentRestorePhase Phase,
    ApplyGenerationResult? GenerationResult,
    IReadOnlyList<ApplyTimelineEntry> Timeline,
    IReadOnlyList<ProductCandidateSessionResult> Sessions,
    SessionRetirementBatchResult Retirement,
    IReadOnlyList<int?> RendererExitCodes,
    IReadOnlyList<WallpaperAssignment> PersistedAssignments,
    string? Failure)
{
    public bool Succeeded => Failure is null;
}

public sealed record HostContentRestoreResult(
    WallpaperImportResult Import,
    string? ContentRoot,
    IReadOnlyList<HostContentRestorePhaseResult> Phases,
    string? Failure)
{
    public bool Valid => Failure is null && Phases.Count == 2 && Phases.All(phase => phase.Succeeded);
}

internal sealed record HostRuntimeHandle(WindowsHostRuntime Runtime, HostCommandLoop CommandLoop);

/// <summary>Composes one Host runtime over a data root; the runtime owns and disposes the repository.</summary>
internal delegate HostRuntimeHandle HostRuntimeFactory(string configurationDirectory, IWallpaperRepository repository);

/// <summary>
/// C2 content restore across a Host restart: import a local video through the Host's import pipeline into a
/// fresh data root, apply it with one Host and stop it, then start a second Host on the same data and let
/// startup restore show it again. Both Hosts are the real <see cref="WindowsHostRuntime"/> with the persistent
/// library, the JSON configuration store and the default Video provider; only the desktop composition differs.
/// </summary>
public static class HostContentRestoreDiagnostics
{
    internal const string InstallationScope = "diagnostics-c2";
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RendererExitBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Real desktop composition. The only difference from the production Host is the explicit, unvalidated
    /// Raised attachment override, which the production Host Program never accepts.
    /// </summary>
    public static Task<HostContentRestoreResult> RunWithRaisedDiagnosticsAsync(
        HostContentRestoreRequest request,
        string rendererDirectory,
        CancellationToken cancellationToken)
    {
        // Fail before the data root is created when the Renderer is not side by side.
        _ = RequireVideoProviders(rendererDirectory);
        return RunAsync(
            request,
            (configuration, repository) =>
            {
                WindowsHostComposition composition = WindowsHostCompositionRoot.CreateComposition(
                    configuration,
                    new WindowsHostCompositionOptions(
                        repository,
                        RequireVideoProviders(rendererDirectory),
                        DesktopAttachmentPolicy.AllowUnvalidatedRaisedDiagnostics));
                return new HostRuntimeHandle(composition.Runtime, composition.CommandLoop);
            },
            cancellationToken);
    }

    /// <summary>The production provider set, failing when the Video Renderer is not side by side.</summary>
    internal static IReadOnlyList<IRendererProvider> RequireVideoProviders(string rendererDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rendererDirectory);
        IReadOnlyList<IRendererProvider> providers = DefaultRendererProviders.Create(InstallationScope, rendererDirectory);
        return providers.Count == 1
            ? providers
            : throw new FileNotFoundException(
                $"{DefaultRendererProviders.VideoExecutableName} is not in the renderer directory.");
    }

    /// <summary>
    /// Hidden or automated composition: a fixed topology and no Windows event sources, around the same
    /// runtime, bootstrapper and JSON configuration store the production Host uses.
    /// </summary>
    internal static HostRuntimeHandle CreateIsolatedRuntime(
        string configurationDirectory,
        IWallpaperRepository repository,
        IReadOnlyList<IRendererProvider> providers,
        IDesktopHost desktopHost,
        DisplayTopology topology)
    {
        HostCommandLoop commandLoop = new(
            new SessionCoordinator(
                repository,
                new DefaultLayoutPlanner(),
                new RendererProviderSelector(providers),
                desktopHost,
                new RuntimeEnvironment("win-x64", HardwareAccelerationAvailable: true, new HashSet<string>())),
            new DefaultPlaybackPolicyEvaluator());
        WindowsHostRuntime runtime = new(
            commandLoop,
            new HostBootstrapper(new JsonHostConfigurationStore(configurationDirectory)),
            new FixedDisplayTopologySource(topology),
            new InertEnvironmentEventBridge(),
            () =>
            {
                (repository as IDisposable)?.Dispose();
                return ValueTask.CompletedTask;
            });
        return new HostRuntimeHandle(runtime, commandLoop);
    }

    internal static async Task<HostContentRestoreResult> RunAsync(
        HostContentRestoreRequest request,
        HostRuntimeFactory createRuntime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(createRuntime);
        if (request.ObservationDuration <= TimeSpan.Zero || request.ObservationDuration > MaximumDuration ||
            request.PauseBetweenHosts < TimeSpan.Zero || request.PauseBetweenHosts > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Observation and pause durations must not exceed 30 seconds.");
        }

        (string configuration, WallpaperImportResult imported, string? contentRoot) =
            await ImportIntoFreshDataRootAsync(request.RunDirectory, request.SourceVideoPath, cancellationToken)
                .ConfigureAwait(false);
        if (imported.WallpaperId is not { } wallpaperId || contentRoot is null)
        {
            return new HostContentRestoreResult(imported, contentRoot, [], ImportFailure(imported));
        }

        HostContentRestorePhaseResult applied = await RunPhaseAsync(
                HostContentRestorePhase.Applied, configuration, wallpaperId, request, createRuntime, cancellationToken)
            .ConfigureAwait(false);
        if (!applied.Succeeded)
        {
            return new HostContentRestoreResult(imported, contentRoot, [applied], applied.Failure);
        }

        await Task.Delay(request.PauseBetweenHosts, cancellationToken).ConfigureAwait(false);
        HostContentRestorePhaseResult restored = await RunPhaseAsync(
                HostContentRestorePhase.Restored, configuration, wallpaperId, request, createRuntime, cancellationToken)
            .ConfigureAwait(false);
        return new HostContentRestoreResult(imported, contentRoot, [applied, restored], restored.Failure);
    }

    /// <summary>Imports the source through the Host's own pipeline into a data root under a fresh run directory.</summary>
    internal static async Task<(string Configuration, WallpaperImportResult Import, string? ContentRoot)>
        ImportIntoFreshDataRootAsync(string runDirectory, string sourceVideoPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runDirectory) || !Path.IsPathFullyQualified(runDirectory) ||
            Directory.Exists(runDirectory) || File.Exists(runDirectory))
        {
            throw new ArgumentException("A fresh absolute run directory is required.", nameof(runDirectory));
        }

        string configuration = Path.Combine(Path.GetFullPath(runDirectory), "configuration");
        Directory.CreateDirectory(configuration);
        using JsonWallpaperRepository library = WindowsHostCompositionRoot.CreateWallpaperRepository(configuration);
        WallpaperImportResult imported = await WindowsHostCompositionRoot.CreateImportCoordinator(configuration, library)
            .ImportAsync(new ImportSource(sourceVideoPath), null, cancellationToken)
            .ConfigureAwait(false);
        string? contentRoot = imported is { Succeeded: true, WallpaperId: { } importedId }
            ? (await library.FindAsync(importedId, cancellationToken).ConfigureAwait(false))?.CanonicalContentRoot
            : null;
        return (configuration, imported, contentRoot);
    }

    internal static string ImportFailure(WallpaperImportResult imported) =>
        "Import produced no readable library entry: " + string.Join(", ", imported.Diagnostics);

    private static async Task<HostContentRestorePhaseResult> RunPhaseAsync(
        HostContentRestorePhase phase,
        string configuration,
        WallpaperId wallpaperId,
        HostContentRestoreRequest request,
        HostRuntimeFactory createRuntime,
        CancellationToken cancellationToken)
    {
        HostRuntimeHandle host = createRuntime(configuration, WindowsHostCompositionRoot.CreateWallpaperRepository(configuration));
        ApplyGenerationResult? generation = null;
        IReadOnlyList<ApplyTimelineEntry> timeline = [];
        ProductCandidateSessionResult[] sessions = [];
        DisplayId? display = null;
        List<Process> processes = [];
        string? failure = null;
        try
        {
            await host.Runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            HostBootstrapResult bootstrap = host.Runtime.BootstrapResult ??
                throw new InvalidOperationException("The Host did not finish initialization.");
            if (phase == HostContentRestorePhase.Applied)
            {
                if (bootstrap.RestoreResult is not null)
                {
                    throw new InvalidOperationException("A fresh data root restored an assignment.");
                }

                HostStateSnapshot state = await GetStateAsync(host.CommandLoop, cancellationToken).ConfigureAwait(false);
                display = state.DisplayTopology?.Displays.FirstOrDefault(item => item.IsPrimary)?.Id ??
                    throw new InvalidOperationException("No primary display is connected.");
                HostCommandResult accepted = await host.CommandLoop.SendAsync(
                        new ApplyWallpaperCommand(wallpaperId, [display.Value], LayoutMode.PerDisplay, FitMode.Cover),
                        cancellationToken)
                    .ConfigureAwait(false);
                long applyGeneration = accepted is { Accepted: true, State: { } applyState }
                    ? applyState.Generation
                    : throw new InvalidOperationException($"Apply was rejected: {accepted.ErrorCode}.");
                generation = await host.CommandLoop.WaitForApplyGenerationAsync(applyGeneration, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                generation = bootstrap.RestoreGenerationResult ??
                    throw new InvalidOperationException("The Host did not restore the persisted assignment.");
            }

            timeline = host.CommandLoop.GetApplyTimelineForDiagnostics(generation.Generation);
            if (generation.State != ApplyGenerationTerminalState.Succeeded)
            {
                throw new InvalidOperationException($"{phase} generation ended as {generation.State}.");
            }

            PreparedSession[] prepared = host.CommandLoop.GetPreparedSessionsForDiagnostics()
                .OrderBy(item => item.SurfaceOrdinal)
                .ToArray();
            if (prepared.Length != 1 || prepared[0].Session.WallpaperId != wallpaperId ||
                prepared[0].Session.Displays.Count != 1 ||
                (display is { } applied && prepared[0].Session.Displays[0] != applied))
            {
                throw new InvalidOperationException("Expected exactly one session showing the imported wallpaper.");
            }

            display = prepared[0].Session.Displays[0];
            sessions = prepared.Select(RendererProductCandidateDiagnostics.ToSessionResult).ToArray();
            foreach (PreparedSession item in prepared)
            {
                if (item.Renderer is RendererProtocolSession protocol)
                {
                    Process process = Process.GetProcessById(protocol.ProcessId);
                    _ = process.SafeHandle; // Hold this identity; a reused PID must never be reopened later.
                    processes.Add(process);
                }
            }

            request.PhaseCommitted?.Invoke(phase, sessions);
            await Task.Delay(request.ObservationDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            // Retires the session and persists the Host state, exactly as a normal Host exit does.
            await host.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        request.HostStopped?.Invoke(phase);
        if (failure is null && host.Runtime.LastFault is { } fault)
        {
            failure = $"Host stopped with {fault.GetType().Name}: {fault.Message}";
        }

        SessionRetirementBatchResult retirement = new(Array.AsReadOnly(
            host.CommandLoop.GetRetirementHistoryForDiagnostics()
                .SelectMany(batch => batch.Sessions)
                .GroupBy(item => item.SessionId)
                .Select(group => group.Last())
                .ToArray()));
        List<int?> exitCodes = [];
        foreach (Process process in processes)
        {
            using (process)
            {
                exitCodes.Add(process.WaitForExit(RendererExitBudget) ? process.ExitCode : null);
            }
        }

        IReadOnlyList<WallpaperAssignment> persisted = (await new JsonHostConfigurationStore(configuration)
                .LoadAsync(CancellationToken.None)
                .ConfigureAwait(false))
            .Configuration.Assignments;
        failure ??= VerifyStopped(sessions, retirement, exitCodes, persisted, wallpaperId, display);
        return new HostContentRestorePhaseResult(phase, generation, timeline, sessions, retirement,
            exitCodes, persisted, failure);
    }

    private static string? VerifyStopped(
        IReadOnlyList<ProductCandidateSessionResult> sessions,
        SessionRetirementBatchResult retirement,
        IReadOnlyList<int?> exitCodes,
        IReadOnlyList<WallpaperAssignment> persisted,
        WallpaperId wallpaperId,
        DisplayId? display)
    {
        bool retired = sessions.All(session => retirement.Sessions.Any(item =>
            item.SessionId == session.SessionId && item.Graceful && item.OwnedResourcesCleaned && item.Issues.Count == 0));
        if (!retired)
        {
            return "The session did not retire gracefully with its owned resources cleaned.";
        }

        if (exitCodes.Any(code => code != 0))
        {
            return "A Renderer process did not exit with code 0.";
        }

        return persisted is [{ } assignment] && assignment.WallpaperId == wallpaperId &&
            assignment.DisplayId == display && assignment.LayoutMode == LayoutMode.PerDisplay &&
            assignment.FitMode == FitMode.Cover
                ? null
                : "The persisted assignment does not name the imported wallpaper on its display.";
    }

    internal static async Task<HostStateSnapshot> GetStateAsync(
        HostCommandLoop commandLoop,
        CancellationToken cancellationToken) =>
        (await commandLoop.SendAsync(new GetHostStateCommand(), cancellationToken).ConfigureAwait(false)).State ??
        throw new InvalidOperationException("The Host did not return a state snapshot.");

    private sealed class FixedDisplayTopologySource(DisplayTopology topology) : IDisplayTopologySource
    {
        public DisplayTopology Current => topology;

        public event EventHandler<DisplayTopologyChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class InertEnvironmentEventBridge : IWindowsEnvironmentEventBridge
    {
        public void Start()
        {
        }

        public void RequestEnvironmentValidation()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
