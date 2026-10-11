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
    DiagnosticRendererEnvironmentTarget? RendererEnvironment = null,
    TimeSpan? RetirementTimeout = null,
    IReadOnlyList<LayoutMode>? LayoutSequence = null);

public sealed record RendererProductCandidateIterationResult(
    int Iteration,
    long HostGeneration,
    IReadOnlyList<ProductCandidateSessionResult> Sessions,
    int SurfaceCount,
    ApplyGenerationResult GenerationResult,
    IReadOnlyList<ApplyTimelineEntry> Timeline,
    LayoutMode LayoutMode = LayoutMode.PerDisplay);

public sealed record RendererProductCandidateLoopResult(
    IReadOnlyList<RendererProductCandidateIterationResult> Iterations,
    IReadOnlyList<RendererProductCandidateLoopRetirement> Retirements,
    SessionRetirementBatchResult RetirementResult,
    bool OwnedResourcesCleaned);

public sealed record RendererProductCandidateLoopRetirement(
    SessionRetirementResult Result,
    string Trigger);

public sealed record RendererProductCandidateReplacementFaultRequest(
    string RendererExecutablePath,
    WindowsShellSnapshot ShellSnapshot,
    DisplayTopology DisplayTopology,
    TimeSpan ObservationDuration,
    DiagnosticRendererEnvironmentTarget RendererEnvironment,
    ProductCandidateDisplaySelection? DisplaySelection = null,
    TimeSpan? RendererStageTimeout = null);

public sealed record RendererProductCandidateReplacementFaultResult(
    RendererProductCandidateIterationResult CommittedBeforeFailure,
    ApplyGenerationResult FailedAttempt,
    IReadOnlyList<ApplyTimelineEntry> FailedAttemptTimeline,
    RendererProductCandidateIterationResult CommittedAfterFailure,
    bool PreviousSessionsPreserved,
    bool FailedAttemptReplacedSurfaces,
    bool FailedAttemptResourcesCleaned,
    SessionRetirementBatchResult PreviousSessionRetirement,
    SessionRetirementBatchResult FinalRetirement);

public sealed record ProductCandidateSessionResult(
    SessionId SessionId,
    RendererId RendererId,
    IReadOnlyList<DisplayId> DisplayIds,
    int SurfaceOrdinal,
    int SurfaceCount,
    int? RendererProcessId = null);

public sealed record RendererProductCandidateRecoveryRequest(
    string RendererExecutablePath,
    WindowsShellSnapshot ShellSnapshot,
    DisplayTopology DisplayTopology,
    TimeSpan InitialVisibleDuration,
    TimeSpan RecoveredVisibleDuration,
    Func<CancellationToken, Task> RestartShellAsync,
    ProductCandidateDisplaySelection? DisplaySelection = null,
    DiagnosticRendererEnvironmentTarget? RendererEnvironment = null,
    DesktopRecoveryCompletionMode ExpectedCompletionMode =
        DesktopRecoveryCompletionMode.Reattached,
    TimeSpan? RendererStageTimeout = null);

public sealed record RendererProductCandidateRecoveryResult(
    RendererProductCandidateIterationResult Initial,
    DesktopRecoveryResult Recovery,
    IReadOnlyList<ProductCandidateSessionResult> RecoveredSessions,
    SessionRetirementBatchResult FinalRetirement);

public sealed record RendererProductCandidateTopologyRequest(
    string RendererExecutablePath,
    WindowsShellSnapshot ShellSnapshot,
    DisplayTopology InitialTopology,
    int TopologyChanges,
    TimeSpan ObservationDuration,
    Func<int, DisplayTopology, CancellationToken, Task<DisplayTopology>>
        AwaitTopologyChangeAsync,
    ProductCandidateDisplaySelection? DisplaySelection = null,
    TimeSpan? RendererStageTimeout = null,
    Func<DisplayTopology, TimeSpan, CancellationToken, Task<bool>>?
        VerifyTopologyStableAsync = null,
    RendererProductCandidateTransitionGuardRequest? TransitionGuard = null,
    LayoutMode LayoutMode = LayoutMode.PerDisplay);

public sealed record RendererProductCandidateTransitionGuardRequest(
    DisplayId TargetDisplayId,
    uint GuardColorReference,
    Action<DesktopTransitionGuardProbeResult>? Observer = null);

public sealed record RendererProductCandidateTopologyTransition(
    int Index,
    DisplayTopology PreviousTopology,
    DisplayTopology CurrentTopology,
    DisplayReconciliationResult Reconciliation,
    IReadOnlyList<ProductCandidateSessionResult> ActiveSessions,
    SessionRetirementBatchResult RetiredSessions);

public sealed record RendererProductCandidateTopologyResult(
    RendererProductCandidateIterationResult Initial,
    IReadOnlyList<RendererProductCandidateTopologyTransition> Transitions,
    SessionRetirementBatchResult FinalRetirement,
    DesktopTransitionGuardProbeResult? TransitionGuard = null);

public sealed record RendererProductCandidateTopologyChurnRequest(
    string RendererExecutablePath,
    WindowsShellSnapshot ShellSnapshot,
    DisplayTopology InitialTopology,
    int MaximumPublishedChanges,
    TimeSpan FinalObservationDuration,
    IAsyncEnumerable<DisplayTopology> PublishedTopologies,
    ProductCandidateDisplaySelection? DisplaySelection = null,
    TimeSpan? RendererStageTimeout = null,
    TimeSpan? PreparedCommitHold = null);

public sealed record RendererProductCandidateTopologyChurnRevision(
    int Index,
    DisplayTopology PreviousTopology,
    DisplayTopology CurrentTopology,
    DisplayReconciliationResult Reconciliation);

public sealed record RendererProductCandidateTopologyChurnResult(
    RendererProductCandidateIterationResult Initial,
    IReadOnlyList<RendererProductCandidateTopologyChurnRevision> Revisions,
    IReadOnlyList<ProductCandidateSessionResult> FinalActiveSessions,
    SessionRetirementBatchResult FinalRetirement);

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
                    item.Surfaces.Count,
                    (item.Renderer as RendererProtocolSession)?.ProcessId))
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
        if (request.RetirementTimeout is { } retirementTimeout && retirementTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Retirement timeout must be positive.");
        }
        if (request.LayoutSequence is { } layoutSequence &&
            (layoutSequence.Count != request.Iterations ||
             layoutSequence.Any(mode => !Enum.IsDefined(mode))))
        {
            throw new ArgumentException(
                "A loop layout sequence must contain one known layout mode per iteration.",
                nameof(request));
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
            TimeSpan.FromSeconds(10),
            retirementPolicy: request.RetirementTimeout is null
                ? null
                : new SessionRetirementPolicy(
                    request.RetirementTimeout.Value,
                    TimeSpan.FromSeconds(2)));
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
            LayoutMode layoutMode = request.LayoutSequence?[iteration - 1] ??
                LayoutMode.PerDisplay;
            HostCommandResult accepted = await commandLoop.SendAsync(
                    new ApplyWallpaperCommand(
                        wallpaper.Id,
                        selectedDisplays,
                        layoutMode,
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
            int expectedSessionCount = layoutMode == LayoutMode.Span
                ? 1
                : selectedDisplays.Count;
            if (active.Generation != generation ||
                active.Sessions.Count != expectedSessionCount)
            {
                throw new InvalidOperationException(
                    $"Succeeded loop generation {generation} published " +
                    $"{active.Sessions.Count} sessions; {expectedSessionCount} were required.");
            }

            PreparedSession[] prepared = commandLoop.GetPreparedSessionsForDiagnostics().ToArray();
            int surfaceCount = prepared.SelectMany(item => item.Surfaces).Count();
            HashSet<DisplayId> coveredDisplays = prepared
                .SelectMany(item => item.Session.Displays)
                .ToHashSet();
            HashSet<SessionId> activeSessionIds = active.Sessions
                .Select(item => item.Session.Id)
                .ToHashSet();
            if (surfaceCount != expectedSessionCount ||
                !coveredDisplays.SetEquals(selectedDisplays) ||
                prepared.Any(item => item.Session.Displays.Count !=
                    (layoutMode == LayoutMode.Span ? selectedDisplays.Count : 1)) ||
                !activeSessionIds.SetEquals(prepared.Select(item => item.Session.Id)))
            {
                throw new InvalidOperationException(
                    $"Product candidate loop generation {generation} did not provide " +
                    $"the expected {layoutMode} Surface topology.");
            }
            if (timeline.Count(entry =>
                    entry.Phase == ApplyGenerationPhase.FirstFramePresented &&
                    entry.Outcome == ApplyStageOutcome.Success) != expectedSessionCount ||
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
                    item.Surfaces.Count,
                    (item.Renderer as RendererProtocolSession)?.ProcessId))
                .ToArray();
            iterations.Add(new RendererProductCandidateIterationResult(
                iteration,
                generation,
                Array.AsReadOnly(sessionResults),
                surfaceCount,
                generationResult,
                timeline,
                layoutMode));
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

    public static async Task<RendererProductCandidateReplacementFaultResult>
        RunReplacementFaultAsync(
            RendererProductCandidateReplacementFaultRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RendererExecutablePath);
        if (request.DisplayTopology.Displays.Count == 0)
        {
            throw new ArgumentException("Replacement fault diagnostics require a display.", nameof(request));
        }
        if (request.ObservationDuration < TimeSpan.FromSeconds(1) ||
            request.ObservationDuration > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Replacement fault observation duration must be between 1 and 30 seconds.");
        }
        if (request.RendererEnvironment.TargetIteration != 2)
        {
            throw new ArgumentException(
                "Replacement fault injection must target iteration 2.",
                nameof(request));
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

        await desktopHost.EnsureTopologyAsync(request.DisplayTopology, cancellationToken)
            .ConfigureAwait(false);
        await commandLoop.SendAsync(
                new DisplayTopologyChangedCommand(request.DisplayTopology),
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<DisplayId> selectedDisplays = ResolveDisplayIds(
            request.DisplayTopology,
            request.DisplaySelection ?? ProductCandidateDisplaySelection.All);
        if (selectedDisplays.Count < 2)
        {
            throw new InvalidOperationException(
                "Replacement fault diagnostics require at least two selected displays.");
        }

        SessionRetirementBatchResult finalRetirement = new([]);
        try
        {
            RendererProductCandidateIterationResult committedA = await ApplySuccessfulIterationAsync(
                    commandLoop,
                    wallpaper.Id,
                    selectedDisplays,
                    iteration: 1,
                    cancellationToken)
                .ConfigureAwait(false);
            SessionId[] committedAIds = committedA.Sessions.Select(item => item.SessionId).ToArray();
            await Task.Delay(request.ObservationDuration, cancellationToken).ConfigureAwait(false);

            HostCommandResult acceptedB = await commandLoop.SendAsync(
                    CreateApplyCommand(wallpaper.Id, selectedDisplays),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!acceptedB.Accepted)
            {
                throw new InvalidOperationException(
                    $"Replacement fault Apply B was rejected: {acceptedB.ErrorCode}.");
            }
            long generationB = acceptedB.State?.Generation ??
                throw new InvalidOperationException("Replacement fault Apply B had no generation.");
            ApplyGenerationResult failedB = await commandLoop.WaitForApplyGenerationAsync(
                    generationB,
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<ApplyTimelineEntry> timelineB =
                commandLoop.GetApplyTimelineForDiagnostics(generationB);
            if (failedB.State != ApplyGenerationTerminalState.Failed)
            {
                throw new InvalidOperationException(
                    $"Replacement fault Apply B ended as {failedB.State}, not Failed.");
            }

            HostStateSnapshot afterB = await GetStateAsync(commandLoop, cancellationToken)
                .ConfigureAwait(false);
            HashSet<SessionId> afterBIds = afterB.Sessions.Select(item => item.Session.Id).ToHashSet();
            HashSet<SessionId> preparedAfterBIds = commandLoop.GetPreparedSessionsForDiagnostics()
                .Select(item => item.Session.Id)
                .ToHashSet();
            bool previousSessionsPreserved = afterBIds.SetEquals(committedAIds) &&
                preparedAfterBIds.SetEquals(committedAIds) &&
                afterB.Sessions.All(item => item.Session.Generation == committedA.HostGeneration);
            bool replacedB = timelineB.Any(entry =>
                entry.Phase == ApplyGenerationPhase.SurfacesReplaced &&
                entry.Outcome == ApplyStageOutcome.Success);
            int cleanedB = timelineB.Count(entry =>
                entry.Phase == ApplyGenerationPhase.CleanupVerified &&
                entry.Outcome == ApplyStageOutcome.Success);
            bool resourcesCleanedB = cleanedB == selectedDisplays.Count;
            if (!previousSessionsPreserved || replacedB || !resourcesCleanedB)
            {
                throw new InvalidOperationException(
                    "Replacement fault Apply B violated old-session preservation or provisional cleanup invariants.");
            }

            await Task.Delay(request.ObservationDuration, cancellationToken).ConfigureAwait(false);
            RendererProductCandidateIterationResult committedC = await ApplySuccessfulIterationAsync(
                    commandLoop,
                    wallpaper.Id,
                    selectedDisplays,
                    iteration: 3,
                    cancellationToken)
                .ConfigureAwait(false);
            HashSet<SessionId> committedCIds = committedC.Sessions
                .Select(item => item.SessionId)
                .ToHashSet();
            if (committedCIds.Overlaps(committedAIds))
            {
                throw new InvalidOperationException("Apply C reused a Session from committed Apply A.");
            }
            SessionRetirementBatchResult retiredA =
                await commandLoop.WaitForSessionRetirementsForDiagnosticsAsync(
                        committedAIds,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!retiredA.Graceful || !retiredA.OwnedResourcesCleaned)
            {
                throw new InvalidOperationException("Committed Apply A did not retire cleanly after Apply C.");
            }
            await Task.Delay(request.ObservationDuration, cancellationToken).ConfigureAwait(false);

            finalRetirement = await commandLoop.RetireActiveSessionsForDiagnosticsAsync(
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!finalRetirement.Graceful || !finalRetirement.OwnedResourcesCleaned)
            {
                throw new InvalidOperationException("Committed Apply C did not retire cleanly.");
            }
            return new RendererProductCandidateReplacementFaultResult(
                committedA,
                failedB,
                timelineB,
                committedC,
                previousSessionsPreserved,
                replacedB,
                resourcesCleanedB,
                retiredA,
                finalRetirement);
        }
        finally
        {
            if (commandLoop.GetPreparedSessionsForDiagnostics().Count > 0)
            {
                _ = await commandLoop.RetireActiveSessionsForDiagnosticsAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<RendererProductCandidateIterationResult> ApplySuccessfulIterationAsync(
        HostCommandLoop commandLoop,
        WallpaperId wallpaperId,
        IReadOnlyList<DisplayId> selectedDisplays,
        int iteration,
        CancellationToken cancellationToken,
        LayoutMode layoutMode = LayoutMode.PerDisplay)
    {
        HostCommandResult accepted = await commandLoop.SendAsync(
                CreateApplyCommand(wallpaperId, selectedDisplays, layoutMode),
                cancellationToken)
            .ConfigureAwait(false);
        if (!accepted.Accepted)
        {
            throw new InvalidOperationException(
                $"Product candidate Apply {iteration} was rejected: {accepted.ErrorCode}.");
        }
        long generation = accepted.State?.Generation ??
            throw new InvalidOperationException(
                $"Product candidate Apply {iteration} did not return its generation.");
        ApplyGenerationResult result = await commandLoop.WaitForApplyGenerationAsync(
                generation,
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ApplyTimelineEntry> timeline =
            commandLoop.GetApplyTimelineForDiagnostics(generation);
        if (result.State != ApplyGenerationTerminalState.Succeeded)
        {
            throw new RendererProductCandidateFailureException(result, timeline);
        }
        PreparedSession[] prepared = commandLoop.GetPreparedSessionsForDiagnostics()
            .OrderBy(item => item.SurfaceOrdinal)
            .ToArray();
        int expectedSessionCount = layoutMode == LayoutMode.Span
            ? 1
            : selectedDisplays.Count;
        if (prepared.Length != expectedSessionCount ||
            timeline.Count(entry => entry.Phase == ApplyGenerationPhase.SurfacesReplaced &&
                entry.Outcome == ApplyStageOutcome.Success) != 1)
        {
            throw new InvalidOperationException(
                $"Product candidate Apply {iteration} did not atomically commit all selected displays.");
        }
        return new RendererProductCandidateIterationResult(
            iteration,
            generation,
            Array.AsReadOnly(prepared.Select(item => new ProductCandidateSessionResult(
                item.Session.Id,
                item.Session.RendererId,
                Array.AsReadOnly(item.Session.Displays.ToArray()),
                item.SurfaceOrdinal,
                item.Surfaces.Count,
                (item.Renderer as RendererProtocolSession)?.ProcessId)).ToArray()),
            prepared.SelectMany(item => item.Surfaces).Count(),
            result,
            timeline,
            layoutMode);
    }

    public static async Task<RendererProductCandidateRecoveryResult> RunRecoveryAsync(
        RendererProductCandidateRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RendererExecutablePath);
        ArgumentNullException.ThrowIfNull(request.RestartShellAsync);
        if (request.InitialVisibleDuration <= TimeSpan.Zero ||
            request.RecoveredVisibleDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Recovery observation durations must be positive.");
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

        await desktopHost.EnsureTopologyAsync(request.DisplayTopology, cancellationToken)
            .ConfigureAwait(false);
        await commandLoop.SendAsync(
                new DisplayTopologyChangedCommand(request.DisplayTopology),
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<DisplayId> selectedDisplays = ResolveDisplayIds(
            request.DisplayTopology,
            request.DisplaySelection ?? ProductCandidateDisplaySelection.All);
        SessionRetirementBatchResult finalRetirement = new([]);
        try
        {
            RendererProductCandidateIterationResult initial = await ApplySuccessfulIterationAsync(
                    commandLoop,
                    wallpaper.Id,
                    selectedDisplays,
                    iteration: 1,
                    cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(request.InitialVisibleDuration, cancellationToken)
                .ConfigureAwait(false);
            await request.RestartShellAsync(cancellationToken).ConfigureAwait(false);

            HostCommandResult accepted = await commandLoop.SendAsync(
                    new ExplorerRestartedCommand(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!accepted.Accepted || accepted.State is null)
            {
                throw new InvalidOperationException(
                    $"Host rejected desktop recovery: {accepted.ErrorCode}.");
            }
            DesktopRecoveryResult recovery = await commandLoop.WaitForDesktopRecoveryAsync(
                    accepted.State.Generation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (recovery.TerminalState != DesktopRecoveryTerminalState.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Desktop recovery ended as {recovery.TerminalState}: {recovery.FailureReason}.");
            }
            PreparedSession[] recovered = commandLoop.GetPreparedSessionsForDiagnostics()
                .OrderBy(item => item.SurfaceOrdinal)
                .ToArray();
            HashSet<SessionId> initialIds = initial.Sessions
                .Select(item => item.SessionId)
                .ToHashSet();
            HashSet<SessionId> recoveredIds = recovered
                .Select(item => item.Session.Id)
                .ToHashSet();
            bool expectedIdentities = request.ExpectedCompletionMode switch
            {
                DesktopRecoveryCompletionMode.Reattached => initialIds.SetEquals(recoveredIds),
                DesktopRecoveryCompletionMode.Rebuilt => !initialIds.Overlaps(recoveredIds),
                _ => false,
            };
            if (recovery.CompletionMode != request.ExpectedCompletionMode ||
                recovered.Length != selectedDisplays.Count || !expectedIdentities)
            {
                throw new InvalidOperationException(
                    $"Desktop recovery did not produce the expected " +
                    $"{request.ExpectedCompletionMode} Session identity boundary.");
            }
            ProductCandidateSessionResult[] recoveredResults = recovered.Select(item =>
                new ProductCandidateSessionResult(
                    item.Session.Id,
                    item.Session.RendererId,
                    Array.AsReadOnly(item.Session.Displays.ToArray()),
                    item.SurfaceOrdinal,
                    item.Surfaces.Count,
                    (item.Renderer as RendererProtocolSession)?.ProcessId)).ToArray();
            await Task.Delay(request.RecoveredVisibleDuration, cancellationToken)
                .ConfigureAwait(false);
            finalRetirement = await commandLoop.RetireActiveSessionsForDiagnosticsAsync(
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new RendererProductCandidateRecoveryResult(
                initial,
                recovery,
                Array.AsReadOnly(recoveredResults),
                finalRetirement);
        }
        finally
        {
            if (commandLoop.GetPreparedSessionsForDiagnostics().Count > 0)
            {
                _ = await commandLoop.RetireActiveSessionsForDiagnosticsAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    public static async Task<RendererProductCandidateTopologyResult> RunTopologyAsync(
        RendererProductCandidateTopologyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RendererExecutablePath);
        ArgumentNullException.ThrowIfNull(request.AwaitTopologyChangeAsync);
        if (request.InitialTopology.Displays.Count == 0)
        {
            throw new ArgumentException(
                "Topology diagnostics require an initially connected display.",
                nameof(request));
        }
        if (request.TopologyChanges is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Topology diagnostics support between one and eight changes.");
        }
        if (request.TransitionGuard is not null && request.TopologyChanges != 1)
        {
            throw new ArgumentException(
                "A transition guard probe supports exactly one display rotation.",
                nameof(request));
        }
        if (!Enum.IsDefined(request.LayoutMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Topology diagnostics require a known layout mode.");
        }
        if (request.ObservationDuration <= TimeSpan.Zero ||
            request.ObservationDuration > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Topology observation duration must be between 1 millisecond and 30 seconds.");
        }

        WallpaperDefinition wallpaper = CreateSyntheticWallpaper();
        InMemoryDiagnosticWallpaperRepository repository = new(
            new WallpaperLibraryEntry(
                wallpaper,
                AppContext.BaseDirectory,
                new CompatibilityReport(CompatibilityGrade.Native, [])));
        RendererProcessProvider provider = CreateRendererProvider(
            request.RendererExecutablePath,
            environmentTarget: null);
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
            new DefaultPlaybackPolicyEvaluator(),
            new DefaultLayoutPlanner());

        await desktopHost.EnsureTopologyAsync(request.InitialTopology, cancellationToken)
            .ConfigureAwait(false);
        await commandLoop.SendAsync(
                new DisplayTopologyChangedCommand(request.InitialTopology),
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<DisplayId> selectedDisplays = ResolveDisplayIds(
            request.InitialTopology,
            request.DisplaySelection ?? ProductCandidateDisplaySelection.All);
        List<RendererProductCandidateTopologyTransition> transitions = [];
        SessionRetirementBatchResult finalRetirement = new([]);
        DesktopTransitionGuardProbe? transitionGuard = null;
        DesktopTransitionGuardProbeResult? transitionGuardResult = null;
        try
        {
            RendererProductCandidateIterationResult initial = await ApplySuccessfulIterationAsync(
                    commandLoop,
                    wallpaper.Id,
                    selectedDisplays,
                    iteration: 1,
                    cancellationToken,
                    request.LayoutMode)
                .ConfigureAwait(false);
            if (request.TransitionGuard is { } guardRequest)
            {
                transitionGuard = await DesktopTransitionGuardProbe.ArmAsync(
                        desktopHost,
                        request.InitialTopology,
                        guardRequest.TargetDisplayId,
                        guardRequest.GuardColorReference,
                        cancellationToken)
                    .ConfigureAwait(false);
                guardRequest.Observer?.Invoke(transitionGuard.GetResult());
            }
            // TG0 must begin observing immediately after GUARD_ARMED. Waiting through the
            // ordinary pre-change stability interval would make the human trigger ambiguous.
            if (request.TransitionGuard is null)
            {
                await VerifyTopologyObservationAsync(
                        request,
                        request.InitialTopology,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            DisplayTopology current = request.InitialTopology;
            for (int index = 1; index <= request.TopologyChanges; index++)
            {
                DisplayTopology next = await request.AwaitTopologyChangeAsync(
                        index,
                        current,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (next.Revision <= current.Revision)
                {
                    throw new InvalidOperationException(
                        $"Topology transition {index} did not advance the revision.");
                }
                if (transitionGuard is not null)
                {
                    transitionGuard.Observe(next);
                    request.TransitionGuard?.Observer?.Invoke(transitionGuard.GetResult());
                }

                PreparedSession[] beforeSessions = commandLoop
                    .GetPreparedSessionsForDiagnostics()
                    .ToArray();
                HostCommandResult accepted = await commandLoop.SendAsync(
                        new DisplayTopologyChangedCommand(next),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!accepted.Accepted)
                {
                    throw new InvalidOperationException(
                        $"Topology transition {index} was rejected: {accepted.ErrorCode}.");
                }
                DisplayReconciliationResult reconciliation =
                    await commandLoop.WaitForDisplayReconciliationAsync(
                            next.Revision,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (reconciliation.TerminalState !=
                    DisplayReconciliationTerminalState.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Topology transition {index} ended as " +
                        $"{reconciliation.TerminalState}: {reconciliation.FailureReason}: " +
                        $"{reconciliation.Detail}");
                }

                PreparedSession[] active = commandLoop.GetPreparedSessionsForDiagnostics()
                    .OrderBy(item => item.SurfaceOrdinal)
                    .ToArray();
                HashSet<DisplayId> connected = next.Displays.Select(item => item.Id).ToHashSet();
                if (active.Any(item => item.Session.Displays.Any(id => !connected.Contains(id))))
                {
                    throw new InvalidOperationException(
                        $"Topology transition {index} kept a Session for a disconnected display.");
                }
                SessionId[] retiredIds = beforeSessions
                    .Where(previous => active.All(item => item.Session.Id != previous.Session.Id))
                    .Select(item => item.Session.Id)
                    .ToArray();
                SessionRetirementBatchResult retired = retiredIds.Length == 0
                    ? new SessionRetirementBatchResult([])
                    : await commandLoop.WaitForSessionRetirementsForDiagnosticsAsync(
                            retiredIds,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (!retired.OwnedResourcesCleaned)
                {
                    throw new InvalidOperationException(
                        $"Topology transition {index} did not clean all retired resources.");
                }

                ProductCandidateSessionResult[] sessionResults = active.Select(item =>
                    new ProductCandidateSessionResult(
                        item.Session.Id,
                        item.Session.RendererId,
                        Array.AsReadOnly(item.Session.Displays.ToArray()),
                        item.SurfaceOrdinal,
                        item.Surfaces.Count,
                        (item.Renderer as RendererProtocolSession)?.ProcessId)).ToArray();
                transitions.Add(new RendererProductCandidateTopologyTransition(
                    index,
                    current,
                    next,
                    reconciliation,
                    Array.AsReadOnly(sessionResults),
                    retired));
                current = next;
                await VerifyTopologyObservationAsync(request, current, cancellationToken)
                    .ConfigureAwait(false);
                if (transitionGuard is not null)
                {
                    await transitionGuard.DisposeAsync().ConfigureAwait(false);
                    transitionGuardResult = transitionGuard.GetResult();
                    request.TransitionGuard?.Observer?.Invoke(transitionGuardResult);
                    transitionGuard = null;
                }
            }

            finalRetirement = await commandLoop.RetireActiveSessionsForDiagnosticsAsync(
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new RendererProductCandidateTopologyResult(
                initial,
                Array.AsReadOnly(transitions.ToArray()),
                finalRetirement,
                transitionGuardResult);
        }
        finally
        {
            if (transitionGuard is not null)
            {
                await transitionGuard.DisposeAsync().ConfigureAwait(false);
            }
            if (commandLoop.GetPreparedSessionsForDiagnostics().Count > 0)
            {
                _ = await commandLoop.RetireActiveSessionsForDiagnosticsAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task VerifyTopologyObservationAsync(
        RendererProductCandidateTopologyRequest request,
        DisplayTopology expected,
        CancellationToken cancellationToken)
    {
        if (request.VerifyTopologyStableAsync is null)
        {
            await Task.Delay(request.ObservationDuration, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        bool stable = await request.VerifyTopologyStableAsync(
                expected,
                request.ObservationDuration,
                cancellationToken)
            .ConfigureAwait(false);
        if (!stable)
        {
            throw new InvalidOperationException(
                $"Display topology changed again during the observation interval after " +
                $"revision {expected.Revision}; the diagnostic sample is inconclusive.");
        }
    }

    private sealed class DiagnosticPreparedCommitHoldCoordinator : ISessionCoordinator
    {
        private readonly ISessionCoordinator inner;
        private readonly TimeSpan hold;
        private int prepareCalls;

        public DiagnosticPreparedCommitHoldCoordinator(ISessionCoordinator inner, TimeSpan hold)
        {
            this.inner = inner;
            this.hold = hold;
            if (hold <= TimeSpan.Zero || hold > TimeSpan.FromSeconds(30))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(hold),
                    "Diagnostic prepared commit hold must be between zero and thirty seconds.");
            }
        }

        public async Task<PreparedApply> PrepareApplyAsync(
            IReadOnlyList<WallpaperAssignment> assignments,
            DisplayTopology topology,
            long generation,
            ApplyGenerationOperation operation,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref prepareCalls);
            PreparedApply prepared = await inner.PrepareApplyAsync(
                    assignments,
                    topology,
                    generation,
                    operation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (call == 1)
            {
                return prepared;
            }

            try
            {
                await Task.Delay(hold, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Return the fully prepared result so the real Host command path observes its
                // stale revision and performs the same retirement used for a naturally late
                // internal completion. The diagnostic wrapper never commits or owns cleanup.
            }
            return prepared;
        }

        public int GetPlannedSurfaceCount(
            IReadOnlyList<WallpaperAssignment> assignments,
            DisplayTopology topology) => inner.GetPlannedSurfaceCount(assignments, topology);

        public TimeSpan GetApplyDeadline(int surfaceCount) => inner.GetApplyDeadline(surfaceCount);

        public Task<IReadOnlyList<ActiveSurfaceResizeResult>> ResizeActiveSurfacesAsync(
            IReadOnlyList<ActiveSurfaceResizeRequest> requests,
            DisplayTopology topology,
            long generation,
            CancellationToken cancellationToken) => inner.ResizeActiveSurfacesAsync(
                requests,
                topology,
                generation,
                cancellationToken);

        public Task<SessionRetirementBatchResult> RetireAsync(
            IReadOnlyList<PreparedSession> sessions,
            CancellationToken cancellationToken) => inner.RetireAsync(sessions, cancellationToken);

        public Task ReplaceSurfacesAsync(
            PreparedApply prepared,
            IReadOnlyList<PreparedSession> replacedSessions,
            CancellationToken cancellationToken) =>
            inner.ReplaceSurfacesAsync(prepared, replacedSessions, cancellationToken);

        public Task<PreparedRecovery> PrepareRecoveryAsync(
            IReadOnlyList<PreparedSession> activeSessions,
            DisplayTopology topology,
            long hostGeneration,
            Action<DesktopRecoveryMilestone>? observer,
            CancellationToken cancellationToken) => inner.PrepareRecoveryAsync(
                activeSessions,
                topology,
                hostGeneration,
                observer,
                cancellationToken);

        public Task ReplaceRecoveredSurfacesAsync(
            PreparedRecovery recovery,
            CancellationToken cancellationToken) =>
            inner.ReplaceRecoveredSurfacesAsync(recovery, cancellationToken);

        public Task DestroySurfacesAsync(
            IEnumerable<PreparedSurface> surfaces,
            CancellationToken cancellationToken) =>
            inner.DestroySurfacesAsync(surfaces, cancellationToken);

        public Task AbandonSurfacesAsync(
            IEnumerable<PreparedSurface> surfaces,
            CancellationToken cancellationToken) =>
            inner.AbandonSurfacesAsync(surfaces, cancellationToken);

        public Task AbandonRecoveryAsync(
            PreparedRecovery recovery,
            CancellationToken cancellationToken) =>
            inner.AbandonRecoveryAsync(recovery, cancellationToken);
    }

    public static async Task<RendererProductCandidateTopologyChurnResult> RunTopologyChurnAsync(
        RendererProductCandidateTopologyChurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RendererExecutablePath);
        ArgumentNullException.ThrowIfNull(request.PublishedTopologies);
        if (request.InitialTopology.Displays.Count == 0)
        {
            throw new ArgumentException(
                "Topology churn diagnostics require an initially connected display.",
                nameof(request));
        }
        if (request.MaximumPublishedChanges is < 2 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Topology churn diagnostics support between two and sixteen published changes.");
        }
        if (request.FinalObservationDuration <= TimeSpan.Zero ||
            request.FinalObservationDuration > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Final topology observation duration must be between 1 millisecond and 30 seconds.");
        }

        WallpaperDefinition wallpaper = CreateSyntheticWallpaper();
        InMemoryDiagnosticWallpaperRepository repository = new(
            new WallpaperLibraryEntry(
                wallpaper,
                AppContext.BaseDirectory,
                new CompatibilityReport(CompatibilityGrade.Native, [])));
        RendererProcessProvider provider = CreateRendererProvider(
            request.RendererExecutablePath,
            environmentTarget: null);
        await using WindowsDesktopHost desktopHost =
            WindowsDesktopHost.CreateExperimentalDiagnostics(request.ShellSnapshot);
        SessionCoordinator coordinator = new(
            repository,
            new DefaultLayoutPlanner(),
            new RendererProviderSelector([provider]),
            desktopHost,
            new RuntimeEnvironment("win-x64", true, new HashSet<string>()),
            request.RendererStageTimeout ?? TimeSpan.FromSeconds(10));
        ISessionCoordinator commandCoordinator = request.PreparedCommitHold is TimeSpan hold
            ? new DiagnosticPreparedCommitHoldCoordinator(coordinator, hold)
            : coordinator;
        await using HostCommandLoop commandLoop = new(
            commandCoordinator,
            new DefaultPlaybackPolicyEvaluator(),
            new DefaultLayoutPlanner());

        await desktopHost.EnsureTopologyAsync(request.InitialTopology, cancellationToken)
            .ConfigureAwait(false);
        await commandLoop.SendAsync(
                new DisplayTopologyChangedCommand(request.InitialTopology),
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<DisplayId> selectedDisplays = ResolveDisplayIds(
            request.InitialTopology,
            request.DisplaySelection ?? ProductCandidateDisplaySelection.All);
        SessionRetirementBatchResult finalRetirement = new([]);
        try
        {
            RendererProductCandidateIterationResult initial = await ApplySuccessfulIterationAsync(
                    commandLoop,
                    wallpaper.Id,
                    selectedDisplays,
                    iteration: 1,
                    cancellationToken)
                .ConfigureAwait(false);

            List<(int Index, DisplayTopology Previous, DisplayTopology Current,
                Task<DisplayReconciliationResult> Completion)> pending = [];
            DisplayTopology current = request.InitialTopology;
            int index = 0;
            await foreach (DisplayTopology published in request.PublishedTopologies
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (index >= request.MaximumPublishedChanges)
                {
                    break;
                }
                if (published.Revision <= current.Revision)
                {
                    throw new InvalidOperationException(
                        $"Published topology revision {published.Revision} did not advance " +
                        $"revision {current.Revision}.");
                }

                HostCommandResult accepted = await commandLoop.SendAsync(
                        new DisplayTopologyChangedCommand(published),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!accepted.Accepted)
                {
                    throw new InvalidOperationException(
                        $"Published topology revision {published.Revision} was rejected: " +
                        $"{accepted.ErrorCode}.");
                }

                index++;
                pending.Add((
                    index,
                    current,
                    published,
                    commandLoop.WaitForDisplayReconciliationAsync(
                        published.Revision,
                        cancellationToken)));
                current = published;
            }
            if (pending.Count != request.MaximumPublishedChanges)
            {
                throw new InvalidOperationException(
                    $"Topology churn stream ended after {pending.Count} published changes; " +
                    $"{request.MaximumPublishedChanges} were required.");
            }

            DisplayReconciliationResult[] completed = await Task.WhenAll(
                    pending.Select(item => item.Completion))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            RendererProductCandidateTopologyChurnRevision[] revisions = pending
                .Zip(completed, (item, result) =>
                    new RendererProductCandidateTopologyChurnRevision(
                        item.Index,
                        item.Previous,
                        item.Current,
                        result))
                .ToArray();

            await Task.Delay(request.FinalObservationDuration, cancellationToken)
                .ConfigureAwait(false);
            PreparedSession[] active = commandLoop.GetPreparedSessionsForDiagnostics()
                .OrderBy(item => item.SurfaceOrdinal)
                .ToArray();
            HashSet<DisplayId> connected = current.Displays.Select(item => item.Id).ToHashSet();
            if (active.Any(item => item.Session.Displays.Any(id => !connected.Contains(id))))
            {
                throw new InvalidOperationException(
                    "Final topology churn state retained a Session for a disconnected display.");
            }
            ProductCandidateSessionResult[] activeResults = active.Select(item =>
                new ProductCandidateSessionResult(
                    item.Session.Id,
                    item.Session.RendererId,
                    Array.AsReadOnly(item.Session.Displays.ToArray()),
                    item.SurfaceOrdinal,
                    item.Surfaces.Count,
                    (item.Renderer as RendererProtocolSession)?.ProcessId)).ToArray();

            finalRetirement = await commandLoop.RetireActiveSessionsForDiagnosticsAsync(
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new RendererProductCandidateTopologyChurnResult(
                initial,
                Array.AsReadOnly(revisions),
                Array.AsReadOnly(activeResults),
                finalRetirement);
        }
        finally
        {
            if (commandLoop.GetPreparedSessionsForDiagnostics().Count > 0)
            {
                _ = await commandLoop.RetireActiveSessionsForDiagnosticsAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private static ApplyWallpaperCommand CreateApplyCommand(
        WallpaperId wallpaperId,
        IReadOnlyList<DisplayId> displayIds,
        LayoutMode layoutMode = LayoutMode.PerDisplay) => new(
        wallpaperId,
        displayIds,
        layoutMode,
        FitMode.Cover,
        null);

    private static async Task<HostStateSnapshot> GetStateAsync(
        HostCommandLoop commandLoop,
        CancellationToken cancellationToken) =>
        (await commandLoop.SendAsync(new GetHostStateCommand(), cancellationToken)
            .ConfigureAwait(false)).State ??
        throw new InvalidOperationException("Host did not return a state snapshot.");

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
