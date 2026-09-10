using System.Diagnostics;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;

namespace LiveWall.Host.Orchestration;

public enum ApplyGenerationPhase
{
    TopologyReady,
    SurfaceCreated,
    ProcessStarted,
    PipeConnected,
    HelloReceived,
    Initialized,
    AttachSent,
    SurfaceAttached,
    LoadSent,
    ContentLoaded,
    FirstFramePresented,
    SurfacesReplaced,
    ShutdownCompleted,
    CleanupVerified,
}

public enum ApplyStageOutcome
{
    Success,
    Timeout,
    Rejected,
    Cancelled,
}

public enum ApplyGenerationTerminalState
{
    Succeeded,
    Failed,
    Cancelled,
    CircuitBroken,
}

public enum ApplyFailureReason
{
    None,
    DeadlineExceeded,
    Superseded,
    TopologyFailed,
    SurfaceCreationFailed,
    ProcessStartFailed,
    PipeConnectionFailed,
    HelloInvalid,
    InitializationFailed,
    AttachWriteFailed,
    SurfaceAttachedTimeout,
    SurfaceAttachmentRejected,
    LoadWriteFailed,
    ContentLoadedTimeout,
    ContentLoadRejected,
    FirstFrameTimeout,
    FirstFrameRejected,
    ReplaceFailed,
    CleanupFailed,
    Cancelled,
    Unexpected,
}

public sealed record ApplyTimelineEntry(
    long Generation,
    IReadOnlyList<DisplayId> DisplayIds,
    int SurfaceOrdinal,
    ApplyGenerationPhase Phase,
    TimeSpan Elapsed,
    ApplyStageOutcome Outcome,
    ApplyFailureReason FailureReason,
    string? Detail);

public sealed record ApplyGenerationResult(
    long Generation,
    ApplyGenerationTerminalState State,
    ApplyGenerationPhase? TerminalPhase,
    ApplyFailureReason FailureReason,
    string? Detail,
    TimeSpan TotalDeadline,
    TimeSpan Elapsed,
    IReadOnlyList<ApplyTimelineEntry> Timeline);

internal sealed record ApplyDeadlineBudget(
    TimeSpan RendererHandshake,
    TimeSpan SurfaceAttach,
    TimeSpan LoadAndFirstFrame,
    TimeSpan Replace,
    TimeSpan Cleanup,
    TimeSpan ShellStability)
{
    public static ApplyDeadlineBudget Default { get; } = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(2));

    public TimeSpan Calculate(int surfaceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(surfaceCount);
        long perSurfaceTicks = checked(
            RendererHandshake.Ticks + SurfaceAttach.Ticks + LoadAndFirstFrame.Ticks);
        return TimeSpan.FromTicks(checked(
            perSurfaceTicks * surfaceCount +
            Replace.Ticks +
            Cleanup.Ticks +
            ShellStability.Ticks));
    }
}

internal sealed class ApplyGenerationOperation : IDisposable
{
    private readonly Lock gate = new();
    private readonly long startedTimestamp = Stopwatch.GetTimestamp();
    private readonly ApplyDeadlineLease deadline;
    private readonly List<ApplyTimelineEntry> timeline = [];
    private readonly TaskCompletionSource<ApplyGenerationResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ApplyGenerationPhase? currentPhase;

    public ApplyGenerationOperation(long generation, int surfaceCount, TimeSpan totalDeadline)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(surfaceCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalDeadline, TimeSpan.Zero);
        Generation = generation;
        SurfaceCount = surfaceCount;
        TotalDeadline = totalDeadline;
        deadline = new ApplyDeadlineLease(totalDeadline);
    }

    public long Generation { get; }

    public int SurfaceCount { get; }

    public TimeSpan TotalDeadline { get; }

    public Task<ApplyGenerationResult> Completion => completion.Task;

    public CancellationToken DeadlineToken => deadline.Token;

    internal bool IsDeadlineDisposed => deadline.IsDisposed;

    public ApplyGenerationPhase? CurrentPhase
    {
        get
        {
            lock (gate)
            {
                return currentPhase;
            }
        }
    }

    public void BeginPhase(ApplyGenerationPhase phase)
    {
        lock (gate)
        {
            currentPhase = phase;
        }
    }

    public void Record(
        ApplyGenerationPhase phase,
        IReadOnlyList<DisplayId>? displayIds = null,
        int surfaceOrdinal = 0,
        ApplyStageOutcome outcome = ApplyStageOutcome.Success,
        ApplyFailureReason failureReason = ApplyFailureReason.None,
        string? detail = null,
        long? timestamp = null)
    {
        long observedTimestamp = timestamp ?? Stopwatch.GetTimestamp();
        lock (gate)
        {
            currentPhase = phase;
            timeline.Add(new ApplyTimelineEntry(
                Generation,
                Array.AsReadOnly(displayIds?.ToArray() ?? []),
                surfaceOrdinal,
                phase,
                Stopwatch.GetElapsedTime(startedTimestamp, observedTimestamp),
                outcome,
                failureReason,
                detail));
        }
    }

    public IReadOnlyList<ApplyTimelineEntry> Snapshot()
    {
        lock (gate)
        {
            return Array.AsReadOnly(timeline.ToArray());
        }
    }

    public void Succeed() => Complete(
        ApplyGenerationTerminalState.Succeeded,
        ApplyGenerationPhase.SurfacesReplaced,
        ApplyFailureReason.None,
        null);

    public void Fail(
        ApplyGenerationPhase? phase,
        ApplyFailureReason reason,
        string? detail,
        ApplyGenerationTerminalState state = ApplyGenerationTerminalState.Failed) =>
        Complete(state, phase, reason, detail);

    public void Dispose() => deadline.Dispose();

    private void Complete(
        ApplyGenerationTerminalState state,
        ApplyGenerationPhase? phase,
        ApplyFailureReason reason,
        string? detail)
    {
        ApplyGenerationResult result;
        lock (gate)
        {
            result = new ApplyGenerationResult(
                Generation,
                state,
                phase,
                reason,
                detail,
                TotalDeadline,
                Stopwatch.GetElapsedTime(startedTimestamp),
                Array.AsReadOnly(timeline.ToArray()));
        }

        if (completion.TrySetResult(result))
        {
            deadline.Dispose();
        }
    }
}

internal sealed class ApplyDeadlineLease : IDisposable
{
    private readonly CancellationTokenSource source = new();
    private readonly CancellationToken token;
    private int disposed;

    public ApplyDeadlineLease(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        token = source.Token;
        source.CancelAfter(duration);
    }

    public CancellationToken Token => token;

    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            source.Dispose();
        }
    }
}

internal sealed class ApplyStageException : Exception
{
    public ApplyStageException(
        ApplyGenerationPhase phase,
        ApplyFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Phase = phase;
        Reason = reason;
    }

    public ApplyGenerationPhase Phase { get; }

    public ApplyFailureReason Reason { get; }
}

internal sealed record RendererProcessMilestone(
    ApplyGenerationPhase Phase,
    ApplyStageOutcome Outcome,
    ApplyFailureReason FailureReason,
    string? Detail,
    long Timestamp,
    bool IsPhaseStart = false);

internal interface IObservableRendererProvider
{
    Task<IRendererSession> CreateObservedAsync(
        RendererLaunchContext context,
        Action<RendererProcessMilestone> observer,
        CancellationToken cancellationToken);
}
