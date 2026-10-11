namespace LiveWall.Host.Orchestration;

internal enum SessionEnvironmentPath
{
    Resume,
    Reconfigure,
    Rebuild,
}

internal enum SessionEnvironmentTerminalState
{
    Succeeded,
    Failed,
    Cancelled,
}
internal enum SessionEnvironmentFailureReason
{
    None,
    SessionDisconnected,
    Superseded,
    HostStopping,
    ValidationFailed,
    ValidationTimeout,
    RecoveryRejected,
    ReconciliationRejected,
    RecoveryFailed,
    ReconciliationFailed,
}

internal sealed record SessionEnvironmentResult(
    long ValidationRevision,
    SessionEnvironmentTerminalState State,
    SessionEnvironmentPath? Path,
    SessionEnvironmentFailureReason Reason,
    TimeSpan Elapsed);

internal sealed class SessionEnvironmentOperation(long validationRevision)
{
    private readonly long startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly TaskCompletionSource<SessionEnvironmentResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long ValidationRevision { get; } = validationRevision;
    public Task<SessionEnvironmentResult> Completion => completion.Task;

    public void Complete(SessionEnvironmentTerminalState state,
        SessionEnvironmentPath? path = null, SessionEnvironmentFailureReason reason = SessionEnvironmentFailureReason.None) =>
        completion.TrySetResult(new SessionEnvironmentResult(ValidationRevision, state, path, reason,
            System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp)));
}
