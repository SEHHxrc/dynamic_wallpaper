using FluentAssertions;
using LiveWall.Application.Configuration;
using LiveWall.Domain.Playback;
using LiveWall.Host;
using LiveWall.Host.Bootstrap;
using LiveWall.Host.CommandLoop;
using LiveWall.Host.Orchestration;
using LiveWall.Platform.Windows.Lifecycle;

namespace LiveWall.Application.Tests;

public sealed partial class HostCommandLoopTests
{
    [Theory]
    [InlineData(WindowsExitWaitOutcome.Completed, true)]
    [InlineData(WindowsExitWaitOutcome.TimedOut, false)]
    [InlineData(WindowsExitWaitOutcome.Failed, false)]
    public async Task NativeReceiptCorrelatesWithRuntimeReceiptWithoutClaimingProcessExit(WindowsExitWaitOutcome outcome, bool successful)
    {
        TestRuntime test = new(true);
        await using WindowsHostRuntime runtime = new(test.CreateCommandLoop(), new HostBootstrapper(new FakeHostConfigurationStore(
            new HostConfiguration(UserPlaybackPolicy.Default, []))), new FakeDisplayTopologySource(CreateTopology()), new FakeWindowsEnvironmentEventBridge());
        await runtime.StartAsync(CancellationToken.None);
        HostExitReceipt? cleanupReceipt = null;
        HostNativeExitReceipt? nativeReceipt = null;
        using HostProcessLifetime lifetime = new(runtime,
            value => { cleanupReceipt = value; return Task.CompletedTask; },
            (value, _) => { nativeReceipt = value; return Task.CompletedTask; });
        await lifetime.RequestStop(new WindowsExitRequest(WindowsExitReason.SessionLogoff));
        await lifetime.PersistNativeObservationAsync(new(new(WindowsExitReason.SessionLogoff), outcome,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(4)), CancellationToken.None);
        nativeReceipt!.RunId.Should().Be(cleanupReceipt!.RunId);
        nativeReceipt.Phase.Should().Be("NativeCallbackCleanupObserved");
        nativeReceipt.ObservedReason.Should().Be("SessionLogoff");
        nativeReceipt.StopReason.Should().Be(cleanupReceipt.Reason);
        nativeReceipt.CleanupOutcome.Should().Be(outcome.ToString());
        nativeReceipt.CleanupSucceeded.Should().Be(successful);
        nativeReceipt.RuntimeStopped.Should().BeTrue();
        nativeReceipt.CallbackReturnObserved.Should().BeFalse();
        nativeReceipt.ProcessExitObserved.Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredNativeReceiptBudgetDoesNotCancelRuntimeOrWriteObservation()
    {
        TestRuntime test = new(true);
        await using WindowsHostRuntime runtime = new(test.CreateCommandLoop(), new HostBootstrapper(new FakeHostConfigurationStore(
            new HostConfiguration(UserPlaybackPolicy.Default, []))), new FakeDisplayTopologySource(CreateTopology()), new FakeWindowsEnvironmentEventBridge());
        await runtime.StartAsync(CancellationToken.None);
        int writes = 0;
        using HostProcessLifetime lifetime = new(runtime, persistNativeReceipt: (_, _) => { writes++; return Task.CompletedTask; });
        using CancellationTokenSource expired = new();
        expired.Cancel();
        Func<Task> write = () => lifetime.PersistNativeObservationAsync(new(new(WindowsExitReason.SessionLogoff),
            WindowsExitWaitOutcome.TimedOut, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)), expired.Token);
        await write.Should().ThrowAsync<OperationCanceledException>();
        writes.Should().Be(0);
        runtime.State.Should().Be(WindowsHostRuntimeState.Running);
        await lifetime.RequestStop("ProgramExit");
        runtime.State.Should().Be(WindowsHostRuntimeState.Stopped);
    }

    [Fact]
    public async Task NativeExitObservationPublishesRegisteredStopBeforeDuplicateCanWin()
    {
        TestRuntime test = new(true);
        await using WindowsHostRuntime runtime = new(test.CreateCommandLoop(), new HostBootstrapper(new FakeHostConfigurationStore(
            new HostConfiguration(UserPlaybackPolicy.Default, []))), new FakeDisplayTopologySource(CreateTopology()), new FakeWindowsEnvironmentEventBridge());
        await runtime.StartAsync(CancellationToken.None);
        TaskCompletionSource releaseReceipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HostExitReceipt? receipt = null;
        using HostProcessLifetime lifetime = new(runtime, async value =>
        {
            receipt = value;
            await releaseReceipt.Task;
        });
        TaskCompletionSource<Task> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task duplicateAfterObservation = Task.Run(async () =>
        {
            Task nativeStop = await observed.Task;
            lifetime.IsStopRequested.Should().BeTrue();
            lifetime.Reason.Should().Be("WindowClose");
            lifetime.RequestStop("DuplicateAfterNativeClose").Should().BeSameAs(nativeStop);
        });
        try
        {
            Task returned = WindowsHostEventChainDiagnostics.RequestObservedNativeStop(lifetime,
                new(WindowsExitReason.WindowClose), observed);
            (await observed.Task).Should().BeSameAs(returned);
            await duplicateAfterObservation.WaitAsync(TimeSpan.FromSeconds(2));
            returned.IsCompleted.Should().BeFalse("native completion must include the pending receipt");
            releaseReceipt.TrySetResult();
            await returned.WaitAsync(TimeSpan.FromSeconds(2));
            receipt.Should().Match<HostExitReceipt>(value => value.Reason == "WindowClose" && value.RuntimeStopped && value.CleanupSucceeded);
        }
        finally { releaseReceipt.TrySetResult(); }
    }

    [Theory]
    [InlineData(WindowsExitReason.WindowClose)]
    [InlineData(WindowsExitReason.SessionShutdown)]
    [InlineData(WindowsExitReason.SessionLogoff)]
    [InlineData(WindowsExitReason.SessionCloseApp)]
    [InlineData(WindowsExitReason.ConsoleClose)]
    public async Task ProcessLifetimeCoalescesAllSignalsIntoOneStop(WindowsExitReason reason)
    {
        TestRuntime test = new(true);
        HostCommandLoop loop = test.CreateCommandLoop();
        FakeWindowsEnvironmentEventBridge bridge = new();
        await using WindowsHostRuntime runtime = new(loop, new HostBootstrapper(new FakeHostConfigurationStore(
            new HostConfiguration(UserPlaybackPolicy.Default, []))), new FakeDisplayTopologySource(CreateTopology()), bridge);
        await runtime.StartAsync(CancellationToken.None);
        using HostProcessLifetime lifetime = new(runtime);
        Task first = lifetime.RequestStop(new WindowsExitRequest(reason));
        Task duplicate = lifetime.RequestStop("ConsoleCancel");
        first.Should().BeSameAs(duplicate);
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        lifetime.Token.IsCancellationRequested.Should().BeTrue();
        lifetime.Reason.Should().Be(reason.ToString());
        bridge.DisposeCount.Should().Be(1);
        await runtime.RunAsync(lifetime.Token); // The stop may win the race before Run starts.
        runtime.State.Should().Be(WindowsHostRuntimeState.Stopped);
    }

    [Fact]
    public async Task ExitSignalCleanupFailureIsNotReportedAsSuccessfulProcessExit()
    {
        TestRuntime test = new(true);
        await using WindowsHostRuntime runtime = new(test.CreateCommandLoop(), new HostBootstrapper(new FakeHostConfigurationStore(
            new HostConfiguration(UserPlaybackPolicy.Default, []))), new FakeDisplayTopologySource(CreateTopology()),
            new FakeWindowsEnvironmentEventBridge(), () => throw new InvalidOperationException("Synthetic platform cleanup failure."));
        await runtime.StartAsync(CancellationToken.None);
        using HostProcessLifetime lifetime = new(runtime);
        Func<Task> exit = () => lifetime.RequestStop("ConsoleCancel");
        await exit.Should().ThrowAsync<InvalidOperationException>();
        runtime.LastFault.Should().NotBeNull();
        runtime.State.Should().Be(WindowsHostRuntimeState.Stopped);
    }

    [Theory]
    [InlineData("InvalidScope")]
    [InlineData("1-invalid")]
    [InlineData("exit-validation-0123456789abcdef0123456789abcdef")]
    public void InvalidInstallationScopeIsRejectedBeforeMutexOrNativeStartup(string scope)
    {
        Action parse = () => HostProgramOptions.Parse(["--installation-scope", scope]);
        parse.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitCompletionIncludesDurableReceiptAndDoesNotHideWriteFailure(bool failWrite)
    {
        TestRuntime test = new(true);
        await using WindowsHostRuntime runtime = new(test.CreateCommandLoop(), new HostBootstrapper(new FakeHostConfigurationStore(
            new HostConfiguration(UserPlaybackPolicy.Default, []))), new FakeDisplayTopologySource(CreateTopology()), new FakeWindowsEnvironmentEventBridge());
        await runtime.StartAsync(CancellationToken.None);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource writing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HostExitReceipt? observed = null;
        using HostProcessLifetime lifetime = new(runtime, async receipt =>
        {
            observed = receipt;
            writing.TrySetResult();
            await release.Task;
            if (failWrite) throw new IOException("Synthetic receipt persistence failure.");
        });
        Task exit = lifetime.RequestStop("SessionLogoff");
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            exit.IsCompleted.Should().BeFalse("native acknowledgement must not precede the receipt write except on bounded native timeout");
            observed!.RuntimeStopped.Should().BeTrue();
            observed.CleanupSucceeded.Should().BeTrue();
            observed.Reason.Should().Be("SessionLogoff");
            release.TrySetResult();
            if (failWrite)
            {
                Func<Task> awaitExit = () => exit;
                await awaitExit.Should().ThrowAsync<IOException>();
            }
            else await exit.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.TrySetResult(); }
    }
}
