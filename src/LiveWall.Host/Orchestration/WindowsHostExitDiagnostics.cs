using LiveWall.Domain.Displays;
using LiveWall.Host.Bootstrap;
using LiveWall.Host.CommandLoop;
using LiveWall.Platform.Windows.Lifecycle;

namespace LiveWall.Host.Orchestration;

public sealed record WindowsHostExitCheckResult(bool Valid, int ActiveSessionCount, bool NativeCloseObserved,
    string? ExitReason, WindowsExitWaitOutcome NativeWaitOutcome, bool ReceiptVerified,
    IReadOnlyList<SessionRetirementResult> Retirement, IReadOnlyList<RendererCleanupEvidence> RendererCleanup,
    bool OwnedResourcesCleaned);

public static partial class WindowsHostEventChainDiagnostics
{
    public static async Task<WindowsHostExitCheckResult> RunActiveHostExitCheckAsync(string rendererPath,
        DisplayTopology topology, ProductCandidateDisplaySelection selection, TimeSpan visibleDuration,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(visibleDuration, TimeSpan.Zero);
        DiagnosticRendererProcessTracker tracker = new();
        var composition = CreateComposition(rendererPath, topology, selection, tracker).Composition;
        HostExitReceipt? receipt = null;
        using HostProcessLifetime lifetime = new(composition.Runtime, value => { receipt = value; return Task.CompletedTask; });
        TaskCompletionSource<Task> nativeObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WindowsProcessExitMonitor monitor = new(request => RequestObservedNativeStop(lifetime, request, nativeObserved));
        try
        {
            await monitor.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            await composition.Runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            EnsureInitialApplySucceeded(composition.Runtime);
            HostStateSnapshot initial = await GetStateAsync(composition.CommandLoop, cancellationToken).ConfigureAwait(false);
            monitor.MarkRunning();
            await Task.Delay(visibleDuration, cancellationToken).ConfigureAwait(false);
            await monitor.RequestCloseForDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            Task nativeStop = await nativeObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            await nativeStop.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(nativeStop, lifetime.RequestStop("DuplicateAfterNativeClose")))
                throw new InvalidOperationException("Duplicate exit requests must share the native stop completion.");
            await monitor.DisposeAsync().ConfigureAwait(false);
            SessionRetirementResult[] retirement = composition.CommandLoop.GetRetirementHistoryForDiagnostics()
                .SelectMany(batch => batch.Sessions).ToArray();
            RendererCleanupEvidence[] cleanup = tracker.Snapshot();
            bool cleaned = composition.DesktopHost.OwnedSurfaceCountForDiagnostics == 0 &&
                cleanup.Length == initial.Sessions.Count && cleanup.All(item => item.ExitObserved && item.HandleDisposed && item.Issue is null);
            bool receiptValid = receipt is { Reason: "WindowClose", RuntimeStopped: true, CleanupSucceeded: true };
            bool valid = initial.Sessions.Count > 0 && lifetime.Reason == "WindowClose" && receiptValid && cleaned &&
                monitor.WaitOutcome == WindowsExitWaitOutcome.Completed && retirement.Length == initial.Sessions.Count &&
                retirement.All(item => item.Graceful && item.ShutdownSent && item.OwnedResourcesCleaned && item.Issues.Count == 0);
            return new(valid, initial.Sessions.Count, nativeObserved.Task.IsCompletedSuccessfully, lifetime.Reason,
                monitor.WaitOutcome, receiptValid, retirement, cleanup, cleaned);
        }
        finally { await composition.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false); }
    }

    internal static Task RequestObservedNativeStop(HostProcessLifetime lifetime, WindowsExitRequest request,
        TaskCompletionSource<Task> observed)
    {
        // Publish only after the native request owns the shared stop and its reason.
        // Waking the waiter first lets a duplicate request win the first-request race.
        Task completion = lifetime.RequestStop(request);
        observed.TrySetResult(completion);
        return completion;
    }
}
