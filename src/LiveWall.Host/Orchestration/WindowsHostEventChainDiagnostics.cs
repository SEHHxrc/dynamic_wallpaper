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
    SessionReconnect,
}

public sealed record SessionReconnectEvidence(long ValidationRevision, string TerminalState,
    string? Path, string FailureReason, TimeSpan Elapsed);

public sealed record SessionNotificationEvidence(long Sequence, string Kind,
    bool CurrentSessionMatches, long SystemRevisionBeforeEvent, bool HostStopping, TimeSpan Elapsed);

public sealed class SystemTransitionDiagnosticException(
    string phase, Exception innerException, SessionReconnectEvidence? sessionReconnect,
    IReadOnlyList<SessionNotificationEvidence> notifications)
    : Exception($"System transition failed during {phase}: {innerException.Message}", innerException)
{
    public string Phase { get; } = phase;
    public SessionReconnectEvidence? SessionReconnect { get; } = sessionReconnect;
    public IReadOnlyList<SessionNotificationEvidence> Notifications { get; } = notifications;
}

public sealed record WindowsHostSystemTransitionRequest(
    string RendererExecutablePath,
    DisplayTopology DisplayTopology,
    ProductCandidateDisplaySelection DisplaySelection,
    WindowsHostSystemTransitionKind TransitionKind,
    TimeSpan TransitionTimeout,
    TimeSpan RecoveredVisibleDuration,
    Action? Armed = null,
    bool PauseBeforeTransition = false);

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
    bool OwnedResourcesCleaned,
    IReadOnlyList<RendererPlaybackEvidence> PlaybackEvidence,
    IReadOnlyList<RendererPlaybackEvidence> PolicyEvidence,
    IReadOnlyList<SessionRetirementResult> Retirement,
    bool PlaybackFeedbackValidated,
    bool UserPausePreserved,
    IReadOnlyList<DesktopRecoveryResult> Recovery,
    DisplayReconciliationResult? Reconciliation,
    SessionReconnectEvidence? SessionReconnect = null,
    IReadOnlyList<SessionNotificationEvidence>? SessionNotifications = null);

public static partial class WindowsHostEventChainDiagnostics
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
        string phase = "Startup";
        SessionReconnectEvidence? sessionReconnect = null;
        long armedNotificationSequence = 0;
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
                    WindowsHostSystemTransitionKind.SessionReconnect =>
                        (SystemStateChangeReason.SessionDisconnected,
                            SystemStateChangeReason.SessionReconnectRequested),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(request),
                        request.TransitionKind,
                        "Unknown system transition diagnostic kind."),
                };
            long[] initialRecoveryIds = composition.CommandLoop.GetRecoveryOperationIdsForDiagnostics().ToArray();
            RendererProtocolSession[] initialRenderers = composition.CommandLoop
                .GetPreparedSessionsForDiagnostics().Select(item => item.Renderer)
                .OfType<RendererProtocolSession>().ToArray();
            if (request.PauseBeforeTransition)
            {
                await composition.CommandLoop.SendAsync(
                    new ChangePlaybackIntentCommand(UserPlaybackIntent.Pause), cancellationToken)
                    .ConfigureAwait(false);
                using CancellationTokenSource pauseBudget =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pauseBudget.CancelAfter(TimeSpan.FromSeconds(10));
                await WaitForPlaybackFeedbackAsync(initialRenderers, PlaybackState.Paused,
                    pauseBudget.Token).ConfigureAwait(false);
            }
            long armedRevision = composition.EventBridge.GetSystemStateHistoryForDiagnostics()
                .Select(item => item.Revision).DefaultIfEmpty(0).Max();
            armedNotificationSequence = composition.EventBridge.GetSessionEventHistoryForDiagnostics()
                .Select(item => item.Sequence).DefaultIfEmpty(0).Max();
            request.Armed?.Invoke();

            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.TransitionTimeout);
            phase = "AwaitingSuspendEvent";
            SystemStateChange disconnected = await WaitForSystemStateReasonAsync(
                    composition.EventBridge,
                    suspendedReason,
                    timeout.Token, armedRevision)
                .ConfigureAwait(false);
            phase = "AwaitingResumeEvent";
            SystemStateChange reconnect = await WaitForSystemStateReasonAsync(
                    composition.EventBridge,
                    resumedReason,
                    timeout.Token, disconnected.Revision)
                .ConfigureAwait(false);
            // Human-operation time does not consume the bounded post-event verification budget.
            using CancellationTokenSource feedbackBudget =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            feedbackBudget.CancelAfter(
                ApplyDeadlineBudget.Default.Calculate(Math.Max(1, initialSessions.Length)) * 4 +
                TimeSpan.FromSeconds(10));
            phase = "EnvironmentValidation";
            if (request.TransitionKind == WindowsHostSystemTransitionKind.SessionReconnect)
            {
                // Flush the event bridge's preceding command before looking up its operation.
                await GetStateAsync(composition.CommandLoop, feedbackBudget.Token).ConfigureAwait(false);
                SessionEnvironmentResult terminal = await composition.CommandLoop
                    .WaitForSessionEnvironmentAsync(reconnect.Revision, feedbackBudget.Token)
                    .ConfigureAwait(false);
                sessionReconnect = new(terminal.ValidationRevision, terminal.State.ToString(),
                    terminal.Path?.ToString(), terminal.Reason.ToString(), terminal.Elapsed);
                if (terminal.State != SessionEnvironmentTerminalState.Succeeded)
                    throw new InvalidOperationException($"Session validation {terminal.ValidationRevision}: " +
                        $"{terminal.State}/{terminal.Reason}.");
            }
            // A resumed system snapshot alone is not proof that an environment
            // transaction finished. Wait on its explicit terminal before inspecting playback.
            HostStateSnapshot environment = await GetStateAsync(composition.CommandLoop, feedbackBudget.Token)
                .ConfigureAwait(false);
            List<DesktopRecoveryResult> recovery = [];
            foreach (long id in composition.CommandLoop.GetRecoveryOperationIdsForDiagnostics()
                         .Except(initialRecoveryIds))
            {
                recovery.Add(await composition.CommandLoop.WaitForDesktopRecoveryAsync(id, feedbackBudget.Token)
                    .ConfigureAwait(false));
            }
            DisplayReconciliationResult? reconciliation = null;
            if (environment.DisplayTopology is not null &&
                environment.DisplayTopology.Revision != initial.DisplayTopology?.Revision)
            {
                reconciliation = await composition.CommandLoop.WaitForDisplayReconciliationAsync(
                    environment.DisplayTopology.Revision, feedbackBudget.Token).ConfigureAwait(false);
            }
            PlaybackState expectedState = request.PauseBeforeTransition
                ? PlaybackState.Paused : PlaybackState.Playing;
            phase = "PlaybackFeedback";
            HostStateSnapshot resumed = await WaitForPlayingSessionsAsync(
                    composition.CommandLoop,
                    initialSessions.Length,
                    feedbackBudget.Token,
                    expectedState)
                .ConfigureAwait(false);
            RendererProtocolSession[] finalRenderers = composition.CommandLoop
                .GetPreparedSessionsForDiagnostics().Select(item => item.Renderer)
                .OfType<RendererProtocolSession>().ToArray();
            await WaitForPlaybackFeedbackAsync(finalRenderers, expectedState,
                feedbackBudget.Token).ConfigureAwait(false);
            await Task.Delay(request.RecoveredVisibleDuration, cancellationToken)
                .ConfigureAwait(false);

            ProductCandidateSessionResult[] finalSessions = SnapshotSessions(
                composition.CommandLoop);
            IReadOnlyList<SystemStateChange> timeline = composition.EventBridge
                .GetSystemStateHistoryForDiagnostics();
            long finalReplaceCount = composition.DesktopHost.ReplacementCountForDiagnostics;
            int finalRetirementCount = CountRetirements(composition.CommandLoop);
            phase = "Retirement";
            await composition.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            RendererPlaybackEvidence[] playback = initialRenderers.Union(finalRenderers)
                .SelectMany(item => item.GetPlaybackEvidenceForDiagnostics()).ToArray();
            bool feedbackValidated = initialRenderers.All(renderer =>
                    HasCompletedPlaybackExchange(renderer.GetPlaybackEvidenceForDiagnostics(),
                        PlaybackState.Suspended)) &&
                finalRenderers.All(renderer => HasCompletedPlaybackExchange(
                    renderer.GetPlaybackEvidenceForDiagnostics(), expectedState)) &&
                initialRenderers.Length == initialSessions.Length &&
                finalRenderers.Length == finalSessions.Length;
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
                composition.DesktopHost.OwnedSurfaceCountForDiagnostics == 0,
                playback,
                composition.CommandLoop.GetPlaybackPolicyEvidenceForDiagnostics(),
                composition.CommandLoop.GetRetirementHistoryForDiagnostics()
                    .SelectMany(batch => batch.Sessions).ToArray(),
                feedbackValidated,
                !request.PauseBeforeTransition || resumed.Sessions.All(item =>
                    item.UserIntent == UserPlaybackIntent.Pause &&
                    item.Session.State == PlaybackState.Paused),
                recovery.ToArray(),
                reconciliation,
                sessionReconnect,
                composition.EventBridge.GetSessionEventHistoryForDiagnostics()
                    .Where(item => item.Sequence > armedNotificationSequence)
                    .Select(item => new SessionNotificationEvidence(item.Sequence, item.Kind.ToString(),
                        item.CurrentSessionMatches, item.SystemRevisionBeforeEvent, item.HostStopping,
                        item.Elapsed)).ToArray());
        }
        catch (Exception exception)
        {
            throw new SystemTransitionDiagnosticException(phase, exception, sessionReconnect,
                composition.EventBridge.GetSessionEventHistoryForDiagnostics()
                    .Where(item => item.Sequence > armedNotificationSequence)
                    .Select(item => new SessionNotificationEvidence(item.Sequence, item.Kind.ToString(),
                        item.CurrentSessionMatches, item.SystemRevisionBeforeEvent, item.HostStopping,
                        item.Elapsed)).ToArray());
        }
        finally
        {
            await composition.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static bool HasValidatedSessionReconnect(SessionReconnectEvidence? terminal,
        IReadOnlyList<SessionNotificationEvidence>? notifications)
    {
        if (terminal is not { TerminalState: "Succeeded", FailureReason: "None" } ||
            terminal.Path is not ("Resume" or "Reconfigure" or "Rebuild") || notifications is null)
            return false;
        return notifications.Any(disconnect => disconnect.CurrentSessionMatches && !disconnect.HostStopping &&
            disconnect.Kind == "SessionDisconnected" && notifications.Any(reconnect =>
                reconnect.CurrentSessionMatches && !reconnect.HostStopping &&
                reconnect.Kind == "SessionReconnected" && reconnect.Sequence > disconnect.Sequence &&
                reconnect.SystemRevisionBeforeEvent < terminal.ValidationRevision));
    }

    private static DiagnosticComposition CreateComposition(
        string rendererExecutablePath,
        DisplayTopology topology,
        ProductCandidateDisplaySelection selection,
        IRendererProcessLauncher? processLauncher = null)
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
        RendererProcessProvider provider = CreateRendererProvider(rendererExecutablePath, processLauncher);
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

    private static RendererProcessProvider CreateRendererProvider(string executablePath,
        IRendererProcessLauncher? processLauncher = null) =>
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
            TimeSpan.FromSeconds(10)), processLauncher);

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

    private static async Task<SystemStateChange> WaitForSystemStateReasonAsync(
        WindowsDesktopEventBridge eventBridge,
        SystemStateChangeReason reason,
        CancellationToken cancellationToken,
        long afterRevision = 0)
    {
        while (true)
        {
            SystemStateChange? change = eventBridge.GetSystemStateHistoryForDiagnostics()
                .FirstOrDefault(item => item.Reason == reason && item.Revision > afterRevision);
            if (change is not null) return change;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<HostStateSnapshot> WaitForPlayingSessionsAsync(
        HostCommandLoop commandLoop,
        int expectedSessionCount,
        CancellationToken cancellationToken,
        PlaybackState expectedState = PlaybackState.Playing)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HostStateSnapshot state = await GetStateAsync(commandLoop, cancellationToken)
                .ConfigureAwait(false);
            if (state.Sessions.Count == expectedSessionCount &&
                state.Sessions.All(item => item.Session.State == expectedState))
            {
                return state;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static bool HasCompletedPlaybackExchange(
        IReadOnlyList<RendererPlaybackEvidence> evidence, PlaybackState state)
    {
        // Feedback can race WriteCompleted. Compare with WriteStarted; require both
        // successful write and independently received feedback in the same generation.
        return evidence.Any(start => start.Kind == RendererPlaybackEvidenceKind.WriteStarted &&
            start.State == state && evidence.Any(write =>
                write.Kind == RendererPlaybackEvidenceKind.WriteCompleted &&
                write.Generation == start.Generation && write.SessionId == start.SessionId &&
                write.State == state && write.Sequence > start.Sequence) &&
            evidence.Any(feedback => feedback.Kind == RendererPlaybackEvidenceKind.FeedbackReceived &&
                feedback.Generation == start.Generation && feedback.SessionId == start.SessionId &&
                feedback.State == state && feedback.Sequence > start.Sequence));
    }

    private static async Task WaitForPlaybackFeedbackAsync(
        IReadOnlyList<RendererProtocolSession> renderers, PlaybackState state,
        CancellationToken cancellationToken)
    {
        while (!renderers.All(renderer =>
               {
                   IReadOnlyList<RendererPlaybackEvidence> evidence =
                       renderer.GetPlaybackEvidenceForDiagnostics();
                   return HasCompletedPlaybackExchange(evidence, state) &&
                       evidence.LastOrDefault(item => item.Kind ==
                           RendererPlaybackEvidenceKind.FeedbackReceived)?.State == state;
               }))
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
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
