using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveWall.Diagnostics;
using LiveWall.Domain.Displays;
using LiveWall.Host.Orchestration;
using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.DesktopHostDiagnostics;

internal enum ProductCandidateClassification
{
    ValidSuccess,
    ProductFailure,
    EnvironmentBlocked,
    EnvironmentChanged,
    Cancelled,
    HarnessFailure,
    ExpectedFailureValidated,
}

internal sealed record ProductCandidateStructuredResult(
    string SchemaVersion,
    Guid RunId,
    DateTimeOffset StartedAt,
    string FullBuild,
    ProductCandidateExecutionContext Context,
    string DisplayTopologyFingerprint,
    bool DisplayTopologyChanged,
    bool ShellGenerationChanged,
    IReadOnlyList<ProductCandidateStructuredSession> Sessions,
    ProductCandidateApplyResult? Apply,
    ProductCandidateRetirementResult? Retirement,
    ProductCandidateCleanupResult? Cleanup,
    ProductCandidateClassification Classification,
    string? Error,
    ProductCandidateFaultExpectation? Fault = null);

internal sealed record ProductCandidateFaultExpectation(
    int TargetIteration,
    int TargetSurfaceOrdinal,
    string Phase,
    string Action,
    IReadOnlyList<string> TargetDisplayIds,
    string? ActualTerminalPhase,
    string ActualFailureReason,
    int ActiveSessionCount,
    int PreparedSurfaceCount,
    int SuccessfulReplaceCount,
    int SuccessfulCleanupCount,
    bool Validated);

internal sealed record ProductCandidateExecutionContext(
    uint? SessionId,
    string? WindowStation,
    string? Desktop);

internal sealed record ProductCandidateStructuredSession(
    string SessionId,
    string RendererId,
    IReadOnlyList<string> DisplayIds,
    int SurfaceOrdinal,
    int SurfaceCount);

internal sealed record ProductCandidateApplyResult(
    long Generation,
    string TerminalState,
    string? FailurePhase,
    string FailureReason,
    double DeadlineMilliseconds,
    double ElapsedMilliseconds,
    IReadOnlyList<ProductCandidatePhaseTiming> PhaseTimings);

internal sealed record ProductCandidatePhaseTiming(
    string Phase,
    int SurfaceOrdinal,
    IReadOnlyList<string> DisplayIds,
    double ElapsedMilliseconds,
    string Outcome,
    string FailureReason,
    string? Detail);

internal sealed record ProductCandidateRetirementResult(
    bool Graceful,
    bool ShutdownSent,
    bool ShutdownCompleted,
    bool RendererDisposed,
    bool SurfacesCleaned,
    double ElapsedMilliseconds,
    IReadOnlyList<string> Issues,
    IReadOnlyList<ProductCandidateSessionRetirement> Sessions);

internal sealed record ProductCandidateSessionRetirement(
    string SessionId,
    long Generation,
    IReadOnlyList<string> DisplayIds,
    int SurfaceOrdinal,
    double ElapsedMilliseconds,
    bool ShutdownSent,
    bool ShutdownCompleted,
    bool RendererDisposed,
    bool SurfacesCleaned,
    IReadOnlyList<string> Issues);

internal sealed record ProductCandidateCleanupResult(
    bool OwnedWindowsRemain,
    int DirectShellWorkerCountBefore,
    int DirectShellWorkerCountAfter,
    int AddedWorkerCount,
    string ShellMutationRecovery);

internal static class ProductCandidateStructuredResultWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    static ProductCandidateStructuredResultWriter()
    {
        SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static ProductCandidateStructuredResult CreateSuccess(
        Guid runId,
        DateTimeOffset startedAt,
        DisplayTopology topology,
        DisplayTopology afterTopology,
        ShellTopologySnapshot before,
        ShellTopologySnapshot after,
        RendererProductCandidateResult result,
        bool ownedWindowsRemain,
        int workerCountBefore,
        int workerCountAfter,
        int addedWorkerCount)
    {
        bool shellGenerationChanged = HasShellGenerationChanged(before, after);
        bool displayTopologyChanged = !StringComparer.Ordinal.Equals(
            Fingerprint(topology),
            Fingerprint(afterTopology));
        ProductCandidateCleanupResult cleanup = new(
            ownedWindowsRemain,
            workerCountBefore,
            workerCountAfter,
            addedWorkerCount,
            addedWorkerCount == 0 ? "NoNewWorkerObserved" : "CleanupIncomplete");
        bool valid = result.GenerationResult.State == ApplyGenerationTerminalState.Succeeded &&
            result.FirstFrameCommitted &&
            result.RetirementResult.Graceful &&
            result.RetirementResult.OwnedResourcesCleaned &&
            !ownedWindowsRemain &&
            addedWorkerCount == 0 &&
            !shellGenerationChanged &&
            !displayTopologyChanged;
        return new ProductCandidateStructuredResult(
            "1.0",
            runId,
            startedAt,
            before.BuildIdentity.FullBuild,
            ToContext(before),
            Fingerprint(topology),
            displayTopologyChanged,
            shellGenerationChanged,
            Array.AsReadOnly(result.Sessions.Select(session =>
                new ProductCandidateStructuredSession(
                    session.SessionId.Value,
                    session.RendererId.Value,
                    Array.AsReadOnly(session.DisplayIds.Select(id => id.Value).ToArray()),
                    session.SurfaceOrdinal,
                    session.SurfaceCount)).ToArray()),
            ToApply(result.GenerationResult, result.Timeline),
            ToRetirement(result.RetirementResult),
            cleanup,
            shellGenerationChanged || displayTopologyChanged
                ? ProductCandidateClassification.EnvironmentChanged
                : valid
                    ? ProductCandidateClassification.ValidSuccess
                    : ProductCandidateClassification.ProductFailure,
            valid ? null : "The product candidate did not satisfy every reliability invariant.");
    }

    public static ProductCandidateStructuredResult CreateFailure(
        Guid runId,
        DateTimeOffset startedAt,
        DisplayTopology? topology,
        ShellTopologySnapshot? snapshot,
        Exception exception,
        ShellTopologySnapshot? before = null,
        ShellTopologySnapshot? after = null)
    {
        ApplyGenerationResult? generation =
            (exception as RendererProductCandidateFailureException)?.GenerationResult;
        IReadOnlyList<ApplyTimelineEntry> timeline =
            (exception as RendererProductCandidateFailureException)?.Timeline ?? [];
        bool shellGenerationChanged = before is not null && after is not null &&
            HasShellGenerationChanged(before, after);
        ProductCandidateCleanupResult? cleanup = before is null || after is null
            ? null
            : CreateCleanup(before, after);
        bool environmentBlocked = before?.Availability == ShellTopologyAvailability.ShellUnavailable ||
            (before is not null &&
             (!string.Equals(
                  before.ExecutionContext.WindowStationName,
                  "WinSta0",
                  StringComparison.OrdinalIgnoreCase) ||
              !string.Equals(
                  before.ExecutionContext.DesktopName,
                  "Default",
                  StringComparison.OrdinalIgnoreCase)));
        return new ProductCandidateStructuredResult(
            "1.0",
            runId,
            startedAt,
            snapshot?.BuildIdentity.FullBuild ?? "unavailable",
            snapshot is null ? new ProductCandidateExecutionContext(null, null, null) : ToContext(snapshot),
            topology is null ? "unavailable" : Fingerprint(topology),
            false,
            shellGenerationChanged,
            [],
            generation is null ? null : ToApply(generation, timeline),
            null,
            cleanup,
            shellGenerationChanged
                ? ProductCandidateClassification.EnvironmentChanged
                : environmentBlocked
                    ? ProductCandidateClassification.EnvironmentBlocked
                : Classify(exception),
            exception.Message);
    }

    public static ProductCandidateStructuredResult CreateExpectedFailure(
        Guid runId,
        DateTimeOffset startedAt,
        DisplayTopology topology,
        DisplayTopology afterTopology,
        ShellTopologySnapshot before,
        ShellTopologySnapshot after,
        RendererProductCandidateFailureException failure,
        ProbeFaultPlan plan,
        IReadOnlyList<DisplayId> targetDisplayIds,
        bool validated,
        string? validationError)
    {
        ProductCandidateCleanupResult cleanup = CreateCleanup(before, after);
        bool shellGenerationChanged = HasShellGenerationChanged(before, after);
        bool displayTopologyChanged = !StringComparer.Ordinal.Equals(
            Fingerprint(topology),
            Fingerprint(afterTopology));
        int successfulReplacements = failure.Timeline.Count(entry =>
            entry.Phase == ApplyGenerationPhase.SurfacesReplaced &&
            entry.Outcome == ApplyStageOutcome.Success);
        int successfulCleanup = failure.Timeline.Count(entry =>
            entry.Phase == ApplyGenerationPhase.CleanupVerified &&
            entry.Outcome == ApplyStageOutcome.Success);
        bool expectedFailureValidated = validated &&
            !shellGenerationChanged &&
            !displayTopologyChanged &&
            !cleanup.OwnedWindowsRemain &&
            cleanup.AddedWorkerCount == 0;
        bool environmentBlocked = failure.Timeline.Any(entry =>
            entry.Detail?.Contains("CompetingDesktopSurface", StringComparison.Ordinal) == true ||
            entry.Detail?.Contains("ShellUnavailable", StringComparison.Ordinal) == true ||
            entry.Detail?.Contains("WrongInteractiveDesktop", StringComparison.Ordinal) == true);
        return new ProductCandidateStructuredResult(
            "1.0",
            runId,
            startedAt,
            before.BuildIdentity.FullBuild,
            ToContext(before),
            Fingerprint(topology),
            displayTopologyChanged,
            shellGenerationChanged,
            [],
            ToApply(failure.GenerationResult, failure.Timeline),
            null,
            cleanup,
            environmentBlocked
                ? ProductCandidateClassification.EnvironmentBlocked
                : expectedFailureValidated
                ? ProductCandidateClassification.ExpectedFailureValidated
                : ProductCandidateClassification.ProductFailure,
            expectedFailureValidated ? null : validationError,
            new ProductCandidateFaultExpectation(
                plan.TargetIteration,
                plan.TargetSurfaceOrdinal,
                plan.Phase.ToString(),
                plan.Action.ToString(),
                Array.AsReadOnly(targetDisplayIds.Select(id => id.Value).ToArray()),
                failure.GenerationResult.TerminalPhase?.ToString(),
                failure.GenerationResult.FailureReason.ToString(),
                failure.ActiveSessionIds.Count,
                failure.PreparedSurfaceCount,
                successfulReplacements,
                successfulCleanup,
                expectedFailureValidated));
    }

    public static async Task WriteAsync(
        string path,
        ProductCandidateStructuredResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    JsonSerializer.Serialize(result, SerializerOptions),
                    new UTF8Encoding(false),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static ProductCandidateClassification Classify(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return ProductCandidateClassification.Cancelled;
        }
        if (exception is FileNotFoundException or ArgumentException)
        {
            return ProductCandidateClassification.HarnessFailure;
        }
        if (exception is DesktopAttachPointUnavailableException &&
            (exception.Message.Contains("CompetingDesktopSurface", StringComparison.Ordinal) ||
             exception.Message.Contains("ShellUnavailable", StringComparison.Ordinal) ||
             exception.Message.Contains("WrongInteractiveDesktop", StringComparison.Ordinal)))
        {
            return ProductCandidateClassification.EnvironmentBlocked;
        }
        return ProductCandidateClassification.ProductFailure;
    }

    private static ProductCandidateCleanupResult CreateCleanup(
        ShellTopologySnapshot before,
        ShellTopologySnapshot after)
    {
        HashSet<ulong> beforeWorkers = DirectShellWorkers(before);
        HashSet<ulong> afterWorkers = DirectShellWorkers(after);
        int addedWorkers = afterWorkers.Except(beforeWorkers).Count();
        bool ownedWindowsRemain = after.ShellDescendants.Any(window =>
            window.ClassName.StartsWith(
                RaisedDesktopHostSurfaceLease.WindowClassPrefix,
                StringComparison.Ordinal) ||
            window.ClassName.StartsWith(
                RendererChildDirectCompositionSession.WindowClassPrefix,
                StringComparison.Ordinal));
        return new ProductCandidateCleanupResult(
            ownedWindowsRemain,
            beforeWorkers.Count,
            afterWorkers.Count,
            addedWorkers,
            addedWorkers == 0 ? "NoNewWorkerObserved" : "CleanupIncomplete");
    }

    private static HashSet<ulong> DirectShellWorkers(ShellTopologySnapshot snapshot) =>
        snapshot.ShellDescendants
            .Where(window =>
                window.ParentWindowHandle == snapshot.ShellWindowHandle &&
                StringComparer.Ordinal.Equals(window.ClassName, "WorkerW"))
            .Select(window => window.WindowHandle)
            .ToHashSet();

    private static ProductCandidateExecutionContext ToContext(ShellTopologySnapshot snapshot) => new(
        snapshot.ExecutionContext.SessionId,
        snapshot.ExecutionContext.WindowStationName,
        snapshot.ExecutionContext.DesktopName);

    private static ProductCandidateApplyResult ToApply(
        ApplyGenerationResult result,
        IReadOnlyList<ApplyTimelineEntry> timeline) => new(
        result.Generation,
        result.State.ToString(),
        result.TerminalPhase?.ToString(),
        result.FailureReason.ToString(),
        result.TotalDeadline.TotalMilliseconds,
        result.Elapsed.TotalMilliseconds,
        Array.AsReadOnly(timeline.Select(entry => new ProductCandidatePhaseTiming(
            entry.Phase.ToString(),
            entry.SurfaceOrdinal,
            Array.AsReadOnly(entry.DisplayIds.Select(id => id.Value).ToArray()),
            entry.Elapsed.TotalMilliseconds,
            entry.Outcome.ToString(),
            entry.FailureReason.ToString(),
            entry.Detail)).ToArray()));

    private static ProductCandidateRetirementResult ToRetirement(
        SessionRetirementBatchResult result) => new(
        result.Graceful,
        result.Sessions.All(session => session.ShutdownSent),
        result.Sessions.All(session => session.ShutdownCompleted),
        result.Sessions.All(session => session.RendererDisposed),
        result.Sessions.All(session => session.SurfacesCleaned),
        result.Sessions.Count == 0 ? 0 : result.Sessions.Max(session => session.Elapsed.TotalMilliseconds),
        Array.AsReadOnly(result.Sessions.SelectMany(session => session.Issues)
            .Select(issue => $"{issue.Kind}:{issue.Detail}")
            .ToArray()),
        Array.AsReadOnly(result.Sessions.Select(session =>
            new ProductCandidateSessionRetirement(
                session.SessionId.Value,
                session.Generation,
                Array.AsReadOnly(session.DisplayIds.Select(id => id.Value).ToArray()),
                session.SurfaceOrdinal,
                session.Elapsed.TotalMilliseconds,
                session.ShutdownSent,
                session.ShutdownCompleted,
                session.RendererDisposed,
                session.SurfacesCleaned,
                Array.AsReadOnly(session.Issues.Select(issue =>
                    $"{issue.Kind}:{issue.Detail}").ToArray())))
            .ToArray()));

    private static bool HasShellGenerationChanged(
        ShellTopologySnapshot before,
        ShellTopologySnapshot after)
    {
        uint? beforeProcess = before.TopLevelWindows
            .FirstOrDefault(window => window.WindowHandle == before.ShellWindowHandle)?.ProcessId;
        uint? afterProcess = after.TopLevelWindows
            .FirstOrDefault(window => window.WindowHandle == after.ShellWindowHandle)?.ProcessId;
        return before.ShellWindowHandle != after.ShellWindowHandle || beforeProcess != afterProcess;
    }

    private static string Fingerprint(DisplayTopology topology)
    {
        string canonical = string.Join("\n", topology.Displays
            .OrderBy(display => display.Id.Value, StringComparer.Ordinal)
            .Select(display => string.Join("|",
                display.Id.Value,
                display.DevicePath,
                display.Bounds.X,
                display.Bounds.Y,
                display.Bounds.Width,
                display.Bounds.Height,
                display.ScaleFactor.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                display.RefreshRateHz,
                display.IsPrimary)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
