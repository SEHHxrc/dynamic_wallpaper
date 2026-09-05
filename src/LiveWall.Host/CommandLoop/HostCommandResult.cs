namespace LiveWall.Host.CommandLoop;

internal sealed record HostCommandResult(
    bool Accepted,
    long StateRevision,
    string? ErrorCode = null,
    HostStateSnapshot? State = null)
{
    public static HostCommandResult Success(long revision, HostStateSnapshot? state = null) =>
        new(true, revision, State: state);

    public static HostCommandResult Reject(long revision, string errorCode) =>
        new(false, revision, errorCode);
}
