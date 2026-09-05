namespace LiveWall.Application.Sessions;

public interface IProcessSupervisor
{
    Task<SupervisedProcess> StartAsync(
        ProcessLaunchSpec spec,
        CancellationToken cancellationToken);

    Task StopAsync(
        ProcessId processId,
        ShutdownMode mode,
        CancellationToken cancellationToken);

    event EventHandler<SupervisedProcessExitedEventArgs> ProcessExited;
}

public readonly record struct ProcessId(int Value);

public sealed record ProcessLaunchSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

public sealed record SupervisedProcess(ProcessId Id, DateTimeOffset StartedAt);

public enum ShutdownMode
{
    Graceful,
    ForceAfterTimeout,
    Force,
}

public sealed class SupervisedProcessExitedEventArgs(
    ProcessId processId,
    int exitCode,
    DateTimeOffset exitedAt) : EventArgs
{
    public ProcessId ProcessId { get; } = processId;

    public int ExitCode { get; } = exitCode;

    public DateTimeOffset ExitedAt { get; } = exitedAt;
}

