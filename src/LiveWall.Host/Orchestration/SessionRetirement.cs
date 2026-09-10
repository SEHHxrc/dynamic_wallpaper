using System.Diagnostics;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Sessions;

namespace LiveWall.Host.Orchestration;

public enum SessionRetirementIssueKind
{
    ShutdownCancelled,
    ShutdownTimeout,
    ShutdownFailed,
    RendererDisposeTimeout,
    RendererDisposeFailed,
    SurfaceCleanupFailed,
}

public sealed record SessionRetirementIssue(
    SessionRetirementIssueKind Kind,
    string Detail);

public sealed record SessionRetirementResult(
    SessionId SessionId,
    long Generation,
    IReadOnlyList<DisplayId> DisplayIds,
    int SurfaceOrdinal,
    TimeSpan Elapsed,
    bool ShutdownSent,
    bool ShutdownCompleted,
    bool RendererDisposed,
    bool SurfacesCleaned,
    IReadOnlyList<SessionRetirementIssue> Issues)
{
    public bool Graceful => ShutdownCompleted;

    public bool OwnedResourcesCleaned => RendererDisposed && SurfacesCleaned;
}

public sealed record SessionRetirementBatchResult(
    IReadOnlyList<SessionRetirementResult> Sessions)
{
    public bool Graceful => Sessions.All(session => session.Graceful);

    public bool OwnedResourcesCleaned =>
        Sessions.All(session => session.OwnedResourcesCleaned);
}

internal sealed record SessionRetirementPolicy(
    TimeSpan GracefulShutdown,
    TimeSpan RendererDispose)
{
    public static SessionRetirementPolicy Default { get; } = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10));

    public SessionRetirementPolicy Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(GracefulShutdown, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RendererDispose, TimeSpan.Zero);
        return this;
    }
}

internal static class SessionRetirementClock
{
    public static TimeSpan Elapsed(long startedTimestamp) =>
        Stopwatch.GetElapsedTime(startedTimestamp);
}
