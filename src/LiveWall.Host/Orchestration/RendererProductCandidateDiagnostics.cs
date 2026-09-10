using LiveWall.Application.Importing;
using LiveWall.Application.Layouts;
using LiveWall.Application.Library;
using LiveWall.Application.Policies;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.CommandLoop;
using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.Host.Orchestration;

public sealed record RendererProductCandidateRequest(
    string RendererExecutablePath,
    WindowsShellSnapshot ShellSnapshot,
    DisplayTopology DisplayTopology,
    TimeSpan VisibleDuration,
    bool ExtendedSoak = false,
    ProductCandidateDisplaySelection? DisplaySelection = null,
    DiagnosticRendererEnvironmentTarget? RendererEnvironment = null,
    TimeSpan? RendererStageTimeout = null);

public sealed record DiagnosticRendererEnvironmentTarget(
    int TargetIteration,
    int TargetSurfaceOrdinal,
    IReadOnlyDictionary<string, string> Variables);

public enum ProductCandidateDisplaySelectionKind
{
    Primary,
    All,
    Explicit,
}

public sealed record ProductCandidateDisplaySelection(
    ProductCandidateDisplaySelectionKind Kind,
    IReadOnlyList<DisplayId> DisplayIds)
{
    public static ProductCandidateDisplaySelection Primary { get; } =
        new(ProductCandidateDisplaySelectionKind.Primary, []);

    public static ProductCandidateDisplaySelection All { get; } =
        new(ProductCandidateDisplaySelectionKind.All, []);

    public static ProductCandidateDisplaySelection Explicit(params DisplayId[] displayIds) =>
        new(ProductCandidateDisplaySelectionKind.Explicit, Array.AsReadOnly(displayIds));
}

public sealed record RendererProductCandidateLoopRequest(
    string RendererExecutablePath,
    WindowsShellSnapshot ShellSnapshot,
    DisplayTopology DisplayTopology,
    TimeSpan VisibleDuration,
    int Iterations,
    ProductCandidateDisplaySelection? DisplaySelection = null,
    DiagnosticRendererEnvironmentTarget? RendererEnvironment = null);

public sealed record RendererProductCandidateIterationResult(
    int Iteration,
    long HostGeneration,
    IReadOnlyList<ProductCandidateSessionResult> Sessions,
    int SurfaceCount,
    ApplyGenerationResult GenerationResult,
    IReadOnlyList<ApplyTimelineEntry> Timeline);

public sealed record RendererProductCandidateLoopResult(
    IReadOnlyList<RendererProductCandidateIterationResult> Iterations,
    IReadOnlyList<RendererProductCandidateLoopRetirement> Retirements,
    SessionRetirementBatchResult RetirementResult,
    bool OwnedResourcesCleaned);

public sealed record RendererProductCandidateLoopRetirement(
    SessionRetirementResult Result,
    string Trigger);

public sealed record ProductCandidateSessionResult(
    SessionId SessionId,
    RendererId RendererId,
    IReadOnlyList<DisplayId> DisplayIds,
    int SurfaceOrdinal,
    int SurfaceCount);

public sealed record RendererProductCandidateResult(
    long HostGeneration,
    IReadOnlyList<ProductCandidateSessionResult> Sessions,
    int SurfaceCount,
    bool FirstFrameCommitted,
    bool OwnedResourcesCleaned,
    ApplyGenerationResult GenerationResult,
    IReadOnlyList<ApplyTimelineEntry> Timeline,
    SessionRetirementBatchResult RetirementResult);

public sealed class RendererProductCandidateFailureException : Exception
{
    public RendererProductCandidateFailureException(
        ApplyGenerationResult generationResult,
        IReadOnlyList<ApplyTimelineEntry> timeline,
        IReadOnlyList<SessionId>? activeSessionIds = null,
        int preparedSurfaceCount = 0)
        : base(
            $"Product candidate generation {generationResult.Generation} ended as " +
            $"{generationResult.State} at {generationResult.TerminalPhase}: " +
            $"{generationResult.FailureReason}: {generationResult.Detail}")
    {
        GenerationResult = generationResult;
        Timeline = timeline;
        ActiveSessionIds = activeSessionIds ?? [];
        PreparedSurfaceCount = preparedSurfaceCount;
    }

    public ApplyGenerationResult GenerationResult { get; }

    public IReadOnlyList<ApplyTimelineEntry> Timeline { get; }

    public IReadOnlyList<SessionId> ActiveSessionIds { get; }

    public int PreparedSurfaceCount { get; }
}

/// <summary>
/// Explicit diagnostic-only composition root for the real Host candidate chain.
/// Production adapter allowlists remain authoritative outside this entry point.
/// </summary>
public static class RendererProductCandidateDiagnostics
{
    public static async Task<RendererProductCandidateResult> RunAsync(
        RendererProductCandidateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RendererExecutablePath);
        if (request.DisplayTopology.Displays.Count == 0)
        {
            throw new ArgumentException("Product candidate diagnostics require a display.", nameof(request));
        }
        TimeSpan maximumVisibleDuration = request.ExtendedSoak
            ? TimeSpan.FromHours(1)
            : TimeSpan.FromSeconds(30);
        if (request.VisibleDuration < TimeSpan.FromSeconds(1) ||
            request.VisibleDuration > maximumVisibleDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Visible duration must be between 1 second and {maximumVisibleDuration}.");
        }
        if (request.RendererStageTimeout is { } rendererStageTimeout &&
            rendererStageTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Renderer stage timeout must be positive.");
        }

        WallpaperDefinition wallpaper = CreateSyntheticWallpaper();
        InMemoryDiagnosticWallpaperRepository repository = new(
            new WallpaperLibraryEntry(
                wallpaper,
                AppContext.BaseDirectory,
                new CompatibilityReport(CompatibilityGrade.Native, [])));
        RendererProcessProvider provider = CreateRendererProvider(
            request.RendererExecutablePath,
            request.RendererEnvironment);
        await using WindowsDesktopHost desktopHost =
            WindowsDesktopHost.CreateExperimentalDiagnostics(request.ShellSnapshot);
        SessionCoordinator coordinator = new(
            repository,
            new DefaultLayoutPlanner(),
            new RendererProviderSelector([provider]),
            desktopHost,
            new RuntimeEnvironment("win-x64", true, new HashSet<string>()),
            request.RendererStageTimeout ?? TimeSpan.FromSeconds(10));
        await using HostCommandLoop commandLoop = new(
            coordinator,
            new DefaultPlaybackPolicyEvaluator());
        PreparedSession[] sessions = [];
        ApplyGenerationResult? generationResult = null;
        ProductCandidateSessionResult[] sessionResults = [];
        int surfaceCount = 0;
        bool ownedResourcesCleaned = false;
        SessionRetirementBatchResult? retirementResult = null;
        try
        {
            // Surface discovery failures must be observable to diagnostics instead of being
            // reduced to the command loop's asynchronous Apply failure notification.
            await desktopHost.EnsureTopologyAsync(request.DisplayTopology, cancellationToken)
                .ConfigureAwait(false);
            await commandLoop.SendAsync(
                    new DisplayTopologyChangedCommand(request.DisplayTopology),
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<DisplayId> selectedDisplays = ResolveDisplayIds(
                request.DisplayTopology,
                request.DisplaySelection ?? ProductCandidateDisplaySelection.Primary);
            HostCommandResult accepted = await commandLoop.SendAsync(
                    new ApplyWallpaperCommand(
                        wallpaper.Id,
                        selectedDisplays,
                        LayoutMode.PerDisplay,
                        FitMode.Cover,
                        null),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!accepted.Accepted)
            {
                throw new InvalidOperationException(
                    $"Product candidate Apply was rejected: {accepted.ErrorCode}.");
            }

            long generation = accepted.State?.Generation ??
                throw new InvalidOperationException(
                    "Product candidate Apply did not return its generation.");
            generationResult = await commandLoop.WaitForApplyGenerationAsync(
                    generation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (generationResult.State != ApplyGenerationTerminalState.Succeeded)
            {
                HostStateSnapshot failedState = (await commandLoop.SendAsync(
                        new GetHostStateCommand(),
                        CancellationToken.None)
                    .ConfigureAwait(false)).State ??
                    throw new InvalidOperationException("Host did not return a failed state snapshot.");
                int preparedSurfaceCount = commandLoop.GetPreparedSessionsForDiagnostics()
                    .SelectMany(item => item.Surfaces)
                    .Count();
                throw new RendererProductCandidateFailureException(
                    generationResult,
                    commandLoop.GetApplyTimelineForDiagnostics(generation),
                    Array.AsReadOnly(failedState.Sessions.Select(item => item.Session.Id).ToArray()),
                    preparedSurfaceCount);
            }

            HostCommandResult stateResult = await commandLoop.SendAsync(
                    new GetHostStateCommand(),
                    cancellationToken)
                .ConfigureAwait(false);
            HostStateSnapshot active = stateResult.State ??
                throw new InvalidOperationException("Host did not return a state snapshot.");
            if (active.Generation != generation ||
                active.Sessions.Count != selectedDisplays.Count)
            {
                throw new InvalidOperationException(
                    $"Succeeded Apply generation published {active.Sessions.Count} sessions; " +
                    $"{selectedDisplays.Count} were required.");
            }

            sessions = commandLoop.GetPreparedSessionsForDiagnostics().ToArray();
            surfaceCount = sessions.SelectMany(item => item.Surfaces).Count();
            HashSet<DisplayId> coveredDisplays = sessions
                .SelectMany(item => item.Session.Displays)
                .ToHashSet();
            if (surfaceCount != selectedDisplays.Count ||
                !coveredDisplays.SetEquals(selectedDisplays) ||
                sessions.Any(item => item.Session.Displays.Count != 1))
            {
                throw new InvalidOperationException(
                    "Product candidate sessions did not provide one independent Surface per selected display.");
            }
            IReadOnlyList<ApplyTimelineEntry> timeline =
                commandLoop.GetApplyTimelineForDiagnostics(generation);
            if (timeline.Count(entry =>
                    entry.Phase == ApplyGenerationPhase.FirstFramePresented &&
                    entry.Outcome == ApplyStageOutcome.Success) != selectedDisplays.Count ||
                timeline.Count(entry =>
                    entry.Phase == ApplyGenerationPhase.SurfacesReplaced &&
                    entry.Outcome == ApplyStageOutcome.Success) != 1)
            {
                throw new InvalidOperationException(
                    "Product candidate did not observe every first frame before one atomic Surface replacement.");
            }

            sessionResults = sessions
                .OrderBy(item => item.SurfaceOrdinal)
                .Select(item => new ProductCandidateSessionResult(
                    item.Session.Id,
                    item.Session.RendererId,
                    Array.AsReadOnly(item.Session.Displays.ToArray()),
                    item.SurfaceOrdinal,
                    item.Surfaces.Count))
                .ToArray();
            await Task.Delay(request.VisibleDuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (sessions.Length > 0)
            {
                retirementResult = await commandLoop.RetireActiveSessionsForDiagnosticsAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
                ownedResourcesCleaned = retirementResult.OwnedResourcesCleaned;
                sessions = [];
            }
        }

        return new RendererProductCandidateResult(
            generationResult.Generation,
            Array.AsReadOnly(sessionResults),
            surfaceCount,
            FirstFrameCommitted: true,
            ownedResourcesCleaned,
            generationResult,
            commandLoop.GetApplyTimelineForDiagnostics(generationResult.Generation),
            retirementResult ?? new SessionRetirementBatchResult([]));
    }

    public static IReadOnlyList<DisplayId> ResolveDisplayIds(
        DisplayTopology topology,
        ProductCandidateDisplaySelection selection)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(selection);
        if (topology.Displays.Count == 0)
        {
            throw new ArgumentException(
                "Product candidate display selection requires a non-empty topology.",
                nameof(topology));
        }
        DisplayId[] selected = selection.Kind switch
        {
            ProductCandidateDisplaySelectionKind.Primary =>
            [topology.Displays.FirstOrDefault(display => display.IsPrimary)?.Id ??
                topology.Displays[0].Id],
            ProductCandidateDisplaySelectionKind.All =>
                topology.Displays.Select(display => display.Id).ToArray(),
            ProductCandidateDisplaySelectionKind.Explicit => selection.DisplayIds.ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(selection)),
        };
        if (selected.Length == 0)
        {
            throw new ArgumentException(
                "Explicit product candidate display selection cannot be empty.",
                nameof(selection));
        }
        if (selected.Distinct().Count() != selected.Length)
        {
            throw new ArgumentException(
                "Product candidate display selection cannot contain duplicates.",
                nameof(selection));
        }

        HashSet<DisplayId> available = topology.Displays.Select(display => display.Id).ToHashSet();
        DisplayId[] missing = selected.Where(displayId => !available.Contains(displayId)).ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown product candidate display IDs: {string.Join(",", missing.Select(id => id.Value))}.",
                nameof(selection));
        }
        return Array.AsReadOnly(selected);
    }

    public static async Task<RendererProductCandidateLoopResult> RunLoopAsync(
        RendererProductCandidateLoopRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RendererExecutablePath);
        if (request.DisplayTopology.Displays.Count == 0)
        {
            throw new ArgumentException("Product candidate diagnostics require a display.", nameof(request));
        }
        if (request.VisibleDuration < TimeSpan.FromSeconds(1) ||
            request.VisibleDuration > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Loop iteration duration must be between 1 and 30 seconds.");
        }
        if (request.Iterations is < 2 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Loop iterations must be between 2 and 100.");
        }

        WallpaperDefinition wallpaper = CreateSyntheticWallpaper();
        InMemoryDiagnosticWallpaperRepository repository = new(
            new WallpaperLibraryEntry(
                wallpaper,
                AppContext.BaseDirectory,
                new CompatibilityReport(CompatibilityGrade.Native, [])));
        RendererProcessProvider provider = CreateRendererProvider(
            request.RendererExecutablePath,
            request.RendererEnvironment);
        await using WindowsDesktopHost desktopHost =
            WindowsDesktopHost.CreateExperimentalDiagnostics(request.ShellSnapshot);
        SessionCoordinator coordinator = new(
            repository,
            new DefaultLayoutPlanner(),
            new RendererProviderSelector([provider]),
            desktopHost,
            new RuntimeEnvironment("win-x64", true, new HashSet<string>()),
            TimeSpan.FromSeconds(10));
        await using HostCommandLoop commandLoop = new(
            coordinator,
            new DefaultPlaybackPolicyEvaluator());

        await desktopHost.EnsureTopologyAsync(request.DisplayTopology, cancellationToken)
            .ConfigureAwait(false);
        await commandLoop.SendAsync(
                new DisplayTopologyChangedCommand(request.DisplayTopology),
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<DisplayId> selectedDisplays = ResolveDisplayIds(
            request.DisplayTopology,
            request.DisplaySelection ?? ProductCandidateDisplaySelection.Primary);
        List<RendererProductCandidateIterationResult> iterations = [];
        Dictionary<SessionId, RendererProductCandidateLoopRetirement> retirements = [];

        for (int iteration = 1; iteration <= request.Iterations; iteration++)
        {
            HostCommandResult accepted = await commandLoop.SendAsync(
                    new ApplyWallpaperCommand(
                        wallpaper.Id,
                        selectedDisplays,
                        LayoutMode.PerDisplay,
                        FitMode.Cover,
                        null),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!accepted.Accepted)
            {
                throw new InvalidOperationException(
                    $"Product candidate loop Apply {iteration} was rejected: {accepted.ErrorCode}.");
            }

            long generation = accepted.State?.Generation ??
                throw new InvalidOperationException(
                    $"Product candidate loop Apply {iteration} did not return its generation.");
            ApplyGenerationResult generationResult = await commandLoop.WaitForApplyGenerationAsync(
                    generation,
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<ApplyTimelineEntry> timeline =
                commandLoop.GetApplyTimelineForDiagnostics(generation);
            if (generationResult.State != ApplyGenerationTerminalState.Succeeded)
            {
                throw new RendererProductCandidateFailureException(generationResult, timeline);
            }

            if (iterations.Count > 0)
            {
                SessionId[] replacedSessionIds = iterations[^1].Sessions
                    .Select(session => session.SessionId)
                    .ToArray();
                SessionRetirementBatchResult replaced =
                    await commandLoop.WaitForSessionRetirementsForDiagnosticsAsync(
                            replacedSessionIds,
                            cancellationToken)
                        .ConfigureAwait(false);
                foreach (SessionRetirementResult retired in replaced.Sessions)
                {
                    retirements[retired.SessionId] = new(
                        retired,
                        "Replacement");
                }
            }

            HostStateSnapshot active = (await commandLoop.SendAsync(
                    new GetHostStateCommand(),
                    cancellationToken)
                .ConfigureAwait(false)).State ??
                throw new InvalidOperationException("Host did not return a state snapshot.");
            if (active.Generation != generation ||
                active.Sessions.Count != selectedDisplays.Count)
            {
                throw new InvalidOperationException(
                    $"Succeeded loop generation {generation} published " +
                    $"{active.Sessions.Count} sessions; {selectedDisplays.Count} were required.");
            }

            PreparedSession[] prepared = commandLoop.GetPreparedSessionsForDiagnostics().ToArray();
            int surfaceCount = prepared.SelectMany(item => item.Surfaces).Count();
            HashSet<DisplayId> coveredDisplays = prepared
                .SelectMany(item => item.Session.Displays)
                .ToHashSet();
            HashSet<SessionId> activeSessionIds = active.Sessions
                .Select(item => item.Session.Id)
                .ToHashSet();
            if (surfaceCount != selectedDisplays.Count ||
                !coveredDisplays.SetEquals(selectedDisplays) ||
                prepared.Any(item => item.Session.Displays.Count != 1) ||
                !activeSessionIds.SetEquals(prepared.Select(item => item.Session.Id)))
            {
                throw new InvalidOperationException(
                    $"Product candidate loop generation {generation} did not provide " +
                    "one active independent Surface per selected display.");
            }
            if (timeline.Count(entry =>
                    entry.Phase == ApplyGenerationPhase.FirstFramePresented &&
                    entry.Outcome == ApplyStageOutcome.Success) != selectedDisplays.Count ||
                timeline.Count(entry =>
                    entry.Phase == ApplyGenerationPhase.SurfacesReplaced &&
                    entry.Outcome == ApplyStageOutcome.Success) != 1)
            {
                throw new InvalidOperationException(
                    $"Product candidate loop generation {generation} did not observe " +
                    "every first frame before one atomic Surface replacement.");
            }
            ProductCandidateSessionResult[] sessionResults = prepared
                .OrderBy(item => item.SurfaceOrdinal)
                .Select(item => new ProductCandidateSessionResult(
                    item.Session.Id,
                    item.Session.RendererId,
                    Array.AsReadOnly(item.Session.Displays.ToArray()),
                    item.SurfaceOrdinal,
                    item.Surfaces.Count))
                .ToArray();
            iterations.Add(new RendererProductCandidateIterationResult(
                iteration,
                generation,
                Array.AsReadOnly(sessionResults),
                surfaceCount,
                generationResult,
                timeline));
            await Task.Delay(request.VisibleDuration, cancellationToken).ConfigureAwait(false);
        }

        SessionRetirementBatchResult finalRetirement =
            await commandLoop.RetireActiveSessionsForDiagnosticsAsync(CancellationToken.None)
                .ConfigureAwait(false);
        foreach (SessionRetirementResult retired in finalRetirement.Sessions)
        {
            retirements[retired.SessionId] = new(
                retired,
                "Shutdown");
        }

        RendererProductCandidateLoopRetirement[] orderedRetirements = retirements.Values
            .OrderBy(item => item.Result.Generation)
            .ToArray();
        SessionRetirementBatchResult retirementResult = new(
            Array.AsReadOnly(orderedRetirements.Select(item => item.Result).ToArray()));
        return new RendererProductCandidateLoopResult(
            Array.AsReadOnly(iterations.ToArray()),
            Array.AsReadOnly(orderedRetirements),
            retirementResult,
            retirementResult.OwnedResourcesCleaned);
    }

    private static RendererProcessProvider CreateRendererProvider(
        string executablePath,
        DiagnosticRendererEnvironmentTarget? environmentTarget)
    {
        if (environmentTarget is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(environmentTarget.TargetIteration);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                environmentTarget.TargetSurfaceOrdinal);
            ArgumentNullException.ThrowIfNull(environmentTarget.Variables);
            if (environmentTarget.Variables.Keys.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    "Renderer diagnostic environment names cannot be empty.",
                    nameof(environmentTarget));
            }
        }

        return new RendererProcessProvider(
            new RendererProcessProviderOptions(
                new RendererDescriptor(
                    new RendererId("renderer-child-dcomp-probe"),
                    "Renderer-child DirectComposition test Renderer",
                    new HashSet<WallpaperKind> { WallpaperKind.Scene },
                    new HashSet<string>(["HwndChild", "DirectComposition"])),
                executablePath,
                "diagnostics",
                ["HwndChild", "DirectComposition"],
                "en-US",
                TimeSpan.FromSeconds(10)),
            processEnvironmentFactory: environmentTarget is null
                ? null
                : (_, iteration, surfaceOrdinal) =>
                    iteration == environmentTarget.TargetIteration &&
                    surfaceOrdinal == environmentTarget.TargetSurfaceOrdinal
                        ? environmentTarget.Variables
                        : null);
    }

    private static WallpaperDefinition CreateSyntheticWallpaper() => new(
        new WallpaperId("diagnostic.synthetic.color"),
        "1.0.0",
        WallpaperKind.Scene,
        "synthetic-color",
        new WallpaperMetadata(
            "LiveWall product candidate color",
            null,
            null,
            null,
            new HashSet<string>()),
        new WallpaperCapabilities(false, false, false, false, 30),
        WallpaperOrigin.Native);

    private sealed class InMemoryDiagnosticWallpaperRepository(WallpaperLibraryEntry entry)
        : IWallpaperRepository
    {
        public Task<WallpaperLibraryEntry?> FindAsync(
            WallpaperId id,
            CancellationToken cancellationToken) =>
            Task.FromResult<WallpaperLibraryEntry?>(id == entry.Definition.Id ? entry : null);

        public Task<IReadOnlyList<WallpaperLibraryEntry>> ListAsync(
            WallpaperQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WallpaperLibraryEntry>>([entry]);

        public Task SaveImportedAsync(
            ImportedWallpaper wallpaper,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Diagnostic repository is read-only.");

        public Task RemoveAsync(WallpaperId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Diagnostic repository is read-only.");
    }
}
