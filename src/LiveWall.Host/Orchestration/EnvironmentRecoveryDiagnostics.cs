using System.Collections.Concurrent;
using System.Threading.Channels;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Host.CommandLoop;

namespace LiveWall.Host.Orchestration;

public enum EnvironmentRecoveryClassification
{
    ValidSuccess,
    EnvironmentInconclusive,
    ProductFailure,
    HarnessFailure,
}

public sealed record WindowsHostEnvironmentRecoveryRequest(
    string RendererExecutablePath, DisplayTopology DisplayTopology,
    ProductCandidateDisplaySelection DisplaySelection, TimeSpan HumanOperationTimeout,
    TimeSpan RecoveredVisibleDuration, Func<CancellationToken, Task> RestartShellAsync,
    Action? Armed = null, Action? Restarting = null);

public sealed record EnvironmentSurfaceEvidence(string SessionId, int SurfaceOrdinal,
    IReadOnlyList<DisplayId> Displays, DisplayBounds Bounds);

public sealed record RendererCleanupEvidence(string SessionId, bool ExitObserved,
    bool HandleDisposed, string? Issue);

public sealed record WindowsHostEnvironmentRecoveryResult(
    EnvironmentRecoveryClassification Classification, bool PendingAtRestart,
    bool OverlapProven, bool ExplorerRestartInvoked, long InitialGeneration, long FinalGeneration,
    long InitialTopologyRevision, long FinalTopologyRevision, long BatchReplaceCount,
    long ValidatedShellChanges, long ForwardedRecoveryCommands,
    IReadOnlyList<DisplayTopology> PublishedTopologies,
    IReadOnlyList<DisplayReconciliationResult> Reconciliation,
    IReadOnlyList<DesktopRecoveryResult> Recovery,
    IReadOnlyList<EnvironmentSurfaceEvidence> FinalSurfaces,
    IReadOnlyList<SessionRetirementResult> Retirement,
    IReadOnlyList<RendererCleanupEvidence> RendererCleanup,
    bool OwnedResourcesCleaned, bool EventOverflow, bool FinalRevisionValidated);

public sealed class EnvironmentRecoveryDiagnosticException(string phase, Exception inner)
    : Exception($"Environment recovery diagnostic failed during {phase}.", inner)
{
    public string Phase { get; } = phase;
}

public static partial class WindowsHostEventChainDiagnostics
{
    public static EnvironmentRecoveryClassification ClassifyEnvironmentRecovery(
        bool overlapProven, bool productInvariantsValid, bool eventOverflow) => eventOverflow
            ? EnvironmentRecoveryClassification.HarnessFailure
            : !productInvariantsValid ? EnvironmentRecoveryClassification.ProductFailure
            : overlapProven ? EnvironmentRecoveryClassification.ValidSuccess
            : EnvironmentRecoveryClassification.EnvironmentInconclusive;

    public static bool HasShellTopologyOverlap(DisplayReconciliationResult first, bool pendingAtRestart) =>
        pendingAtRestart && first.TerminalState == DisplayReconciliationTerminalState.Cancelled &&
        first.FailureReason == ApplyFailureReason.Superseded &&
        first.Detail?.Contains("confirmed Shell generation", StringComparison.Ordinal) == true;

    public static async Task<WindowsHostEnvironmentRecoveryResult> RunEnvironmentRecoveryAsync(
        WindowsHostEnvironmentRecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RestartShellAsync);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.HumanOperationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.RecoveredVisibleDuration, TimeSpan.Zero);
        DiagnosticRendererProcessTracker tracker = new();
        var composition = CreateComposition(request.RendererExecutablePath, request.DisplayTopology,
            request.DisplaySelection, tracker).Composition;
        Channel<DisplayTopology> published = Channel.CreateBounded<DisplayTopology>(128);
        long armedRevision = long.MaxValue;
        int overflow = 0;
        string phase = "Startup";
        void OnTopology(object? sender, DisplayTopologyChangedEventArgs args)
        {
            if (args.Current.Revision > Interlocked.Read(ref armedRevision) &&
                !published.Writer.TryWrite(args.Current)) Interlocked.Exchange(ref overflow, 1);
        }
        try
        {
            await composition.Runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            EnsureInitialApplySucceeded(composition.Runtime);
            // Subscribe after Runtime.Start: the bridge must post the revision before
            // this observer wakes its consumer and issues a command-loop barrier.
            composition.DisplayTopologySource.Changed += OnTopology;
            HostStateSnapshot initial = await GetStateAsync(composition.CommandLoop, cancellationToken)
                .ConfigureAwait(false);
            long[] previousRecoveryIds = composition.CommandLoop.GetRecoveryOperationIdsForDiagnostics().ToArray();
            long initialReplacements = composition.DesktopHost.ReplacementCountForDiagnostics;
            Interlocked.Exchange(ref armedRevision, initial.DisplayTopology!.Revision);
            request.Armed?.Invoke();
            using CancellationTokenSource human = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            human.CancelAfter(request.HumanOperationTimeout);
            phase = "AwaitingTopologyChange";
            DisplayTopology first = await published.Reader.ReadAsync(human.Token).ConfigureAwait(false);
            phase = "TopologyAndShellRecovery";
            using CancellationTokenSource verification = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            verification.CancelAfter(ApplyDeadlineBudget.Default.Calculate(Math.Max(1, initial.Sessions.Count)) * 4 +
                TimeSpan.FromSeconds(30));
            // Bridge subscribed first; its posted topology command precedes this queue barrier.
            await GetStateAsync(composition.CommandLoop, verification.Token).ConfigureAwait(false);
            Task<DisplayReconciliationResult> firstTerminal = composition.CommandLoop
                .WaitForDisplayReconciliationAsync(first.Revision, verification.Token);
            bool pending = !firstTerminal.IsCompleted;
            bool restarted = false;
            List<DesktopRecoveryResult> recoveries = [];
            if (pending)
            {
                request.Restarting?.Invoke();
                restarted = true; // Exactly one explicit attempt; never retry a Shell mutation.
                await request.RestartShellAsync(verification.Token).ConfigureAwait(false);
                long recoveryId = await WaitForNewRecoveryAsync(composition.CommandLoop, previousRecoveryIds,
                    verification.Token).ConfigureAwait(false);
                recoveries.Add(await composition.CommandLoop.WaitForDesktopRecoveryAsync(recoveryId,
                    verification.Token).ConfigureAwait(false));
            }
            List<DisplayTopology> topologies = [first];
            List<DisplayReconciliationResult> reconciliations = [await firstTerminal.ConfigureAwait(false)];
            // Keep event intake active while awaiting each explicit terminal. Include late events,
            // not a sequential sampler that stops observing during preparation.
            await Task.Delay(FallbackStabilityWindow, verification.Token).ConfigureAwait(false);
            while (published.Reader.TryRead(out DisplayTopology? next))
            {
                topologies.Add(next);
                await GetStateAsync(composition.CommandLoop, verification.Token).ConfigureAwait(false);
                reconciliations.Add(await composition.CommandLoop.WaitForDisplayReconciliationAsync(next.Revision,
                    verification.Token).ConfigureAwait(false));
            }
            await Task.Delay(request.RecoveredVisibleDuration, verification.Token).ConfigureAwait(false);
            // Freeze the sample before shutdown; a change during the observation window is
            // observable and makes the final revision differ from the validated result.
            composition.DisplayTopologySource.Changed -= OnTopology;
            while (published.Reader.TryRead(out DisplayTopology? next))
            {
                topologies.Add(next);
                await GetStateAsync(composition.CommandLoop, verification.Token).ConfigureAwait(false);
                reconciliations.Add(await composition.CommandLoop.WaitForDisplayReconciliationAsync(next.Revision,
                    verification.Token).ConfigureAwait(false));
            }
            HostStateSnapshot final = await GetStateAsync(composition.CommandLoop, verification.Token).ConfigureAwait(false);
            long[] finalRecoveryIds = composition.CommandLoop.GetRecoveryOperationIdsForDiagnostics()
                .Except(previousRecoveryIds).ToArray();
            foreach (long id in finalRecoveryIds.Except(recoveries.Select(item => item.RecoveryId)))
                recoveries.Add(await composition.CommandLoop.WaitForDesktopRecoveryAsync(id, verification.Token)
                    .ConfigureAwait(false));
            final = await GetStateAsync(composition.CommandLoop, verification.Token).ConfigureAwait(false);
            EnvironmentSurfaceEvidence[] surfaces = composition.CommandLoop.GetPreparedSessionsForDiagnostics()
                .Select(session => new EnvironmentSurfaceEvidence(session.Session.Id.Value, session.SurfaceOrdinal,
                    session.Session.Displays, DisplayBounds.Union(session.Surfaces.Select(item => item.Request.Bounds))))
                .ToArray();
            long replacements = composition.DesktopHost.ReplacementCountForDiagnostics - initialReplacements;
            bool finalRevisionValidated = final.DisplayTopology!.Revision == topologies.Max(item => item.Revision);
            bool overlap = HasShellTopologyOverlap(reconciliations[0], pending);
            var connected = final.DisplayTopology!.Displays.Select(display => display.Id).ToHashSet();
            int expectedSurfaces = final.Assignments.Count(item => connected.Contains(item.DisplayId));
            bool terminalsValid = recoveries.All(item => item.TerminalState == DesktopRecoveryTerminalState.Succeeded) &&
                reconciliations.All(item => item.TerminalState == DisplayReconciliationTerminalState.Succeeded ||
                    item.TerminalState == DisplayReconciliationTerminalState.Cancelled &&
                    item.FailureReason == ApplyFailureReason.Superseded);
            bool geometryValid = surfaces.Length == expectedSurfaces && surfaces.All(surface =>
                surface.Displays.Count == 1 && final.DisplayTopology.Displays.Any(display =>
                    display.Id == surface.Displays[0] && display.Bounds == surface.Bounds));
            bool commitsValid = !overlap || recoveries.Count == 1 && replacements == 1;
            phase = "ShutdownAndCleanup";
            await composition.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
            RendererCleanupEvidence[] rendererCleanup = tracker.Snapshot();
            bool cleaned = composition.DesktopHost.OwnedSurfaceCountForDiagnostics == 0 &&
                rendererCleanup.Length > 0 && rendererCleanup.All(item => item.ExitObserved && item.HandleDisposed && item.Issue is null);
            SessionRetirementResult[] retirement = composition.CommandLoop.GetRetirementHistoryForDiagnostics()
                .SelectMany(batch => batch.Sessions).ToArray();
            bool retirementValid = retirement.Length > 0 && retirement.All(item => item.OwnedResourcesCleaned) &&
                retirement.Where(item => surfaces.Any(surface => surface.SessionId == item.SessionId.Value))
                    .All(item => item.Graceful && item.Issues.Count == 0);
            bool valid = terminalsValid && geometryValid && commitsValid && cleaned && retirementValid &&
                (!restarted || recoveries.Count > 0) && (!overlap || composition.EventBridge.ValidatedShellChangeCount == 1 &&
                    composition.EventBridge.ForwardedRecoveryCount == 1);
            return new(ClassifyEnvironmentRecovery(overlap && finalRevisionValidated, valid, overflow != 0), pending, overlap, restarted,
                initial.Generation, final.Generation, initial.DisplayTopology.Revision, final.DisplayTopology.Revision,
                replacements, composition.EventBridge.ValidatedShellChangeCount, composition.EventBridge.ForwardedRecoveryCount,
                topologies.ToArray(), reconciliations.ToArray(), recoveries.ToArray(), surfaces,
                retirement, rendererCleanup, cleaned, overflow != 0, finalRevisionValidated);
        }
        catch (Exception exception) when (exception is not EnvironmentRecoveryDiagnosticException)
        {
            throw new EnvironmentRecoveryDiagnosticException(phase, exception);
        }
        finally
        {
            composition.DisplayTopologySource.Changed -= OnTopology;
            await composition.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}

internal sealed class DiagnosticRendererProcessTracker(IRendererProcessLauncher? launcher = null,
    TimeSpan? exitBudget = null) : IRendererProcessLauncher
{
    private readonly IRendererProcessLauncher launcher = launcher ?? new RendererProcessLauncher();
    private readonly ConcurrentQueue<TrackedHandle> handles = new();
    public IRendererProcessHandle Start(RendererProcessStartRequest request)
    {
        TrackedHandle handle = new(launcher.Start(request), request.SessionId, exitBudget ?? TimeSpan.FromSeconds(5));
        handles.Enqueue(handle);
        return handle;
    }
    public RendererCleanupEvidence[] Snapshot() => handles.Select(handle => handle.Evidence).ToArray();

    private sealed class TrackedHandle(IRendererProcessHandle inner, string sessionId, TimeSpan exitBudget) : IRendererProcessHandle
    {
        private RendererCleanupEvidence evidence = new(sessionId, false, false, null);
        public RendererCleanupEvidence Evidence => Volatile.Read(ref evidence);
        public int Id => inner.Id;
        public bool HasExited => inner.HasExited;
        public int? ExitCode => inner.ExitCode;
        public Task WaitForExitAsync(CancellationToken token) => inner.WaitForExitAsync(token);
        public void Terminate() => inner.Terminate();
        public async ValueTask DisposeAsync()
        {
            bool exited = false;
            bool disposed = false;
            string? issue = null;
            try
            {
                using CancellationTokenSource budget = new(exitBudget);
                await inner.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                exited = inner.HasExited;
            }
            catch (Exception exception) { issue = exception.GetType().Name; }
            try { await inner.DisposeAsync().ConfigureAwait(false); disposed = true; }
            catch (Exception exception) { issue ??= exception.GetType().Name; throw; }
            finally { Volatile.Write(ref evidence, new(sessionId, exited, disposed, issue)); }
        }
    }
}
