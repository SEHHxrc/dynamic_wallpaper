using System.Diagnostics;
using LiveWall.Domain.Displays;

namespace LiveWall.Host.Orchestration;

public enum DisplayReconciliationTerminalState
{
    Succeeded,
    Failed,
    Cancelled,
}

public enum DisplayReconciliationPhase
{
    PlanCreated,
    ActiveSurfacesResized,
    PreparationStarted,
    PreparationRetryScheduled,
    PreparationRetryStarted,
    SurfacesReplaced,
    RetirementScheduled,
    Completed,
}

public sealed record DisplayReconciliationTimelineEntry(
    long TopologyRevision,
    long? HostGeneration,
    DisplayReconciliationPhase Phase,
    ApplyStageOutcome Outcome,
    ApplyFailureReason FailureReason,
    TimeSpan Elapsed,
    IReadOnlyList<DisplayId> DisplayIds,
    string? Detail);

public sealed record DisplayReconciliationResult(
    long TopologyRevision,
    long? HostGeneration,
    DisplayReconciliationTerminalState TerminalState,
    DisplayReconciliationKind Kind,
    ApplyFailureReason FailureReason,
    string? Detail,
    TimeSpan Elapsed,
    IReadOnlyList<DisplayReconciliationTimelineEntry> Timeline);

internal sealed class DisplayReconciliationOperation
{
    private readonly object gate = new();
    private readonly long startedTimestamp = Stopwatch.GetTimestamp();
    private readonly List<DisplayReconciliationTimelineEntry> timeline = [];
    private readonly TaskCompletionSource<DisplayReconciliationResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DisplayReconciliationOperation(
        long topologyRevision,
        DisplayReconciliationKind kind)
    {
        TopologyRevision = topologyRevision;
        Kind = kind;
    }

    public long TopologyRevision { get; }

    public DisplayReconciliationKind Kind { get; }

    public Task<DisplayReconciliationResult> Completion => completion.Task;

    public void Record(
        DisplayReconciliationPhase phase,
        long? hostGeneration = null,
        IReadOnlyList<DisplayId>? displayIds = null,
        ApplyStageOutcome outcome = ApplyStageOutcome.Success,
        ApplyFailureReason failureReason = ApplyFailureReason.None,
        string? detail = null)
    {
        lock (gate)
        {
            timeline.Add(new DisplayReconciliationTimelineEntry(
                TopologyRevision,
                hostGeneration,
                phase,
                outcome,
                failureReason,
                Stopwatch.GetElapsedTime(startedTimestamp),
                Array.AsReadOnly(displayIds?.ToArray() ?? []),
                detail));
        }
    }

    public void Complete(
        DisplayReconciliationTerminalState state,
        long? hostGeneration,
        ApplyFailureReason failureReason = ApplyFailureReason.None,
        string? detail = null)
    {
        lock (gate)
        {
            if (completion.Task.IsCompleted)
            {
                return;
            }
            if (state == DisplayReconciliationTerminalState.Succeeded)
            {
                timeline.Add(new DisplayReconciliationTimelineEntry(
                    TopologyRevision,
                    hostGeneration,
                    DisplayReconciliationPhase.Completed,
                    ApplyStageOutcome.Success,
                    ApplyFailureReason.None,
                    Stopwatch.GetElapsedTime(startedTimestamp),
                    [],
                    detail));
            }
            DisplayReconciliationResult result = new(
                TopologyRevision,
                hostGeneration,
                state,
                Kind,
                failureReason,
                detail,
                Stopwatch.GetElapsedTime(startedTimestamp),
                Array.AsReadOnly(timeline.ToArray()));
            completion.TrySetResult(result);
        }
    }
}
