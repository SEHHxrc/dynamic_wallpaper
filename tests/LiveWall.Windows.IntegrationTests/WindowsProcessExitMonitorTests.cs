using FluentAssertions;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Lifecycle;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Windows.IntegrationTests;

public sealed class WindowsProcessExitMonitorTests
{
    [Fact]
    public void QueryAndCancelledEndSessionDoNotRequestExit()
    {
        int calls = 0;
        SessionEndMessageHandler handler = new(_ => { calls++; return Task.CompletedTask; }, TimeSpan.FromSeconds(1));
        handler.TryHandle(SessionEndMessageHandler.QueryEndSession, 0, 0, out nint consent).Should().BeTrue();
        consent.Should().Be(1);
        handler.TryHandle(SessionEndMessageHandler.EndSession, 0, 0, out _).Should().BeTrue();
        calls.Should().Be(0);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.None);
    }

    [Theory]
    [InlineData(0U, WindowsExitReason.SessionShutdown, false)]
    [InlineData(0x80000000U, WindowsExitReason.SessionLogoff, false)]
    [InlineData(1U, WindowsExitReason.SessionCloseApp, false)]
    [InlineData(0xC0000001U, WindowsExitReason.SessionLogoff, true)]
    public void ConfirmedSessionEndDecodesFlagsAndCoalescesRepeatedMessages(uint flags, WindowsExitReason reason, bool critical)
    {
        List<WindowsExitRequest> requests = [];
        SessionEndMessageHandler handler = new(request => { requests.Add(request); return Task.CompletedTask; }, TimeSpan.FromSeconds(1));
        handler.TryHandle(SessionEndMessageHandler.EndSession, 1, unchecked((nint)(long)flags), out _);
        handler.TryHandle(SessionEndMessageHandler.EndSession, 1, 0, out _);
        handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _);
        requests.Should().Equal(new WindowsExitRequest(reason, critical));
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
    }

    [Fact]
    public void NativeWaitIsBoundedWithoutCancellingSharedCleanup()
    {
        TaskCompletionSource cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionEndMessageHandler handler = new(_ => cleanup.Task, TimeSpan.FromMilliseconds(10));
        handler.TryHandle(SessionEndMessageHandler.EndSession, 1, 0, out _);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.TimedOut);
        cleanup.Task.IsCompleted.Should().BeFalse();
        cleanup.TrySetResult();
        handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.TimedOut, "a duplicate must not erase the original native deadline failure");
    }

    [Fact]
    public void ObservationIsPersistedOnceAfterCleanupAndBeforeNativeHandlerReturns()
    {
        int cleanups = 0;
        int writes = 0;
        WindowsExitObservation? saved = null;
        SessionEndMessageHandler handler = new(_ => { cleanups++; return Task.CompletedTask; }, TimeSpan.FromSeconds(1),
            (observation, token) =>
            {
                token.IsCancellationRequested.Should().BeFalse();
                cleanups.Should().Be(1);
                saved = observation;
                Interlocked.Increment(ref writes);
                return Task.CompletedTask;
            });
        handler.TryHandle(SessionEndMessageHandler.QueryEndSession, 0, 0, out _);
        handler.TryHandle(SessionEndMessageHandler.EndSession, 0, 0, out _);
        writes.Should().Be(0);
        handler.TryHandle(SessionEndMessageHandler.EndSession, 1, unchecked((nint)(long)0x80000000U), out _);
        saved!.Request.Reason.Should().Be(WindowsExitReason.SessionLogoff);
        saved.CleanupOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
        saved.Elapsed.Should().BeLessThan(saved.TotalBudget);
        handler.PersistenceOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
        handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _);
        writes.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObservationFailureIsSeparateFromSuccessfulCleanupAndCannotEscapeNativeCallback(bool synchronous)
    {
        int writes = 0;
        SessionEndMessageHandler handler = new(_ => Task.CompletedTask, TimeSpan.FromSeconds(1), (_, _) =>
        {
            Interlocked.Increment(ref writes);
            if (synchronous) throw new IOException("Synthetic observation write failure.");
            return Task.FromException(new IOException("Synthetic observation write failure."));
        });
        handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _);
        handler.CleanupOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
        handler.PersistenceOutcome.Should().Be(WindowsExitWaitOutcome.Failed);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.Failed);
        handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _);
        writes.Should().Be(1);
    }

    [Fact]
    public async Task HungObservationIsBoundedCancelledAndNeverRetriedByDuplicateSignal()
    {
        int writes = 0;
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionEndMessageHandler handler = new(_ => Task.CompletedTask, TimeSpan.FromMilliseconds(250), async (_, token) =>
        {
            Interlocked.Increment(ref writes);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
        });
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _);
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        handler.CleanupOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.TimedOut);
        handler.TryHandle(SessionEndMessageHandler.EndSession, 1, 0, out _);
        writes.Should().Be(1);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.TimedOut);
    }

    [Fact]
    public void CleanupTimeoutStillRecordsTimeoutObservationWithoutCancellingCleanup()
    {
        TaskCompletionSource cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WindowsExitObservation? saved = null;
        SessionEndMessageHandler handler = new(_ => cleanup.Task, TimeSpan.FromMilliseconds(400), (observation, _) =>
        {
            saved = observation;
            return Task.CompletedTask;
        });
        handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _);
        saved!.CleanupOutcome.Should().Be(WindowsExitWaitOutcome.TimedOut);
        handler.PersistenceOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.TimedOut);
        cleanup.Task.IsCompleted.Should().BeFalse();
        cleanup.TrySetResult();
    }

    [Fact]
    public void CleanupFailureStillRecordsFailedObservation()
    {
        WindowsExitObservation? saved = null;
        SessionEndMessageHandler handler = new(_ => Task.FromException(new IOException("Synthetic cleanup failure.")),
            TimeSpan.FromSeconds(1), (observation, _) => { saved = observation; return Task.CompletedTask; });
        handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _);
        saved!.CleanupOutcome.Should().Be(WindowsExitWaitOutcome.Failed);
        handler.PersistenceOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.Failed);
    }

    [Fact]
    public async Task NativeHandlerDoesNotReturnSuccessBeforeObservationWriteCompletes()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionEndMessageHandler handler = new(_ => Task.CompletedTask, TimeSpan.FromSeconds(2), async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        Task callback = Task.Run(() => handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            callback.IsCompleted.Should().BeFalse();
            handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.None);
            release.TrySetResult();
            await callback.WaitAsync(TimeSpan.FromSeconds(2));
            handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
            handler.PersistenceOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public void SynchronouslyBlockedObservationCannotBlockNativeThreadOrExtendDuplicateBudget()
    {
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim finished = new();
        SessionEndMessageHandler handler = new(_ => Task.CompletedTask, TimeSpan.FromMilliseconds(300), (_, token) =>
        {
            entered.Set();
            try { release.Wait(CancellationToken.None); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            finally { finished.Set(); }
        });
        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _);
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
            entered.IsSet.Should().BeTrue();
            handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.TimedOut);
            elapsed.Restart();
            handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _);
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200));
            handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.TimedOut);
        }
        finally { release.Set(); finished.Wait(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task ConcurrentNativeSignalsPersistOnlyTheFirstRequestObservation()
    {
        int calls = 0;
        int writes = 0;
        WindowsExitRequest? first = null;
        WindowsExitObservation? saved = null;
        SessionEndMessageHandler handler = new(request =>
        {
            first = request;
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(2), (observation, _) =>
        {
            saved = observation;
            Interlocked.Increment(ref writes);
            return Task.CompletedTask;
        });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() => handler.WaitForExit(
            new(index % 2 == 0 ? WindowsExitReason.SessionLogoff : WindowsExitReason.ConsoleClose)))));
        calls.Should().Be(1);
        writes.Should().Be(1);
        saved!.Request.Should().Be(first);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackFailuresCannotEscapeWndProcOrRestartCleanup(bool synchronous)
    {
        int calls = 0;
        SessionEndMessageHandler handler = new(_ =>
        {
            calls++;
            if (synchronous) throw new InvalidOperationException("Synthetic cleanup failure.");
            return Task.FromException(new InvalidOperationException("Synthetic cleanup failure."));
        }, TimeSpan.FromSeconds(1));
        handler.TryHandle(SessionEndMessageHandler.EndSession, 1, 0, out _);
        handler.TryHandle(SessionEndMessageHandler.Close, 0, 0, out _);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.Failed);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task LifecycleWindowWaitDoesNotBlockSurfaceDispatcherCleanup()
    {
        DesktopWindowDispatcher surfaceDispatcher = new();
        await surfaceDispatcher.Ready;
        int calls = 0;
        await using WindowsProcessExitMonitor monitor = new(async _ =>
        {
            Interlocked.Increment(ref calls);
            await surfaceDispatcher.InvokeAsync(() => 42, CancellationToken.None);
            await surfaceDispatcher.DisposeAsync();
        });
        try
        {
            await monitor.Ready;
            nint window = unchecked((nint)(long)monitor.WindowHandle);
            char[] title = new char[256];
            int length = DesktopNativeMethods.GetWindowText(window, title, title.Length);
            new string(title, 0, length).Should().Be("LiveWall Host Lifecycle", "Unicode creation must use DefWindowProcW, not truncate the title through DefWindowProcA");
            DesktopNativeMethods.SendMessageTimeout(window, SessionEndMessageHandler.QueryReadiness, 0, 0,
                DesktopNativeMethods.SendMessageTimeoutAbortIfHung, 1000, out nuint readiness).Should().NotBe(0);
            readiness.Should().Be(0);
            monitor.MarkRunning();
            DesktopNativeMethods.SendMessageTimeout(window, SessionEndMessageHandler.QueryReadiness, 0, 0,
                DesktopNativeMethods.SendMessageTimeoutAbortIfHung, 1000, out readiness);
            readiness.Should().Be(1);
            DesktopNativeMethods.SendMessageTimeout(window, SessionEndMessageHandler.Close, 0, 0,
                DesktopNativeMethods.SendMessageTimeoutAbortIfHung, 6000, out _).Should().NotBe(0);
            monitor.WaitOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
            calls.Should().Be(1);
            await surfaceDispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { await surfaceDispatcher.DisposeAsync(); }
    }

    [Fact]
    public async Task ConcurrentConsoleAndWindowExitRequestsShareOneCleanup()
    {
        int calls = 0;
        SessionEndMessageHandler handler = new(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, TimeSpan.FromSeconds(1));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            handler.WaitForExit(new(index % 2 == 0 ? WindowsExitReason.ConsoleClose : WindowsExitReason.SessionLogoff)))));
        calls.Should().Be(1);
        handler.WaitOutcome.Should().Be(WindowsExitWaitOutcome.Completed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4001)]
    public void NativeWaitBudgetCannotBecomeUnbounded(int milliseconds)
    {
        Action create = () => { _ = new SessionEndMessageHandler(_ => Task.CompletedTask, TimeSpan.FromMilliseconds(milliseconds)); };
        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
