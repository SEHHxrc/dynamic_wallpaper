using LiveWall.Platform.Windows.Lifecycle;

namespace LiveWall.Host.Bootstrap;

internal sealed record HostExitReceipt(int SchemaVersion, string RunId, DateTimeOffset RecordedAt,
    string Reason, bool RuntimeStopped, bool CleanupSucceeded, string? FailureType);

internal sealed record HostNativeExitReceipt(int SchemaVersion, string RunId, DateTimeOffset RecordedAt,
    string Phase, string ObservedReason, string? StopReason, bool Critical, string CleanupOutcome,
    double ElapsedMilliseconds, double TotalBudgetMilliseconds, bool RuntimeStopped, bool CleanupSucceeded,
    string? FailureType, bool CallbackReturnObserved = false, bool ProcessExitObserved = false);

internal sealed class HostProcessLifetime(WindowsHostRuntime runtime,
    Func<HostExitReceipt, Task>? persistReceipt = null,
    Func<HostNativeExitReceipt, CancellationToken, Task>? persistNativeReceipt = null) : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool requested;
    private readonly string runId = Guid.NewGuid().ToString("N");
    public CancellationToken Token => cancellation.Token;
    public bool IsStopRequested { get { lock (gate) { return requested; } } }
    public string? Reason { get; private set; }

    public Task RequestStop(WindowsExitRequest request) => RequestStop(request.Reason.ToString());

    public Task PersistNativeObservationAsync(WindowsExitObservation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (persistNativeReceipt is null) return Task.CompletedTask;
        return persistNativeReceipt(new(1, runId, DateTimeOffset.UtcNow, "NativeCallbackCleanupObserved",
            observation.Request.Reason.ToString(), Reason, observation.Request.Critical,
            observation.CleanupOutcome.ToString(), observation.Elapsed.TotalMilliseconds,
            observation.TotalBudget.TotalMilliseconds, runtime.State == WindowsHostRuntimeState.Stopped,
            observation.CleanupOutcome == WindowsExitWaitOutcome.Completed && runtime.LastFault is null,
            runtime.LastFault?.GetType().Name), cancellationToken);
    }

    public Task RequestStop(string reason)
    {
        lock (gate)
        {
            if (requested) return stopped.Task;
            requested = true;
            Reason = reason;
            _ = StopAsync();
            return stopped.Task;
        }
    }

    private async Task StopAsync()
    {
        try
        {
            Task cleanup = runtime.StopAsync(CancellationToken.None);
            cancellation.Cancel();
            await cleanup.ConfigureAwait(false);
            if (persistReceipt is not null)
                await persistReceipt(new(1, runId, DateTimeOffset.UtcNow, Reason ?? "Unknown",
                    runtime.State == WindowsHostRuntimeState.Stopped, runtime.LastFault is null,
                    runtime.LastFault?.GetType().Name)).ConfigureAwait(false);
            if (runtime.LastFault is not null) throw new InvalidOperationException("Host exit cleanup failed.", runtime.LastFault);
            stopped.TrySetResult();
        }
        catch (Exception exception) { stopped.TrySetException(exception); }
    }

    public void Dispose() => cancellation.Dispose();
}
