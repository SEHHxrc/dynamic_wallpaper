using System.Diagnostics;
using LiveWall.Domain.Displays;

namespace LiveWall.Host.Orchestration;

public enum DesktopRecoveryTerminalState
{
    Succeeded,
    Failed,
    Cancelled,
    CircuitBroken,
}

public enum DesktopRecoveryCompletionMode
{
    Reattached,
    Rebuilt,
}

public enum DesktopRecoveryPhase
{
    ShellGenerationObserved,
    TopologyReady,
    SurfaceCreated,
    SetBoundsSent,
    AttachSent,
    SurfaceAttached,
    FirstFramePresented,
    SurfacesReplaced,
    BatchAbandoned,
    RebuildScheduled,
    RebuildStarted,
    CleanupVerified,
}

public sealed record DesktopRecoveryTimelineEntry(
    long RecoveryId,
    long HostGeneration,
    int Attempt,
    int SurfaceOrdinal,
    IReadOnlyList<DisplayId> DisplayIds,
    DesktopRecoveryPhase Phase,
    ApplyStageOutcome Outcome,
    ApplyFailureReason FailureReason,
    TimeSpan Elapsed,
    string? Detail);

public sealed record DesktopRecoveryResult(
    long RecoveryId,
    long FinalHostGeneration,
    DesktopRecoveryTerminalState TerminalState,
    DesktopRecoveryCompletionMode? CompletionMode,
    ApplyFailureReason FailureReason,
    string? Detail,
    TimeSpan Elapsed,
    IReadOnlyList<DesktopRecoveryTimelineEntry> Timeline);

internal sealed record DesktopRecoveryMilestone(
    long HostGeneration,
    int SurfaceOrdinal,
    IReadOnlyList<DisplayId> DisplayIds,
    DesktopRecoveryPhase Phase,
    ApplyStageOutcome Outcome = ApplyStageOutcome.Success,
    ApplyFailureReason FailureReason = ApplyFailureReason.None,
    string? Detail = null);

internal sealed class DesktopRecoveryStageException : Exception
{
    public DesktopRecoveryStageException(
        DesktopRecoveryPhase phase,
        ApplyFailureReason reason,
        string message,
        Exception innerException,
        int surfaceOrdinal = 0,
        IReadOnlyList<DisplayId>? displayIds = null)
        : base(message, innerException)
    {
        Phase = phase;
        Reason = reason;
        SurfaceOrdinal = surfaceOrdinal;
        DisplayIds = Array.AsReadOnly(displayIds?.ToArray() ?? []);
    }

    public DesktopRecoveryPhase Phase { get; }

    public ApplyFailureReason Reason { get; }

    public int SurfaceOrdinal { get; }

    public IReadOnlyList<DisplayId> DisplayIds { get; }
}

internal sealed class DesktopRecoveryOperation
{
    private readonly object gate = new();
    private readonly long startedTimestamp = Stopwatch.GetTimestamp();
    private readonly List<DesktopRecoveryTimelineEntry> timeline = [];
    private readonly TaskCompletionSource<DesktopRecoveryResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DesktopRecoveryOperation(long recoveryId)
    {
        RecoveryId = recoveryId;
    }

    public long RecoveryId { get; }

    public Task<DesktopRecoveryResult> Completion => completion.Task;

    public void Record(
        long hostGeneration,
        int attempt,
        DesktopRecoveryPhase phase,
        IReadOnlyList<DisplayId>? displayIds = null,
        int surfaceOrdinal = 0,
        ApplyStageOutcome outcome = ApplyStageOutcome.Success,
        ApplyFailureReason failureReason = ApplyFailureReason.None,
        string? detail = null)
    {
        lock (gate)
        {
            timeline.Add(new DesktopRecoveryTimelineEntry(
                RecoveryId,
                hostGeneration,
                attempt,
                surfaceOrdinal,
                Array.AsReadOnly(displayIds?.ToArray() ?? []),
                phase,
                outcome,
                failureReason,
                Stopwatch.GetElapsedTime(startedTimestamp),
                detail));
        }
    }

    public IReadOnlyList<DesktopRecoveryTimelineEntry> Snapshot()
    {
        lock (gate)
        {
            return Array.AsReadOnly(timeline.ToArray());
        }
    }

    public void Complete(
        long finalHostGeneration,
        DesktopRecoveryTerminalState terminalState,
        DesktopRecoveryCompletionMode? completionMode,
        ApplyFailureReason failureReason = ApplyFailureReason.None,
        string? detail = null)
    {
        DesktopRecoveryResult result;
        lock (gate)
        {
            result = new DesktopRecoveryResult(
                RecoveryId,
                finalHostGeneration,
                terminalState,
                completionMode,
                failureReason,
                detail,
                Stopwatch.GetElapsedTime(startedTimestamp),
                Array.AsReadOnly(timeline.ToArray()));
        }
        completion.TrySetResult(result);
    }
}
