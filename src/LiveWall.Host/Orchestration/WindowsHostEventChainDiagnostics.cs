using LiveWall.Application.Configuration;
using LiveWall.Application.Importing;
using LiveWall.Application.Library;
using LiveWall.Application.Sessions;
using LiveWall.Application.Policies;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.Bootstrap;
using LiveWall.Host.CommandLoop;

namespace LiveWall.Host.Orchestration;

public sealed record WindowsHostTaskbarNoopRequest(
    string RendererExecutablePath,
    DisplayTopology DisplayTopology,
    ProductCandidateDisplaySelection DisplaySelection,
    int SignalCount,
    TimeSpan ObservationDuration);

public sealed record WindowsHostTaskbarNoopResult(
    long InitialGeneration,
    long FinalGeneration,
    int RecoveryOperationsAdded,
    long RawTaskbarSignals,
    long ValidatedShellChanges,
    long ForwardedRecoveryCommands,
    long BatchReplaceCount,
    int RetirementCount,
    bool SessionIdentityPreserved,
    bool OwnedResourcesCleaned,
    IReadOnlyList<ProductCandidateSessionResult> Sessions);

public sealed record WindowsHostExplorerEventRecoveryRequest(
    string RendererExecutablePath,
    DisplayTopology DisplayTopology,
    ProductCandidateDisplaySelection DisplaySelection,
    TimeSpan RecoveredVisibleDuration,
    Func<CancellationToken, Task> RestartShellAsync);

public sealed record WindowsHostExplorerEventRecoveryResult(
    long RawTaskbarSignals,
    long ValidatedShellChanges,
    long ForwardedRecoveryCommands,
    int RecoveryOperationsAdded,
    int DuplicateRecoveryCount,
    long BatchReplaceCount,
    DesktopRecoveryResult Recovery,
    IReadOnlyList<ProductCandidateSessionResult> InitialSessions,
    IReadOnlyList<ProductCandidateSessionResult> RecoveredSessions,
    bool SessionIdentityPreserved,
    bool OwnedResourcesCleaned);

public enum WindowsHostSystemTransitionKind
{
    SessionLock,
    SleepResume,
}

public sealed record WindowsHostSystemTransitionRequest(
    string RendererExecutablePath,
    DisplayTopology DisplayTopology,
    ProductCandidateDisplaySelection DisplaySelection,
    WindowsHostSystemTransitionKind TransitionKind,
    TimeSpan TransitionTimeout,
    TimeSpan RecoveredVisibleDuration,
    Action? Armed = null);

public sealed record WindowsHostSystemTransitionResult(
    WindowsHostSystemTransitionKind TransitionKind,
    bool SuspendedObserved,
    bool ResumedObserved,
    long InitialGeneration,
    long FinalGeneration,
    long InitialReplaceCount,
    long FinalReplaceCount,
    int InitialRetirementCount,
    int FinalRetirementCount,
    bool SessionIdentityPreserved,
    IReadOnlyList<SystemStateChange> SystemStateTimeline,
    bool OwnedResourcesCleaned);

public static class WindowsHostEventChainDiagnostics
{
    private static readonly TimeSpan RecoveryDiscoveryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FallbackStabilityWindow = TimeSpan.FromMilliseconds(2300);

    public static async Task<WindowsHostTaskbarNoopResult> RunTaskbarNoopAsync(
        WindowsHostTaskbarNoopRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.SignalCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            request.ObservationDuration,
            FallbackStabilityWindow);
        DiagnosticComposition diagnostic = CreateComposition(
            request.RendererExecutablePath,
            request.DisplayTopology,
            request.DisplaySelection);
        WindowsHostComposition composition = diagnostic.Composition;
        try
        {
            await composition.Runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            EnsureInitialApplySucceeded(composition.Runtime);
            HostStateSnapshot initial = await GetStateAsync(
                    composition.CommandLoop,
                    cancellationToken)
                .ConfigureAwait(false);
            ProductCandidateSessionResult[] initialSessions = SnapshotSessions(
                composition.CommandLoop);
            int initialRecoveryCount = composition.CommandLoop.RecoveryDiagnosticsCountForTests;
            long initialReplaceCount = composition.DesktopHost.ReplacementCountForDiagnostics;
            int initialRetirementCount = CountRetirements(composition.CommandLoop);

            await composition.DesktopHost.PostTaskbarCreatedForDiagnosticsAsync(
                    request.SignalCount,
                    cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(request.ObservationDuration, cancellationToken).ConfigureAwait(false);

            HostStateSnapshot final = await GetStateAsync(
                    composition.CommandLoop,
                    cancellationToken)
                .ConfigureAwait(false);
            ProductCandidateSessionResult[] finalSessions = SnapshotSessions(
                composition.CommandLoop);
            int recoveryOperationsAdded =
                composition.CommandLoop.RecoveryDiagnosticsCountForTests - initialRecoveryCount;
            long replacementCount =
                composition.DesktopHost.ReplacementCountForDiagnostics - initialReplaceCount;
            int retirementCount =
                CountRetirements(composition.CommandLoop) - initialRetirementCount;
            bool identityPreserved = SameSessions(initialSessions, finalSessions);

            await composition.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return new WindowsHostTaskbarNoopResult(
                initial.Generation,
                final.Generation,
                recoveryOperationsAdded,
                composition.EventBridge.RawTaskbarSignalCount,
                composition.EventBridge.ValidatedShellChangeCount,
                composition.EventBridge.ForwardedRecoveryCount,
                replacementCount,
                retirementCount,
                identityPreserved,
                composition.DesktopHost.OwnedSurfaceCountForDiagnostics == 0,
                finalSessions);
        }
        finally
        {
            await composition.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static async Task<WindowsHostExplorerEventRecoveryResult> RunExplorerRecoveryAsync(
        WindowsHostExplorerEventRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RestartShellAsync);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            request.RecoveredVisibleDuration,
            TimeSpan.Zero);
        DiagnosticComposition diagnostic = CreateComposition(
            request.RendererExecutablePath,
            request.DisplayTopology,
            request.DisplaySelection);
        WindowsHostComposition composition = diagnostic.Composition;
        try
        {
            await composition.Runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            EnsureInitialApplySucceeded(composition.Runtime);
            ProductCandidateSessionResult[] initialSessions = SnapshotSessions(
                composition.CommandLoop);
            long[] initialRecoveryIds = composition.CommandLoop
                .GetRecoveryOperationIdsForDiagnostics()
                .ToArray();
            long initialReplaceCount = composition.DesktopHost.ReplacementCountForDiagnostics;

            await request.RestartShellAsync(cancellationToken).ConfigureAwait(false);
            long recoveryId = await WaitForNewRecoveryAsync(
                    composition.CommandLoop,
                    initialRecoveryIds,
                    cancellationToken)
                .ConfigureAwait(false);
            DesktopRecoveryResult recovery = await composition.CommandLoop
                .WaitForDesktopRecoveryAsync(recoveryId, cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(request.RecoveredVisibleDuration, cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(FallbackStabilityWindow, cancellationToken).ConfigureAwait(false);

            ProductCandidateSessionResult[] recoveredSessions = SnapshotSessions(
                composition.CommandLoop);
            int operationsAdded = composition.CommandLoop.RecoveryDiagnosticsCountForTests -
                initialRecoveryIds.Length;
            int duplicates = Math.Max(0, operationsAdded - 1);
            long replacements = composition.DesktopHost.ReplacementCountForDiagnostics -
                initialReplaceCount;
            bool identityPreserved = SameSessions(initialSessions, recoveredSessions);

            await composition.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return new WindowsHostExplorerEventRecoveryResult(
                composition.EventBridge.RawTaskbarSignalCount,
                composition.EventBridge.ValidatedShellChangeCount,
                composition.EventBridge.ForwardedRecoveryCount,
                operationsAdded,
                duplicates,
                replacements,
                recovery,
                initialSessions,
                recoveredSessions,
                identityPreserved,
                composition.DesktopHost.OwnedSurfaceCountForDiagnostics == 0);
        }
        finally
        {
            await composition.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static async Task<WindowsHostSystemTransitionResult> RunSystemTransitionAsync(
        WindowsHostSystemTransitionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            request.TransitionTimeout,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            request.RecoveredVisibleDuration,
            TimeSpan.Zero);
        DiagnosticComposition diagnostic = CreateComposition(
            request.RendererExecutablePath,
            request.DisplayTopology,
            request.DisplaySelection);
        WindowsHostComposition composition = diagnostic.Composition;
        try
        {
            await composition.Runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            EnsureInitialApplySucceeded(composition.Runtime);
            HostStateSnapshot initial = await GetStateAsync(
                    composition.CommandLoop,
                    cancellationToken)
                .ConfigureAwait(false);
            ProductCandidateSessionResult[] initialSessions = SnapshotSessions(
                composition.CommandLoop);
            long initialReplaceCount = composition.DesktopHost.ReplacementCountForDiagnostics;
            int initialRetirementCount = CountRetirements(composition.CommandLoop);
            (SystemStateChangeReason suspendedReason, SystemStateChangeReason resumedReason) =
                request.TransitionKind switch
                {
                    WindowsHostSystemTransitionKind.SessionLock =>
                        (SystemStateChangeReason.SessionLocked,
                            SystemStateChangeReason.SessionUnlocked),
                    WindowsHostSystemTransitionKind.SleepResume =>
                        (SystemStateChangeReason.SystemSuspending,
                            SystemStateChangeReason.SystemResumed),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(request),
                        request.TransitionKind,
                        "Unknown system transition diagnostic kind."),
                };
            request.Armed?.Invoke();

            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.TransitionTimeout);
            await WaitForSystemStateReasonAsync(
                    composition.EventBridge,
                    suspendedReason,
                    timeout.Token)
                .ConfigureAwait(false);
            await WaitForSystemStateReasonAsync(
                    composition.EventBridge,
                    resumedReason,
                    timeout.Token)
                .ConfigureAwait(false);
            HostStateSnapshot resumed = await WaitForPlayingSessionsAsync(
                    composition.CommandLoop,
                    initialSessions.Length,
                    timeout.Token)
                .ConfigureAwait(false);
            await Task.Delay(request.RecoveredVisibleDuration, cancellationToken)
                .ConfigureAwait(false);

            ProductCandidateSessionResult[] finalSessions = SnapshotSessions(
                composition.CommandLoop);
            IReadOnlyList<SystemStateChange> timeline = composition.EventBridge
                .GetSystemStateHistoryForDiagnostics();
            long finalReplaceCount = composition.DesktopHost.ReplacementCountForDiagnostics;
            int finalRetirementCount = CountRetirements(composition.CommandLoop);
            await composition.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return new WindowsHostSystemTransitionResult(
                request.TransitionKind,
                timeline.Any(item => item.Reason == suspendedReason),
                timeline.Any(item => item.Reason == resumedReason),
                initial.Generation,
                resumed.Generation,
                initialReplaceCount,
                finalReplaceCount,
                initialRetirementCount,
                finalRetirementCount,
                SameSessions(initialSessions, finalSessions),
                timeline,
                composition.DesktopHost.OwnedSurfaceCountForDiagnostics == 0);
        }
        finally
        {
            await composition.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static DiagnosticComposition CreateComposition(
        string rendererExecutablePath,
        DisplayTopology topology,
        ProductCandidateDisplaySelection selection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rendererExecutablePath);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(selection);
        WallpaperDefinition wallpaper = CreateSyntheticWallpaper();
        WallpaperLibraryEntry entry = new(
            wallpaper,
            AppContext.BaseDirectory,
            new CompatibilityReport(CompatibilityGrade.Native, []));
        DiagnosticWallpaperRepository repository = new(entry);
        RendererProcessProvider provider = CreateRendererProvider(rendererExecutablePath);
        DisplayId[] displayIds = RendererProductCandidateDiagnostics.ResolveDisplayIds(
                topology,
                selection)
            .ToArray();
        WallpaperAssignment[] assignments = displayIds
            .Select(displayId => new WallpaperAssignment(
                displayId,
                wallpaper.Id,
                LayoutMode.PerDisplay,
                FitMode.Cover,
                null))
            .ToArray();
        InMemoryHostConfigurationStore configurationStore = new(
            new HostConfiguration(UserPlaybackPolicy.Default, assignments));
        WindowsHostComposition composition = WindowsHostCompositionRoot.CreateComposition(
            AppContext.BaseDirectory,
            new WindowsHostCompositionOptions(
                repository,
                [provider],
                DesktopAttachmentPolicy.AllowUnvalidatedRaisedDiagnostics,
                configurationStore,
                PersistConfiguration: false));
        return new DiagnosticComposition(composition, provider);
    }

    private static RendererProcessProvider CreateRendererProvider(string executablePath) =>
        new(new RendererProcessProviderOptions(
            new RendererDescriptor(
                new RendererId("renderer-child-dcomp-probe"),
                "Renderer-child DirectComposition test Renderer",
                new HashSet<WallpaperKind> { WallpaperKind.Scene },
                new HashSet<string>(["HwndChild", "DirectComposition"])),
            executablePath,
            "diagnostics",
            ["HwndChild", "DirectComposition"],
            "en-US",
            TimeSpan.FromSeconds(10)));

    private static WallpaperDefinition CreateSyntheticWallpaper() => new(
        new WallpaperId("diagnostic.synthetic.color"),
        "1.0.0",
        WallpaperKind.Scene,
        "synthetic-color",
        new WallpaperMetadata(
            "LiveWall event-chain diagnostic color",
            null,
            null,
            null,
            new HashSet<string>()),
        new WallpaperCapabilities(false, false, false, false, 30),
        WallpaperOrigin.Native);

    private static void EnsureInitialApplySucceeded(WindowsHostRuntime runtime)
    {
        ApplyGenerationResult? result = runtime.BootstrapResult?.RestoreGenerationResult;
        if (result is null || result.State != ApplyGenerationTerminalState.Succeeded)
        {
            throw new InvalidOperationException(
                $"Diagnostic Host startup Apply did not succeed: {result?.State} " +
                $"{result?.FailureReason} {result?.Detail}");
        }
    }

    private static async Task<HostStateSnapshot> GetStateAsync(
        HostCommandLoop commandLoop,
        CancellationToken cancellationToken) =>
        (await commandLoop.SendAsync(new GetHostStateCommand(), cancellationToken)
            .ConfigureAwait(false)).State ??
        throw new InvalidOperationException("Host did not return a state snapshot.");

    private static ProductCandidateSessionResult[] SnapshotSessions(
        HostCommandLoop commandLoop) =>
        commandLoop.GetPreparedSessionsForDiagnostics()
            .OrderBy(session => session.SurfaceOrdinal)
            .Select(session => new ProductCandidateSessionResult(
                session.Session.Id,
                session.Session.RendererId,
                session.Session.Displays,
                session.SurfaceOrdinal,
                session.Surfaces.Count,
                (session.Renderer as RendererProtocolSession)?.ProcessId))
            .ToArray();

    private static int CountRetirements(HostCommandLoop commandLoop) =>
        commandLoop.GetRetirementHistoryForDiagnostics()
            .SelectMany(batch => batch.Sessions)
            .Count();

    private static bool SameSessions(
        IReadOnlyList<ProductCandidateSessionResult> first,
        IReadOnlyList<ProductCandidateSessionResult> second) =>
        first.Select(item => (item.SessionId, item.RendererProcessId))
            .OrderBy(item => item.SessionId.Value, StringComparer.Ordinal)
            .SequenceEqual(second.Select(item => (item.SessionId, item.RendererProcessId))
                .OrderBy(item => item.SessionId.Value, StringComparer.Ordinal));

    private static async Task<long> WaitForNewRecoveryAsync(
        HostCommandLoop commandLoop,
        IReadOnlyCollection<long> existingIds,
        CancellationToken cancellationToken)
    {
        HashSet<long> existing = existingIds.ToHashSet();
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RecoveryDiscoveryTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            long? added = commandLoop.GetRecoveryOperationIdsForDiagnostics()
                .Where(id => !existing.Contains(id))
                .Cast<long?>()
                .FirstOrDefault();
            if (added is not null)
            {
                return added.Value;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token).ConfigureAwait(false);
        }
    }

    private static async Task WaitForSystemStateReasonAsync(
        WindowsDesktopEventBridge eventBridge,
        SystemStateChangeReason reason,
        CancellationToken cancellationToken)
    {
        while (!eventBridge.GetSystemStateHistoryForDiagnostics()
                   .Any(item => item.Reason == reason))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<HostStateSnapshot> WaitForPlayingSessionsAsync(
        HostCommandLoop commandLoop,
        int expectedSessionCount,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HostStateSnapshot state = await GetStateAsync(commandLoop, cancellationToken)
                .ConfigureAwait(false);
            if (state.Sessions.Count == expectedSessionCount &&
                state.Sessions.All(item => item.Session.State == PlaybackState.Playing))
            {
                return state;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed record DiagnosticComposition(
        WindowsHostComposition Composition,
        RendererProcessProvider RendererProvider);

    private sealed class DiagnosticWallpaperRepository(WallpaperLibraryEntry entry)
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

    private sealed class InMemoryHostConfigurationStore(HostConfiguration configuration)
        : IHostConfigurationStore
    {
        public Task<HostConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostConfigurationLoadResult(
                configuration,
                ConfigurationFileStatus.Loaded,
                ConfigurationFileStatus.Loaded));

        public Task SavePlaybackPolicyAsync(
            UserPlaybackPolicy policy,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveAssignmentsAsync(
            IReadOnlyList<WallpaperAssignment> assignments,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
