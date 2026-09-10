using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

public static class RaisedDesktopDiagnostics
{
    private static readonly TimeSpan StabilityDelay = TimeSpan.FromMilliseconds(350);

    public static Task<RaisedDesktopDiagnosticResult> RunProbeAsync(
        ShellWindowBounds virtualDesktopBounds,
        Action<RaisedDesktopSnapshotStage, ShellTopologySnapshot>? snapshotObserver,
        CancellationToken cancellationToken) =>
        RunAsync(
            virtualDesktopBounds,
            colorReference: null,
            presentationKind: null,
            duration: TimeSpan.Zero,
            snapshotObserver,
            WindowsShellTopologyDiagnostics.CaptureSnapshot,
            delay => Task.Delay(delay, cancellationToken),
            cancellationToken);

    public static Task<RaisedDesktopDiagnosticResult> RunColorBlockAsync(
        ShellWindowBounds virtualDesktopBounds,
        uint colorReference,
        TimeSpan duration,
        Action<RaisedDesktopSnapshotStage, ShellTopologySnapshot>? snapshotObserver,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Raised Desktop color-block duration must be between 1 millisecond and 30 seconds.");
        }

        return RunPresentationProbeAsync(
            virtualDesktopBounds,
            colorReference,
            RaisedDesktopPresentationKind.LayeredGdi,
            duration,
            snapshotObserver,
            cancellationToken);
    }

    public static Task<RaisedDesktopDiagnosticResult> RunPresentationProbeAsync(
        ShellWindowBounds virtualDesktopBounds,
        uint colorReference,
        RaisedDesktopPresentationKind presentationKind,
        TimeSpan duration,
        Action<RaisedDesktopSnapshotStage, ShellTopologySnapshot>? snapshotObserver,
        CancellationToken cancellationToken)
    {
        if (presentationKind is not RaisedDesktopPresentationKind.LayeredGdi and
            not RaisedDesktopPresentationKind.NonLayeredGdi and
            not RaisedDesktopPresentationKind.DirectCompositionSwapChain)
        {
            throw new ArgumentOutOfRangeException(nameof(presentationKind));
        }

        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Raised Desktop presentation-probe duration must be between 1 millisecond and 30 seconds.");
        }

        return RunAsync(
            virtualDesktopBounds,
            colorReference,
            presentationKind,
            duration,
            snapshotObserver,
            WindowsShellTopologyDiagnostics.CaptureSnapshot,
            delay => Task.Delay(delay, cancellationToken),
            cancellationToken);
    }

    public static async Task<RaisedDesktopHostSurfaceLease> AcquireHostSurfaceAsync(
        ShellWindowBounds virtualDesktopBounds,
        Action<RaisedDesktopSnapshotStage, ShellTopologySnapshot>? snapshotObserver,
        CancellationToken cancellationToken)
    {
        DesktopAttachmentLease attachmentLease = await DiscoverAttachmentLeaseCoreAsync(
                RaisedDesktopAdapter.AdapterId,
                virtualDesktopBounds,
                snapshotObserver,
                WindowsShellTopologyDiagnostics.CaptureSnapshot(),
                cancellationToken)
            .ConfigureAwait(false);
        return await RaisedDesktopHostSurfaceLease.CreateAsync(
                attachmentLease, virtualDesktopBounds, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static Task<DesktopAttachmentLease> DiscoverAttachmentLeaseAsync(
        string adapterId,
        Action<RaisedDesktopSnapshotStage, ShellTopologySnapshot>? snapshotObserver,
        CancellationToken cancellationToken)
    {
        ShellTopologySnapshot before = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        ShellWindowFingerprint? progman = before.TopLevelWindows.SingleOrDefault(window =>
            window.WindowHandle == before.ShellWindowHandle &&
            window.ClassName == "Progman");
        if (progman?.Bounds is not ShellWindowBounds virtualDesktopBounds)
        {
            throw new DesktopAttachPointUnavailableException(
                "The current Progman desktop bounds were unavailable.");
        }

        return DiscoverAttachmentLeaseCoreAsync(
            adapterId,
            virtualDesktopBounds,
            snapshotObserver,
            before,
            cancellationToken);
    }

    private static async Task<DesktopAttachmentLease> DiscoverAttachmentLeaseCoreAsync(
        string adapterId,
        ShellWindowBounds virtualDesktopBounds,
        Action<RaisedDesktopSnapshotStage, ShellTopologySnapshot>? snapshotObserver,
        ShellTopologySnapshot before,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        cancellationToken.ThrowIfCancellationRequested();
        snapshotObserver?.Invoke(RaisedDesktopSnapshotStage.Before, before);
        await Task.Delay(StabilityDelay, cancellationToken).ConfigureAwait(false);
        ShellTopologySnapshot stable = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        snapshotObserver?.Invoke(RaisedDesktopSnapshotStage.StablePreflight, stable);
        RaisedDesktopAnalysis preflight = AnalyzePreflight(before, stable, virtualDesktopBounds);
        if (preflight.Rejections.Count > 0 || preflight.Context is null)
        {
            throw new DesktopAttachPointUnavailableException(
                string.Join("; ", preflight.Rejections.Select(item => $"{item.Code}: {item.Detail}")));
        }

        SendRequest(preflight.Context.ProgmanWindowHandle);
        await Task.Delay(StabilityDelay, cancellationToken).ConfigureAwait(false);
        ShellTopologySnapshot requestedSnapshot = WindowsShellTopologyDiagnostics.CaptureSnapshot();
        snapshotObserver?.Invoke(RaisedDesktopSnapshotStage.AfterRequest, requestedSnapshot);
        RaisedDesktopAnalysis requested = AnalyzeRequestedTopology(
            stable, requestedSnapshot, virtualDesktopBounds);
        if (requested.Rejections.Count > 0 || requested.Context is null)
        {
            throw new DesktopAttachPointUnavailableException(
                string.Join("; ", requested.Rejections.Select(item => $"{item.Code}: {item.Detail}")));
        }

        ShellWindowFingerprint? competingSurface = requestedSnapshot.ShellDescendants
            .Where(window =>
                window.ParentWindowHandle == requested.Context.ProgmanWindowHandle &&
                window.ProcessId != requested.Context.ProgmanProcessId &&
                window.IsVisible &&
                window.Bounds == virtualDesktopBounds &&
                window.SiblingZOrderIndex > requested.Context.DefViewZOrderIndex &&
                window.SiblingZOrderIndex < requested.Context.WorkerZOrderIndex)
            .OrderBy(window => window.SiblingZOrderIndex)
            .FirstOrDefault();
        if (competingSurface is not null)
        {
            throw new DesktopAttachPointUnavailableException(
                $"{RaisedDesktopRejectionCode.CompetingDesktopSurface}: " +
                $"A visible full-desktop window from process " +
                $"'{competingSurface.ProcessName ?? "unknown"}' already occupies the Raised Desktop layer. " +
                "Exit the competing wallpaper application before running this diagnostic.");
        }

        return DesktopAttachmentLease.CreateRaised(
            adapterId,
            requested.Context.ProgmanWindowHandle,
            requested.Context.DefViewWindowHandle,
            requested.Context.WorkerWindowHandle,
            requestedSnapshot.ShellWindowHandle,
            requested.Context.ProgmanProcessId,
            requested.AfterFingerprint.Value);
    }

    internal static RaisedDesktopAnalysis AnalyzePreflight(
        ShellTopologySnapshot before,
        ShellTopologySnapshot stable,
        ShellWindowBounds virtualDesktopBounds)
    {
        List<RaisedDesktopRejection> rejections = ValidateBase(before);
        rejections.AddRange(ValidateBase(stable));
        RaisedDesktopStructureFingerprint beforeFingerprint = CreateFingerprint(before);
        RaisedDesktopStructureFingerprint stableFingerprint = CreateFingerprint(stable);
        if (CreateCandidateFingerprint(before).Value != CreateCandidateFingerprint(stable).Value)
        {
            rejections.Add(new(
                RaisedDesktopRejectionCode.UnexpectedTopologyMutation,
                "The Shell structure changed during the 350 ms read-only stability interval."));
        }

        RaisedDesktopTransientContext? context = rejections.Count == 0
            ? ResolveContext(stable, virtualDesktopBounds, rejections)
            : null;
        return new RaisedDesktopAnalysis(
            beforeFingerprint,
            stableFingerprint,
            Deduplicate(rejections),
            context);
    }

    internal static RaisedDesktopAnalysis AnalyzeRequestedTopology(
        ShellTopologySnapshot before,
        ShellTopologySnapshot after,
        ShellWindowBounds virtualDesktopBounds)
    {
        List<RaisedDesktopRejection> rejections = [];
        RaisedDesktopTransientContext? baseContext = ResolveContext(after, virtualDesktopBounds, rejections);
        if (baseContext is null)
        {
            return new(
                CreateFingerprint(before),
                CreateFingerprint(after),
                Deduplicate(rejections),
                null);
        }

        ShellWindowFingerprint[] allWorkers = after.ShellDescendants
            .Where(window => window.ClassName == "WorkerW")
            .ToArray();
        ShellWindowFingerprint[] workers = allWorkers
            .Where(window =>
                window.ParentWindowHandle == after.ShellWindowHandle)
            .ToArray();
        ShellWindowFingerprint[] validWorkers = workers
            .Where(window =>
                window.ProcessId == baseContext.ProgmanProcessId &&
                window.OwnerWindowHandle == 0 &&
                window.ShellDefViewWindowHandle == 0 &&
                window.Bounds == virtualDesktopBounds &&
                window.SiblingZOrderIndex > baseContext.DefViewZOrderIndex)
            .ToArray();

        if (workers.Length == 0)
        {
            rejections.Add(new(
                allWorkers.Length == 0
                    ? RaisedDesktopRejectionCode.WorkerNotCreated
                    : RaisedDesktopRejectionCode.WorkerNotDirectChild,
                allWorkers.Length == 0
                    ? "Raised Desktop request did not produce a WorkerW descendant."
                    : "Raised Desktop request produced WorkerW descendants, but none is a direct Progman child."));
        }
        else if (validWorkers.Length == 0)
        {
            AddWorkerRejections(
                rejections,
                workers[0],
                after.ShellWindowHandle,
                baseContext,
                virtualDesktopBounds);
        }

        ShellWindowFingerprint? worker = validWorkers
            .OrderBy(window => window.SiblingZOrderIndex)
            .FirstOrDefault();
        if (worker is not null && !HasExpectedMutation(before, after, worker))
        {
            rejections.Add(new(
                RaisedDesktopRejectionCode.UnexpectedTopologyMutation,
                "Raised Desktop request changed more than the explainable WorkerW structure."));
        }

        RaisedDesktopTransientContext? context = worker is null
            ? null
            : baseContext with
            {
                WorkerWindowHandle = worker.WindowHandle,
                WorkerZOrderIndex = worker.SiblingZOrderIndex,
            };
        return new(
            CreateFingerprint(before),
            CreateFingerprint(after),
            Deduplicate(rejections),
            context);
    }

    internal static RaisedDesktopStructureFingerprint CreateFingerprint(
        ShellTopologySnapshot snapshot)
    {
        Dictionary<ulong, ShellWindowFingerprint> windows = snapshot.TopLevelWindows
            .Concat(snapshot.ShellDescendants)
            .ToDictionary(window => window.WindowHandle);
        IEnumerable<string> tokens = snapshot.TopLevelWindows
            .Concat(snapshot.ShellDescendants)
            .Select(window => CreateToken(window, windows))
            .OrderBy(token => token, StringComparer.Ordinal);
        string summary = string.Join("\n", tokens);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(summary)))
            .ToLowerInvariant();
        return new(hash, summary);
    }

    internal static RaisedDesktopStructureFingerprint CreateCandidateFingerprint(
        ShellTopologySnapshot snapshot)
    {
        Dictionary<ulong, ShellWindowFingerprint> windows = snapshot.TopLevelWindows
            .Concat(snapshot.ShellDescendants)
            .ToDictionary(window => window.WindowHandle);
        IEnumerable<ShellWindowFingerprint> candidateWindows = snapshot.TopLevelWindows
            .Where(window => window.WindowHandle == snapshot.ShellWindowHandle)
            .Concat(snapshot.ShellDescendants);
        IEnumerable<string> tokens = candidateWindows
            .Select(window => CreateToken(
                window,
                windows,
                includeSiblingOrder: window.Scope == ShellWindowScope.ShellDescendant))
            .OrderBy(token => token, StringComparer.Ordinal);
        string summary = string.Join("\n", tokens);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(summary)))
            .ToLowerInvariant();
        return new(hash, summary);
    }

    internal static RaisedDesktopDiagnosticStatus ClassifyRejectedRequest(
        ShellTopologySnapshot before,
        ShellTopologySnapshot after) =>
        IntroducedWorker(before, after)
            ? RaisedDesktopDiagnosticStatus.CleanupIncomplete
            : RaisedDesktopDiagnosticStatus.Rejected;

    private static async Task<RaisedDesktopDiagnosticResult> RunAsync(
        ShellWindowBounds virtualDesktopBounds,
        uint? colorReference,
        RaisedDesktopPresentationKind? presentationKind,
        TimeSpan duration,
        Action<RaisedDesktopSnapshotStage, ShellTopologySnapshot>? snapshotObserver,
        Func<ShellTopologySnapshot> captureSnapshot,
        Func<TimeSpan, Task> delay,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShellTopologySnapshot before = captureSnapshot();
        snapshotObserver?.Invoke(RaisedDesktopSnapshotStage.Before, before);
        await delay(StabilityDelay).ConfigureAwait(false);
        ShellTopologySnapshot stable = captureSnapshot();
        snapshotObserver?.Invoke(RaisedDesktopSnapshotStage.StablePreflight, stable);
        RaisedDesktopAnalysis preflight = AnalyzePreflight(
            before,
            stable,
            virtualDesktopBounds);
        if (preflight.Rejections.Count > 0 || preflight.Context is null)
        {
            return CreateResult(
                RaisedDesktopDiagnosticStatus.Rejected,
                requestSent: false,
                surfaceCreated: false,
                presentationKind,
                RaisedDesktopOwnedResourcesCleanup.Complete,
                RaisedDesktopShellMutationRecovery.NoMutationObserved,
                preflight.BeforeFingerprint,
                preflight.AfterFingerprint,
                preflight.AfterFingerprint,
                preflight.Rejections);
        }

        SendRequest(preflight.Context.ProgmanWindowHandle);
        await delay(StabilityDelay).ConfigureAwait(false);
        ShellTopologySnapshot afterRequest = captureSnapshot();
        snapshotObserver?.Invoke(RaisedDesktopSnapshotStage.AfterRequest, afterRequest);
        RaisedDesktopAnalysis requested = AnalyzeRequestedTopology(
            stable,
            afterRequest,
            virtualDesktopBounds);
        if (requested.Rejections.Count > 0 || requested.Context is null)
        {
            bool requestShellMutationRemains = IntroducedWorker(stable, afterRequest);
            return CreateResult(
                ClassifyRejectedRequest(stable, afterRequest),
                requestSent: true,
                surfaceCreated: false,
                presentationKind,
                RaisedDesktopOwnedResourcesCleanup.Complete,
                requestShellMutationRemains
                    ? RaisedDesktopShellMutationRecovery.Incomplete
                    : RaisedDesktopShellMutationRecovery.NoMutationObserved,
                preflight.BeforeFingerprint,
                requested.AfterFingerprint,
                requested.AfterFingerprint,
                AddCleanupRejectionIfNeeded(stable, afterRequest, requested.Rejections));
        }

        bool surfaceCreated = false;
        List<RaisedDesktopRejection> finalRejections = [];
        if (colorReference is not null)
        {
            await using (IAsyncDisposable session = await CreatePresentationSessionAsync(
                    requested.Context,
                    virtualDesktopBounds,
                    colorReference.Value,
                    presentationKind ?? throw new InvalidOperationException(
                        "A color probe requires a presentation kind."),
                    cancellationToken).ConfigureAwait(false))
            {
                surfaceCreated = true;
                ShellTopologySnapshot active = captureSnapshot();
                snapshotObserver?.Invoke(RaisedDesktopSnapshotStage.ColorBlockActive, active);
                if (!ValidatePresentationSurface(
                        active,
                        requested.Context,
                        virtualDesktopBounds,
                        presentationKind!.Value))
                {
                    finalRejections.Add(new(
                        RaisedDesktopRejectionCode.SurfaceLayerValidationFailed,
                        "The LiveWall test surface was not between DefView and the Shell WorkerW."));
                }

                if (finalRejections.Count == 0)
                {
                    await delay(duration).ConfigureAwait(false);
                }
            }
        }

        ShellTopologySnapshot afterCleanup = captureSnapshot();
        snapshotObserver?.Invoke(RaisedDesktopSnapshotStage.AfterCleanup, afterCleanup);
        bool ownedResourcesClean = !afterCleanup.ShellDescendants.Any(window =>
            window.ClassName.StartsWith("LiveWall.RaisedDesktopColor", StringComparison.Ordinal));
        bool shellMutationRemains = IntroducedWorker(stable, afterCleanup);
        if (!ownedResourcesClean)
        {
            finalRejections.Add(new(
                RaisedDesktopRejectionCode.CleanupIncomplete,
                "A LiveWall diagnostic window remained after owned-resource cleanup."));
        }
        if (shellMutationRemains)
        {
            finalRejections.Add(new(
                RaisedDesktopRejectionCode.CleanupIncomplete,
                "The Raised Desktop request left a Shell WorkerW; restart Explorer only with user confirmation."));
        }

        return CreateResult(
            !ownedResourcesClean || shellMutationRemains
                ? RaisedDesktopDiagnosticStatus.CleanupIncomplete
                : finalRejections.Count > 0
                    ? RaisedDesktopDiagnosticStatus.Rejected
                    : presentationKind is null
                        ? RaisedDesktopDiagnosticStatus.StructuralCandidate
                        : RaisedDesktopDiagnosticStatus.PresentationProbeCompleted,
            requestSent: true,
            surfaceCreated,
            presentationKind,
            ownedResourcesClean
                ? RaisedDesktopOwnedResourcesCleanup.Complete
                : RaisedDesktopOwnedResourcesCleanup.Incomplete,
            shellMutationRemains
                ? RaisedDesktopShellMutationRecovery.Incomplete
                : RaisedDesktopShellMutationRecovery.NoMutationObserved,
            preflight.BeforeFingerprint,
            requested.AfterFingerprint,
            CreateFingerprint(afterCleanup),
            finalRejections);
    }

    private static async Task<IAsyncDisposable> CreatePresentationSessionAsync(
        RaisedDesktopTransientContext context,
        ShellWindowBounds virtualDesktopBounds,
        uint colorReference,
        RaisedDesktopPresentationKind presentationKind,
        CancellationToken cancellationToken) =>
        presentationKind == RaisedDesktopPresentationKind.DirectCompositionSwapChain
            ? await RaisedDesktopDirectCompositionSession.CreateAsync(
                    context.ProgmanWindowHandle,
                    context.DefViewWindowHandle,
                    context.WorkerWindowHandle,
                    virtualDesktopBounds,
                    colorReference,
                    cancellationToken)
                .ConfigureAwait(false)
            : await RaisedDesktopColorBlockSession.CreateAsync(
                    context.ProgmanWindowHandle,
                    context.DefViewWindowHandle,
                    context.WorkerWindowHandle,
                    virtualDesktopBounds,
                    colorReference,
                    presentationKind,
                    cancellationToken)
                .ConfigureAwait(false);

    private static List<RaisedDesktopRejection> ValidateBase(ShellTopologySnapshot snapshot)
    {
        List<RaisedDesktopRejection> rejections = [];
        if (snapshot.Availability == ShellTopologyAvailability.ShellUnavailable)
        {
            rejections.Add(new(
                RaisedDesktopRejectionCode.ShellUnavailable,
                "Explorer Shell is unavailable in the current desktop context."));
            return rejections;
        }

        if (!string.Equals(snapshot.ExecutionContext.WindowStationName, "WinSta0", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.ExecutionContext.DesktopName, "Default", StringComparison.OrdinalIgnoreCase))
        {
            rejections.Add(new(
                RaisedDesktopRejectionCode.WrongInteractiveDesktop,
                "Raised Desktop diagnostics require WinSta0\\Default."));
        }

        return rejections;
    }

    private static RaisedDesktopTransientContext? ResolveContext(
        ShellTopologySnapshot snapshot,
        ShellWindowBounds virtualDesktopBounds,
        List<RaisedDesktopRejection> rejections)
    {
        ShellWindowFingerprint? progman = snapshot.TopLevelWindows.SingleOrDefault(window =>
            window.WindowHandle == snapshot.ShellWindowHandle && window.ClassName == "Progman");
        if (progman is null)
        {
            rejections.Add(new(
                RaisedDesktopRejectionCode.ProgmanNotShellAnchor,
                "GetShellWindow did not identify the captured Progman window."));
            return null;
        }

        if (!string.Equals(progman.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) ||
            progman.ProcessId == 0)
        {
            rejections.Add(new(
                RaisedDesktopRejectionCode.ProcessMismatch,
                "Progman is not owned by the current Explorer process."));
        }

        if ((progman.ExtendedStyle & DesktopNativeMethods.WindowExStyleNoRedirectionBitmap) == 0)
        {
            rejections.Add(new(
                RaisedDesktopRejectionCode.ProgmanNotRaised,
                "Progman does not have WS_EX_NOREDIRECTIONBITMAP."));
        }

        ShellWindowFingerprint? defView = snapshot.ShellDescendants.SingleOrDefault(window =>
            window.WindowHandle == progman.ShellDefViewWindowHandle &&
            window.ParentWindowHandle == progman.WindowHandle &&
            window.ClassName == "SHELLDLL_DefView");
        if (defView is null)
        {
            rejections.Add(new(
                RaisedDesktopRejectionCode.DefViewMissing,
                "Progman does not have a direct SHELLDLL_DefView child."));
            return null;
        }

        if ((defView.ExtendedStyle & DesktopNativeMethods.WindowExStyleLayered) == 0)
        {
            rejections.Add(new(
                RaisedDesktopRejectionCode.DefViewNotLayered,
                "SHELLDLL_DefView does not have WS_EX_LAYERED."));
        }

        if (defView.ProcessId != progman.ProcessId)
        {
            rejections.Add(new(
                RaisedDesktopRejectionCode.ProcessMismatch,
                "Progman and SHELLDLL_DefView belong to different processes."));
        }

        if (progman.Bounds != virtualDesktopBounds)
        {
            rejections.Add(new(
                RaisedDesktopRejectionCode.WorkerBoundsMismatch,
                "Progman bounds do not match the virtual desktop bounds."));
        }

        return rejections.Count == 0
            ? new(
                progman.WindowHandle,
                defView.WindowHandle,
                WorkerWindowHandle: 0,
                progman.ProcessId,
                defView.SiblingZOrderIndex,
                WorkerZOrderIndex: -1)
            : null;
    }

    private static void AddWorkerRejections(
        List<RaisedDesktopRejection> rejections,
        ShellWindowFingerprint worker,
        ulong progmanHandle,
        RaisedDesktopTransientContext context,
        ShellWindowBounds virtualDesktopBounds)
    {
        if (worker.ParentWindowHandle != progmanHandle)
        {
            rejections.Add(new(RaisedDesktopRejectionCode.WorkerNotDirectChild, "WorkerW is not a direct Progman child."));
        }
        if (worker.OwnerWindowHandle != 0)
        {
            rejections.Add(new(RaisedDesktopRejectionCode.WorkerHasOwner, "WorkerW has an owner window."));
        }
        if (worker.ProcessId != context.ProgmanProcessId)
        {
            rejections.Add(new(RaisedDesktopRejectionCode.ProcessMismatch, "WorkerW and Progman belong to different processes."));
        }
        if (worker.Bounds != virtualDesktopBounds)
        {
            rejections.Add(new(RaisedDesktopRejectionCode.WorkerBoundsMismatch, "WorkerW bounds do not match the virtual desktop."));
        }
        if (worker.SiblingZOrderIndex <= context.DefViewZOrderIndex)
        {
            rejections.Add(new(RaisedDesktopRejectionCode.WorkerZOrderInvalid, "WorkerW is not behind DefView."));
        }
    }

    internal static bool ValidatePresentationSurface(
        ShellTopologySnapshot snapshot,
        RaisedDesktopTransientContext context,
        ShellWindowBounds virtualDesktopBounds,
        RaisedDesktopPresentationKind presentationKind)
    {
        ShellWindowFingerprint? surface = snapshot.ShellDescendants.SingleOrDefault(window =>
            window.ClassName.StartsWith("LiveWall.RaisedDesktopColor", StringComparison.Ordinal));
        ShellWindowFingerprint? worker = snapshot.ShellDescendants.SingleOrDefault(window =>
            window.WindowHandle == context.WorkerWindowHandle);
        bool? isLayered = surface is null
            ? null
            : (surface.ExtendedStyle & DesktopNativeMethods.WindowExStyleLayered) != 0;
        bool expectedLayered = presentationKind == RaisedDesktopPresentationKind.LayeredGdi;
        return surface is not null &&
            worker is not null &&
            surface.ParentWindowHandle == context.ProgmanWindowHandle &&
            surface.OwnerWindowHandle == 0 &&
            surface.Bounds == virtualDesktopBounds &&
            (surface.Style & DesktopNativeMethods.WindowStyleChild) != 0 &&
            (surface.Style & DesktopNativeMethods.WindowStyleDisabled) != 0 &&
            (surface.Style & DesktopNativeMethods.WindowStyleCaption) == 0 &&
            (surface.ExtendedStyle & DesktopNativeMethods.WindowExStyleNoActivate) != 0 &&
            (surface.ExtendedStyle & DesktopNativeMethods.WindowExStyleToolWindow) != 0 &&
            isLayered == expectedLayered &&
            surface.SiblingZOrderIndex > context.DefViewZOrderIndex &&
            surface.SiblingZOrderIndex < worker.SiblingZOrderIndex;
    }

    private static bool HasExpectedMutation(
        ShellTopologySnapshot before,
        ShellTopologySnapshot after,
        ShellWindowFingerprint worker)
    {
        string[] beforeTokens = CreateStructuralTokens(before).ToArray();
        string[] afterTokens = CreateStructuralTokens(after).ToArray();
        List<string> additions = afterTokens.ToList();
        foreach (string token in beforeTokens)
        {
            _ = additions.Remove(token);
        }
        List<string> removals = beforeTokens.ToList();
        foreach (string token in afterTokens)
        {
            _ = removals.Remove(token);
        }

        return removals.Count == 0 &&
            (additions.Count == 0 ||
                additions.Count == 1 && additions[0].StartsWith("WorkerW|", StringComparison.Ordinal)) &&
            worker.ShellDefViewWindowHandle == 0;
    }

    private static bool IntroducedWorker(
        ShellTopologySnapshot before,
        ShellTopologySnapshot after)
    {
        HashSet<ulong> beforeWorkers = before.ShellDescendants
            .Where(window => window.ClassName == "WorkerW")
            .Select(window => window.WindowHandle)
            .ToHashSet();
        return after.ShellDescendants.Any(window =>
            window.ClassName == "WorkerW" && !beforeWorkers.Contains(window.WindowHandle));
    }

    private static RaisedDesktopRejection[] AddCleanupRejectionIfNeeded(
        ShellTopologySnapshot before,
        ShellTopologySnapshot after,
        IReadOnlyList<RaisedDesktopRejection> rejections)
    {
        List<RaisedDesktopRejection> result = [.. rejections];
        if (IntroducedWorker(before, after))
        {
            result.Add(new(
                RaisedDesktopRejectionCode.CleanupIncomplete,
                "The rejected request left a Shell WorkerW; Explorer recovery requires user confirmation."));
        }
        return Deduplicate(result);
    }

    private static RaisedDesktopDiagnosticResult CreateResult(
        RaisedDesktopDiagnosticStatus status,
        bool requestSent,
        bool surfaceCreated,
        RaisedDesktopPresentationKind? presentationKind,
        RaisedDesktopOwnedResourcesCleanup ownedResourcesCleanup,
        RaisedDesktopShellMutationRecovery shellMutationRecovery,
        RaisedDesktopStructureFingerprint before,
        RaisedDesktopStructureFingerprint after,
        RaisedDesktopStructureFingerprint cleanup,
        IReadOnlyList<RaisedDesktopRejection> rejections) =>
        new(
            status,
            requestSent,
            surfaceCreated,
            presentationKind,
            ownedResourcesCleanup,
            shellMutationRecovery,
            before,
            after,
            cleanup,
            rejections);

    private static IEnumerable<string> CreateStructuralTokens(ShellTopologySnapshot snapshot)
    {
        Dictionary<ulong, ShellWindowFingerprint> windows = snapshot.TopLevelWindows
            .Concat(snapshot.ShellDescendants)
            .ToDictionary(window => window.WindowHandle);
        return snapshot.ShellDescendants
            .Select(window => CreateToken(window, windows, includeSiblingOrder: false))
            .OrderBy(token => token, StringComparer.Ordinal);
    }

    private static string CreateToken(
        ShellWindowFingerprint window,
        Dictionary<ulong, ShellWindowFingerprint> windows,
        bool includeSiblingOrder = true)
    {
        string parentClass = windows.TryGetValue(window.ParentWindowHandle, out ShellWindowFingerprint? parent)
            ? parent.ClassName
            : window.ParentWindowHandle == 0 ? "none" : "external";
        string bounds = window.Bounds is null
            ? "none"
            : $"{window.Bounds.X},{window.Bounds.Y},{window.Bounds.Width},{window.Bounds.Height}";
        return string.Join('|',
            window.ClassName,
            window.Scope,
            window.Depth,
            includeSiblingOrder ? window.SiblingZOrderIndex : -1,
            parentClass,
            window.OwnerWindowHandle == 0 ? "owner:none" : "owner:present",
            window.ProcessName ?? "unknown",
            window.IsVisible,
            bounds,
            window.Style,
            window.ExtendedStyle,
            window.ShellDefViewWindowHandle == 0 ? "defview:none" : "defview:direct",
            window.SysListViewWindowHandle == 0 ? "listview:none" : "listview:direct");
    }

    private static RaisedDesktopRejection[] Deduplicate(
        IEnumerable<RaisedDesktopRejection> rejections) =>
        rejections.DistinctBy(rejection => (rejection.Code, rejection.Detail)).ToArray();

    private static void SendRequest(ulong progmanWindowHandle)
    {
        nint result = DesktopNativeMethods.SendMessageTimeout(
            unchecked((nint)(long)progmanWindowHandle),
            DesktopNativeMethods.WorkerWMessage,
            0x0D,
            1,
            DesktopNativeMethods.SendMessageTimeoutAbortIfHung,
            1000,
            out _);
        if (result == 0 && System.Runtime.InteropServices.Marshal.GetLastWin32Error() is int error and not 0)
        {
            throw new Win32Exception(error, "Progman did not respond to the Raised Desktop request.");
        }
    }
}

internal sealed record RaisedDesktopAnalysis(
    RaisedDesktopStructureFingerprint BeforeFingerprint,
    RaisedDesktopStructureFingerprint AfterFingerprint,
    IReadOnlyList<RaisedDesktopRejection> Rejections,
    RaisedDesktopTransientContext? Context);

internal sealed record RaisedDesktopTransientContext(
    ulong ProgmanWindowHandle,
    ulong DefViewWindowHandle,
    ulong WorkerWindowHandle,
    uint ProgmanProcessId,
    int DefViewZOrderIndex,
    int WorkerZOrderIndex);

public sealed record RaisedDesktopDiagnosticResult(
    RaisedDesktopDiagnosticStatus Status,
    bool RequestSent,
    bool SurfaceCreated,
    RaisedDesktopPresentationKind? PresentationKind,
    RaisedDesktopOwnedResourcesCleanup OwnedResourcesCleanup,
    RaisedDesktopShellMutationRecovery ShellMutationRecovery,
    RaisedDesktopStructureFingerprint BeforeFingerprint,
    RaisedDesktopStructureFingerprint AfterFingerprint,
    RaisedDesktopStructureFingerprint CleanupFingerprint,
    IReadOnlyList<RaisedDesktopRejection> Rejections);

public sealed record RaisedDesktopStructureFingerprint(string Value, string Summary);

public sealed record RaisedDesktopRejection(RaisedDesktopRejectionCode Code, string Detail);

public enum RaisedDesktopDiagnosticStatus
{
    Rejected,
    StructuralCandidate,
    PresentationProbeCompleted,
    CleanupIncomplete,
}

public enum RaisedDesktopPresentationKind
{
    LayeredGdi,
    NonLayeredGdi,
    DirectCompositionSwapChain,
}

public enum RaisedDesktopOwnedResourcesCleanup
{
    Complete,
    Incomplete,
}

public enum RaisedDesktopShellMutationRecovery
{
    NoMutationObserved,
    Incomplete,
}

public enum RaisedDesktopSnapshotStage
{
    Before,
    StablePreflight,
    AfterRequest,
    ColorBlockActive,
    AfterCleanup,
}

public enum RaisedDesktopRejectionCode
{
    ShellUnavailable,
    WrongInteractiveDesktop,
    ProgmanNotShellAnchor,
    ProgmanNotRaised,
    DefViewMissing,
    DefViewNotLayered,
    ProcessMismatch,
    WorkerNotCreated,
    WorkerNotDirectChild,
    WorkerHasOwner,
    WorkerBoundsMismatch,
    WorkerZOrderInvalid,
    UnexpectedTopologyMutation,
    SurfaceLayerValidationFailed,
    CompetingDesktopSurface,
    CleanupIncomplete,
}
