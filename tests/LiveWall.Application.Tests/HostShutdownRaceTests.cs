using FluentAssertions;
using LiveWall.Application.Configuration;
using LiveWall.Application.Policies;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Playback;
using LiveWall.Host.CommandLoop;
using LiveWall.Host.Bootstrap;
using LiveWall.Host.Orchestration;

namespace LiveWall.Application.Tests;

public sealed partial class HostCommandLoopTests
{
    [Fact]
    public async Task RetirementFailureKeepsTheRendererDiagnosticMessage()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        FakeRendererSession renderer = new(new("retirement-error-detail"), 7, true) { SuppressShutdownCompleted = true };
        renderer.FailBeforeFirstFrame();
        SessionRetirementBatchResult batch = await runtime.Coordinator.RetireAsync(
            [CreatePreparedSession(renderer, "primary", "error-detail-surface", 1)], CancellationToken.None);
        SessionRetirementIssue issue = batch.Sessions.Single().Issues.Should().ContainSingle().Subject;
        issue.Kind.Should().Be(SessionRetirementIssueKind.ShutdownFailed);
        issue.Detail.Should().Contain("renderer.test.failure").And.Contain("Test renderer failed before presenting a frame.");
        batch.Sessions.Single().ShutdownCompleted.Should().BeFalse();
        batch.OwnedResourcesCleaned.Should().BeTrue();
    }

    [Theory]
    [InlineData("Attach")]
    [InlineData("Load")]
    [InlineData("FirstFrame")]
    [InlineData("Replace")]
    public async Task ShutdownAtApplyStageCancelsTerminalAndCleansEveryProvisionalResource(string stage)
    {
        TestRuntime runtime = new(stage != "FirstFrame");
        runtime.Provider.ConfigureSession = renderer => renderer.BlockCommand = stage switch
        {
            "Attach" => nameof(AttachRendererSurface),
            "Load" => nameof(LoadRendererContent),
            _ => null,
        };
        runtime.Desktop.BlockReplacement = stage == "Replace";
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult accepted = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        Task<ApplyGenerationResult> terminal = loop.WaitForApplyGenerationAsync(accepted.State!.Generation, CancellationToken.None);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);
        if (stage is "Attach" or "Load") await renderer.CommandBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        else if (stage == "Replace") await runtime.Desktop.ReplacementEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        else await WaitUntilAsync(() => renderer.Commands.OfType<LoadRendererContent>().Any(), CancellationToken.None);

        HostCommandResult shutdown = await loop.SendAsync(new ShutdownCommand(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await loop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        ApplyGenerationResult result = await terminal;
        result.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        result.FailureReason.Should().Be(ApplyFailureReason.Cancelled);
        shutdown.State!.Sessions.Should().BeEmpty();
        runtime.Desktop.Replacements.Should().BeEmpty();
        renderer.DisposeCount.Should().Be(1);
        AssertAllOwnedSurfacesCleanedOnce(runtime);
    }

    [Theory]
    [InlineData("Apply")]
    [InlineData("Reconciliation")]
    [InlineData("Reattach")]
    [InlineData("Rebuild")]
    public async Task ConcurrentDisposeWaitsForLatePreparedBatchAndDrainsItsCleanup(string source)
    {
        TestRuntime runtime = new(true, true);
        GatedSessionCoordinator? gate = null;
        await using HostCommandLoop loop = runtime.CreateCommandLoop(inner => gate = new(inner,
            source == "Apply" ? 1 : source == "Reattach" ? int.MaxValue : 2,
            blockedRecoveryCall: source == "Reattach" ? 1 : 0));
        await SetTopologyAsync(loop);
        if (source != "Apply")
        {
            HostCommandResult first = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
            await loop.WaitForApplyGenerationAsync(first.State!.Generation, CancellationToken.None);
        }
        if (source == "Rebuild")
            (await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None)).FailAttachOnCount = 2;
        HostCommand command = source switch
        {
            "Apply" => CreateApplyCommand(),
            "Reconciliation" => new DisplayTopologyChangedCommand(new(2,
                [CreateTopology().Displays[0] with { Bounds = new DisplayBounds(0, 0, 2560, 1440) }])),
            _ => new ExplorerRestartedCommand(),
        };
        HostCommandResult accepted = await loop.SendAsync(command, CancellationToken.None);
        await gate!.Prepared.WaitAsync(TimeSpan.FromSeconds(3));
        Task<DesktopRecoveryResult>? recovery = source is "Reattach" or "Rebuild"
            ? loop.WaitForDesktopRecoveryAsync(accepted.State!.Generation, CancellationToken.None) : null;
        Task<ApplyGenerationResult>? apply = source == "Apply"
            ? loop.WaitForApplyGenerationAsync(accepted.State!.Generation, CancellationToken.None) : null;
        Task<DisplayReconciliationResult>? reconciliation = source == "Reconciliation"
            ? loop.WaitForDisplayReconciliationAsync(2, CancellationToken.None) : null;
        int commits = runtime.Desktop.Replacements.Count;
        try
        {
            Task firstDispose = loop.DisposeAsync().AsTask();
            Task secondDispose = loop.DisposeAsync().AsTask();
            firstDispose.Should().BeSameAs(secondDispose);
            HostCommandResult stopped = await loop.SendAsync(new ShutdownCommand(), CancellationToken.None);
            stopped.State!.ShutdownRequested.Should().BeTrue();
            firstDispose.IsCompleted.Should().BeFalse("the late producer still owns a prepared batch");
            loop.TryPost(new RendererExitedCommand(new("stale"), 1, 0)).Should().BeTrue();
            loop.TryPost(new RendererEventCommand(new("stale"), 1, new RendererFirstFramePresented(1))).Should().BeTrue();
            (await loop.SendAsync(new DisplayTopologyChangedCommand(CreateTopology() with { Revision = 20 }), CancellationToken.None))
                .ErrorCode.Should().Be("host.stopping");
            gate.Release();
            await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(3));
            if (recovery is not null) (await recovery).TerminalState.Should().Be(DesktopRecoveryTerminalState.Cancelled);
            if (apply is not null) (await apply).State.Should().Be(ApplyGenerationTerminalState.Cancelled);
            if (reconciliation is not null) (await reconciliation).TerminalState.Should().Be(DisplayReconciliationTerminalState.Cancelled);
            runtime.Desktop.Replacements.Should().HaveCount(commits);
            for (int index = 0; index < runtime.Provider.SessionCount; index++)
                (await runtime.Provider.WaitForSessionAsync(index, CancellationToken.None)).DisposeCount.Should().Be(1);
            AssertAllOwnedSurfacesCleanedOnce(runtime);
            HostCommandResult after = await loop.SendAsync(new ShutdownCommand(), CancellationToken.None);
            after.State!.Sessions.Should().BeEmpty();
            (await loop.SendAsync(CreateApplyCommand(), CancellationToken.None)).ErrorCode.Should().Be("host.stopped");
            loop.TryPost(new ExplorerRestartedCommand()).Should().BeFalse();
        }
        finally { gate.Release(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDuringRecoveryOrTopologyBackoffDoesNotStartAnotherRenderer(bool recovery)
    {
        TestRuntime runtime = new(true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult first = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(first.State!.Generation, CancellationToken.None);
        if (recovery)
        {
            (await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None)).FailAttachOnCount = 2;
            HostCommandResult accepted = await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
            await WaitUntilAsync(() => loop.GetDesktopRecoveryTimelineForDiagnostics(accepted.State!.Generation)
                .Any(entry => entry.Phase == DesktopRecoveryPhase.RebuildScheduled), CancellationToken.None);
        }
        else
        {
            runtime.Desktop.TransientTopologyFailuresRemaining = 3;
            await loop.SendAsync(new DisplayTopologyChangedCommand(new(2,
                [CreateTopology().Displays[0] with { ScaleFactor = 1.5 }])), CancellationToken.None);
            await WaitUntilAsync(() => loop.GetDisplayReconciliationTimelineForDiagnostics(2)
                .Any(entry => entry.Phase == DisplayReconciliationPhase.PreparationRetryScheduled), CancellationToken.None);
        }
        await loop.SendAsync(new ShutdownCommand(), CancellationToken.None);
        await loop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        runtime.Provider.SessionCount.Should().Be(1);
        runtime.Desktop.Replacements.Should().ContainSingle();
        (await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None)).DisposeCount.Should().Be(1);
        AssertAllOwnedSurfacesCleanedOnce(runtime);
    }

    [Fact]
    public async Task RepeatedShutdownAndDisposeWaitForIndependentOldRendererRetirementTimeout()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), ApplyDeadlineBudget.Default,
            new SessionRetirementPolicy(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)), true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult first = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(first.State!.Generation, CancellationToken.None);
        FakeRendererSession old = await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);
        old.SuppressShutdownCompleted = true;
        HostCommandResult second = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(second.State!.Generation, CancellationToken.None);
        Task[] shutdowns = Enumerable.Range(0, 6).Select(_ => loop.SendAsync(new ShutdownCommand(), CancellationToken.None)).ToArray();
        Task[] disposals = Enumerable.Range(0, 6).Select(_ => loop.DisposeAsync().AsTask()).ToArray();
        await Task.WhenAll(shutdowns.Concat(disposals)).WaitAsync(TimeSpan.FromSeconds(3));
        SessionRetirementResult[] history = loop.GetRetirementHistoryForDiagnostics().SelectMany(batch => batch.Sessions).ToArray();
        history.Should().HaveCount(2);
        history.Single(item => item.SessionId == old.Id).Issues.Should().Contain(item => item.Kind == SessionRetirementIssueKind.ShutdownTimeout);
        history.Single(item => item.SessionId != old.Id).Graceful.Should().BeTrue();
        old.Commands.OfType<ShutdownRenderer>().Should().ContainSingle();
        old.DisposeCount.Should().Be(1);
        AssertAllOwnedSurfacesCleanedOnce(runtime);
    }

    [Theory]
    [InlineData("Reconciliation")]
    [InlineData("Reattach")]
    [InlineData("Rebuild")]
    public async Task ShutdownInterruptsPendingReplacementInEveryRecoveryPath(string source)
    {
        TestRuntime runtime = new(true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult first = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(first.State!.Generation, CancellationToken.None);
        runtime.Desktop.BlockReplacement = true;
        if (source == "Rebuild")
            (await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None)).FailAttachOnCount = 2;
        HostCommandResult accepted = await loop.SendAsync(source == "Reconciliation"
            ? new DisplayTopologyChangedCommand(new(2, [CreateTopology().Displays[0] with { ScaleFactor = 1.75 }]))
            : new ExplorerRestartedCommand(), CancellationToken.None);
        await runtime.Desktop.ReplacementEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await loop.SendAsync(new ShutdownCommand(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        if (source == "Reconciliation")
            (await loop.WaitForDisplayReconciliationAsync(2, CancellationToken.None)).TerminalState
                .Should().Be(DisplayReconciliationTerminalState.Cancelled);
        else
            (await loop.WaitForDesktopRecoveryAsync(accepted.State!.Generation, CancellationToken.None)).TerminalState
                .Should().Be(DesktopRecoveryTerminalState.Cancelled);
        await loop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        runtime.Desktop.Replacements.Should().ContainSingle();
        for (int index = 0; index < runtime.Provider.SessionCount; index++)
            (await runtime.Provider.WaitForSessionAsync(index, CancellationToken.None)).DisposeCount.Should().Be(1);
        AssertAllOwnedSurfacesCleanedOnce(runtime);
    }

    [Fact]
    public async Task ShutdownDuringFirstRecoveryTopologyQueryStillAbandonsTheInvalidOldGeneration()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult first = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(first.State!.Generation, CancellationToken.None);
        runtime.Desktop.BlockTopologyOnCall = runtime.Desktop.EnsureTopologyCallCount + 1;
        HostCommandResult accepted = await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await runtime.Desktop.TopologyEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await loop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        (await loop.WaitForDesktopRecoveryAsync(accepted.State!.Generation, CancellationToken.None)).TerminalState
            .Should().Be(DesktopRecoveryTerminalState.Cancelled);
        (await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None)).DisposeCount.Should().Be(1);
        runtime.Desktop.DestroyedSurfaces.Should().BeEmpty("invalid Shell generation HWNDs must only be abandoned");
        AssertAllOwnedSurfacesCleanedOnce(runtime);
    }

    [Fact]
    public async Task CancelledRuntimeStopWaiterDoesNotCancelSharedCleanupOrPersistence()
    {
        TestRuntime test = new(true);
        HostCommandLoop loop = test.CreateCommandLoop();
        FakeHostConfigurationStore store = new(new HostConfiguration(UserPlaybackPolicy.Default, []));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeWindowsEnvironmentEventBridge bridge = new() { StopGate = release };
        await using WindowsHostRuntime runtime = new(loop, new HostBootstrapper(store),
            new FakeDisplayTopologySource(CreateTopology()), bridge);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            using CancellationTokenSource caller = new();
            caller.Cancel();
            Func<Task> cancelledWaiter = () => runtime.StopAsync(caller.Token);
            await cancelledWaiter.Should().ThrowAsync<OperationCanceledException>();
            await bridge.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task shared = runtime.StopAsync(CancellationToken.None);
            shared.IsCompleted.Should().BeFalse();
            release.TrySetResult();
            await shared.WaitAsync(TimeSpan.FromSeconds(3));
            runtime.State.Should().Be(WindowsHostRuntimeState.Stopped);
            runtime.LastFault.Should().BeNull();
            store.SavedPolicy.Should().Be(UserPlaybackPolicy.Default);
            store.SavedAssignments.Should().BeEmpty();
            bridge.DisposeCount.Should().Be(1);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task EveryQueuedCallerSettlesWhenShutdownInterruptsPendingReplace()
    {
        TestRuntime runtime = new(true);
        runtime.Desktop.BlockReplacement = true;
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult accepted = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        Task<ApplyGenerationResult> terminal = loop.WaitForApplyGenerationAsync(accepted.State!.Generation, CancellationToken.None);
        await runtime.Desktop.ReplacementEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<HostCommandResult>[] reads = Enumerable.Range(0, 20)
            .Select(_ => loop.SendAsync(new GetHostStateCommand(), CancellationToken.None)).ToArray();
        Task<HostCommandResult>[] applies = Enumerable.Range(0, 20)
            .Select(_ => loop.SendAsync(CreateApplyCommand(), CancellationToken.None)).ToArray();
        Task<HostCommandResult> stop = loop.SendAsync(new ShutdownCommand(), CancellationToken.None);
        Task dispose = loop.DisposeAsync().AsTask();
        await Task.WhenAll(reads.Concat(applies).Append(stop).Cast<Task>().Append(dispose)).WaitAsync(TimeSpan.FromSeconds(3));
        reads.Should().OnlyContain(task => task.IsCompletedSuccessfully && task.Result.Accepted);
        foreach (Task<HostCommandResult> apply in applies) (await apply).ErrorCode.Should().Be("host.stopping");
        (await terminal).State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        runtime.Provider.SessionCount.Should().Be(1);
        AssertAllOwnedSurfacesCleanedOnce(runtime);
    }

    [Fact]
    public async Task CleanupFailureRemainsObservableToAllDisposeWaiters()
    {
        TestRuntime runtime = new(true);
        HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult first = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(first.State!.Generation, CancellationToken.None);
        runtime.Desktop.FailDestroy = true;
        Task firstDispose = loop.DisposeAsync().AsTask();
        Task secondDispose = loop.DisposeAsync().AsTask();
        firstDispose.Should().BeSameAs(secondDispose);
        Func<Task> wait = () => firstDispose;
        await wait.Should().ThrowAsync<AggregateException>();
        secondDispose.IsFaulted.Should().BeTrue();
        SessionRetirementResult retirement = loop.GetRetirementHistoryForDiagnostics().Single().Sessions.Single();
        retirement.OwnedResourcesCleaned.Should().BeFalse();
        retirement.Issues.Should().Contain(issue => issue.Kind == SessionRetirementIssueKind.SurfaceCleanupFailed);
        (await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None)).DisposeCount.Should().Be(1);
        runtime.Desktop.DestroyedSurfaces.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringConfigurationOrAssignmentRestoreCannotRestartDisposedEventBridge(bool restoring)
    {
        TestRuntime test = new(true);
        GatedSessionCoordinator? gate = null;
        HostCommandLoop loop = test.CreateCommandLoop(inner => gate = new(inner, 1));
        TaskCompletionSource load = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHostConfigurationStore store = new(new HostConfiguration(UserPlaybackPolicy.Default,
            restoring ? [new(new("primary"), new("sample.wallpaper"), LiveWall.Domain.Layouts.LayoutMode.PerDisplay,
                LiveWall.Domain.Layouts.FitMode.Cover, null)] : [])) { LoadGate = restoring ? null : load };
        FakeWindowsEnvironmentEventBridge bridge = new();
        await using WindowsHostRuntime runtime = new(loop, new HostBootstrapper(store), new FakeDisplayTopologySource(CreateTopology()), bridge);
        Task start = runtime.StartAsync(CancellationToken.None);
        if (restoring) await gate!.Prepared.WaitAsync(TimeSpan.FromSeconds(2));
        else await store.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Task stop = runtime.StopAsync(CancellationToken.None);
            gate!.Release();
            load.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Func<Task> awaitStartup = () => start;
            await awaitStartup.Should().ThrowAsync<OperationCanceledException>();
            runtime.State.Should().Be(WindowsHostRuntimeState.Stopped);
            runtime.LastFault.Should().BeNull();
            bridge.StartCount.Should().Be(0);
            bridge.DisposeCount.Should().Be(1);
            store.SavedAssignments.Should().BeNull("an incomplete startup must preserve the existing configuration file");
            test.Desktop.Replacements.Should().BeEmpty();
            if (restoring) (await test.Provider.WaitForSessionAsync(0, CancellationToken.None)).DisposeCount.Should().Be(1);
            AssertAllOwnedSurfacesCleanedOnce(test);
        }
        finally { gate!.Release(); load.TrySetResult(); }
    }

    private static void AssertAllOwnedSurfacesCleanedOnce(TestRuntime runtime)
    {
        string[] cleaned = runtime.Desktop.DestroyedSurfaces.Concat(runtime.Desktop.AbandonedSurfaces)
            .Select(surface => surface.Value).ToArray();
        cleaned.Should().HaveCount(runtime.Desktop.CreatedSurfaceCount);
        cleaned.Should().OnlyHaveUniqueItems();
    }
}
