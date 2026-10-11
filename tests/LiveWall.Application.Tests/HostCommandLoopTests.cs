using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using LiveWall.Application.Configuration;
using LiveWall.Application.Importing;
using LiveWall.Application.Layouts;
using LiveWall.Application.Library;
using LiveWall.Application.Policies;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.Bootstrap;
using LiveWall.Host.CommandLoop;
using LiveWall.Host.Orchestration;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Displays;
using LiveWall.Platform.Windows.Power;

namespace LiveWall.Application.Tests;

public sealed class HostCommandLoopTests
{
    [Fact]
    public async Task JoinedTopologyFailureSharesRecoveryCircuitTerminalWithoutHalfCommit()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, false, false, false);
        GatedSessionCoordinator? gate = null;
        await using HostCommandLoop loop = runtime.CreateCommandLoop(inner =>
            gate = new GatedSessionCoordinator(inner, int.MaxValue, blockedRecoveryCall: 1));
        await SetTopologyAsync(loop);
        HostCommandResult initial = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(initial.State!.Generation, CancellationToken.None);
        HostCommandResult accepted = await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await gate!.Prepared.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await loop.SendAsync(new DisplayTopologyChangedCommand(CreateTopology() with { Revision = 2 }),
                CancellationToken.None);
            gate.Release();
            DesktopRecoveryResult recovery = await loop.WaitForDesktopRecoveryAsync(accepted.State!.Generation,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6));
            DisplayReconciliationResult topology = await loop.WaitForDisplayReconciliationAsync(2,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            recovery.TerminalState.Should().Be(DesktopRecoveryTerminalState.CircuitBroken);
            topology.TerminalState.Should().Be(DisplayReconciliationTerminalState.Failed);
            topology.FailureReason.Should().Be(ApplyFailureReason.FirstFrameTimeout);
            runtime.Desktop.Replacements.Should().ContainSingle();
            HostStateSnapshot closed = await GetStateAsync(loop);
            closed.Sessions.Should().BeEmpty();
            closed.Assignments.Should().ContainSingle();
            runtime.Provider.SessionCount.Should().Be(4);
            for (int index = 0; index < 4; index++)
                (await runtime.Provider.WaitForSessionAsync(index, CancellationToken.None)).IsDisposed.Should().BeTrue();
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task ShutdownCancelsJoinedTopologyAndLatePreparedRecoveryOnlyCleansUp()
    {
        TestRuntime runtime = new(true);
        GatedSessionCoordinator? gate = null;
        await using HostCommandLoop loop = runtime.CreateCommandLoop(inner =>
            gate = new GatedSessionCoordinator(inner, int.MaxValue, blockedRecoveryCall: 1));
        await SetTopologyAsync(loop);
        HostCommandResult initial = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(initial.State!.Generation, CancellationToken.None);
        HostCommandResult recovering = await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await gate!.Prepared.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await loop.SendAsync(new DisplayTopologyChangedCommand(CreateTopology() with { Revision = 2 }),
                CancellationToken.None);
            await loop.SendAsync(new ShutdownCommand(), CancellationToken.None);
            gate.Release();
            (await loop.WaitForDesktopRecoveryAsync(recovering.State!.Generation, CancellationToken.None))
                .TerminalState.Should().Be(DesktopRecoveryTerminalState.Cancelled);
            (await loop.WaitForDisplayReconciliationAsync(2, CancellationToken.None))
                .TerminalState.Should().Be(DisplayReconciliationTerminalState.Cancelled);
            await WaitUntilAsync(() => runtime.Desktop.AbandonedSurfaces.Contains(new SurfaceId("surface-2")),
                CancellationToken.None);
            await loop.WaitForPendingRetirementsForDiagnosticsAsync(CancellationToken.None);
            runtime.Desktop.Replacements.Should().ContainSingle();
            runtime.Provider.SessionCount.Should().Be(1);
            HostStateSnapshot stopped = await GetStateAsync(loop);
            stopped.Sessions.Should().BeEmpty();
            stopped.Assignments.Should().ContainSingle();
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task TopologyCancelsInFlightRebuildAndLateRetryCannotCancelNewerAttempt()
    {
        TestRuntime runtime = new(true, false, false);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult initial = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(initial.State!.Generation, CancellationToken.None);
        FakeRendererSession original = await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);
        original.FailAttachOnCount = 2;
        HostCommandResult accepted = await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        FakeRendererSession stale = await runtime.Provider.WaitForSessionAsync(1, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => stale.Commands.OfType<LoadRendererContent>().Any(), CancellationToken.None);
        long staleGeneration = (await GetStateAsync(loop)).Generation;
        await loop.SendAsync(new DisplayTopologyChangedCommand(CreateTopology() with { Revision = 2 }),
            CancellationToken.None);
        FakeRendererSession latest = await runtime.Provider.WaitForSessionAsync(2, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => latest.Commands.OfType<LoadRendererContent>().Any(), CancellationToken.None);
        Task<DesktopRecoveryResult> terminal = loop.WaitForDesktopRecoveryAsync(accepted.State!.Generation,
            CancellationToken.None);
        await loop.SendAsync(new RetryDesktopRecoveryCommand(staleGeneration, 1), CancellationToken.None);
        await loop.SendAsync(new DesktopRecoveryFailedCommand(staleGeneration, "late.failure", "late",
            DesktopRecoveryPhase.FirstFramePresented, ApplyFailureReason.FirstFrameTimeout), CancellationToken.None);
        await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        terminal.IsCompleted.Should().BeFalse();
        stale.IsDisposed.Should().BeTrue();
        ApplyGenerationResult cancelled = await loop.WaitForApplyGenerationAsync(staleGeneration, CancellationToken.None);
        cancelled.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        cancelled.FailureReason.Should().Be(ApplyFailureReason.Superseded);
        runtime.Desktop.Replacements.Should().ContainSingle();
        latest.PresentFirstFrame();
        (await terminal.WaitAsync(TimeSpan.FromSeconds(3))).TerminalState
            .Should().Be(DesktopRecoveryTerminalState.Succeeded);
        runtime.Desktop.Replacements.Should().HaveCount(2);
        runtime.Provider.SessionCount.Should().Be(3);
        (await GetStateAsync(loop)).Sessions.Should().ContainSingle(item => item.Session.Id == latest.Id);
    }

    [Fact]
    public async Task ConcurrentDualDisplayRecoveryWaitsForBothLatestFirstFramesAndPreservesPause()
    {
        TestRuntime runtime = new(true, true, true, false);
        GatedSessionCoordinator? gate = null;
        await using HostCommandLoop loop = runtime.CreateCommandLoop(inner =>
            gate = new GatedSessionCoordinator(inner, int.MaxValue, blockedRecoveryCall: 1));
        DisplayTopology topology = CreateDualDisplayTopology() with { Revision = 1 };
        await loop.SendAsync(new DisplayTopologyChangedCommand(topology), CancellationToken.None);
        HostCommandResult initial = await loop.SendAsync(CreateApplyCommand() with
        { Displays = topology.Displays.Select(display => display.Id).ToArray() }, CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(initial.State!.Generation, CancellationToken.None);
        await loop.SendAsync(new ChangePlaybackIntentCommand(UserPlaybackIntent.Pause), CancellationToken.None);
        HostCommandResult recovering = await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await gate!.Prepared.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            DisplayTopology latest = topology with { Revision = 2 };
            await loop.SendAsync(new DisplayTopologyChangedCommand(latest), CancellationToken.None);
            gate.Release();
            FakeRendererSession second = await runtime.Provider.WaitForSessionAsync(3, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => second.Commands.OfType<LoadRendererContent>().Any(), CancellationToken.None);
            runtime.Desktop.Replacements.Should().ContainSingle();
            (await GetStateAsync(loop)).Sessions.Should().BeEmpty();
            second.PresentFirstFrame();
            DesktopRecoveryResult terminal = await loop.WaitForDesktopRecoveryAsync(recovering.State!.Generation,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            terminal.TerminalState.Should().Be(DesktopRecoveryTerminalState.Succeeded);
            DisplayReconciliationResult reconciled = await loop.WaitForDisplayReconciliationAsync(2,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            reconciled.Timeline.Should().ContainSingle(item => item.Phase == DisplayReconciliationPhase.SurfacesReplaced);
            HostStateSnapshot committed = await GetStateAsync(loop);
            committed.Sessions.Should().HaveCount(2).And.OnlyContain(item =>
                item.UserIntent == UserPlaybackIntent.Pause && item.Session.State == PlaybackState.Paused);
            runtime.Desktop.Replacements.Should().HaveCount(2);
            runtime.Desktop.ResizedSurfaces.Should().BeEmpty();
            loop.GetRecoveryOperationIdsForDiagnostics().Should().ContainSingle();
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task TopologySupersedesPreparedRebuildAndOnlyLatestRevisionCommits()
    {
        TestRuntime runtime = new(true, true, true);
        GatedSessionCoordinator? gate = null;
        await using HostCommandLoop loop = runtime.CreateCommandLoop(inner =>
            gate = new GatedSessionCoordinator(inner, blockedPrepareCall: 2));
        await SetTopologyAsync(loop);
        HostCommandResult initial = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(initial.State!.Generation, CancellationToken.None);
        FakeRendererSession original = await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);
        original.FailAttachOnCount = 2;
        HostCommandResult recovering = await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await gate!.Prepared.WaitAsync(TimeSpan.FromSeconds(3));
        FakeRendererSession provisional = await runtime.Provider.WaitForSessionAsync(1, CancellationToken.None);
        try
        {
            await loop.SendAsync(new DisplayTopologyChangedCommand(new DisplayTopology(2,
                [CreateTopology().Displays[0] with { Bounds = new DisplayBounds(0, 0, 2560, 1440) }])),
                CancellationToken.None);
            await loop.SendAsync(new DisplayTopologyChangedCommand(CreateTopology() with { Revision = 3 }),
                CancellationToken.None);
            runtime.Desktop.Replacements.Should().ContainSingle();
            gate.Release();
            DesktopRecoveryResult recovered = await loop.WaitForDesktopRecoveryAsync(
                recovering.State!.Generation, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            recovered.TerminalState.Should().Be(DesktopRecoveryTerminalState.Succeeded);
            recovered.CompletionMode.Should().Be(DesktopRecoveryCompletionMode.Rebuilt);
            DisplayReconciliationResult latest = await loop.WaitForDisplayReconciliationAsync(3,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            latest.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
            provisional.IsDisposed.Should().BeTrue();
            runtime.Desktop.Replacements.Should().HaveCount(2);
            (await GetStateAsync(loop)).DisplayTopology!.Revision.Should().Be(3);
            loop.GetRecoveryOperationIdsForDiagnostics().Should().ContainSingle();
            loop.GetRetirementHistoryForDiagnostics().SelectMany(batch => batch.Sessions)
                .Should().Contain(item => item.RendererDisposed && item.SurfacesCleaned);
        }
        finally { gate.Release(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task TopologyDuringPreparedShellRecoveryCoalescesIntoLatestBatch(bool empty, bool changedBounds)
    {
        TestRuntime runtime = new(true, true);
        GatedSessionCoordinator? gate = null;
        await using HostCommandLoop loop = runtime.CreateCommandLoop(inner =>
            gate = new GatedSessionCoordinator(inner, int.MaxValue, blockedRecoveryCall: 1));
        await SetTopologyAsync(loop);
        HostCommandResult initial = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(initial.State!.Generation, CancellationToken.None);
        HostCommandResult recovering = await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await gate!.Prepared.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            DisplayTopology next = new(2, empty ? [] :
                [CreateTopology().Displays[0] with
                { Bounds = changedBounds ? new DisplayBounds(0, 0, 2560, 1440) : CreateTopology().Displays[0].Bounds }]);
            await loop.SendAsync(new DisplayTopologyChangedCommand(next), CancellationToken.None);
            await loop.SendAsync(new DisplayTopologyChangedCommand(next with { Revision = 3 }), CancellationToken.None);
            await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
            DisplayReconciliationResult superseded = await loop.WaitForDisplayReconciliationAsync(
                2, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            superseded.TerminalState.Should().Be(DisplayReconciliationTerminalState.Cancelled);
            superseded.FailureReason.Should().Be(ApplyFailureReason.Superseded);
            runtime.Desktop.Replacements.Should().ContainSingle();
            gate.Release();
            DesktopRecoveryResult terminal = await loop.WaitForDesktopRecoveryAsync(
                recovering.State!.Generation, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            DisplayReconciliationResult reconciled = await loop.WaitForDisplayReconciliationAsync(
                3, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            terminal.TerminalState.Should().Be(DesktopRecoveryTerminalState.Succeeded);
            terminal.CompletionMode.Should().Be(DesktopRecoveryCompletionMode.Rebuilt);
            reconciled.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
            reconciled.Kind.Should().Be(empty ? DisplayReconciliationKind.Suspend : DisplayReconciliationKind.Reconfigure);
            HostStateSnapshot state = await GetStateAsync(loop);
            state.DisplayTopology!.Revision.Should().Be(3);
            state.Assignments.Should().ContainSingle();
            state.Sessions.Should().HaveCount(empty ? 0 : 1);
            runtime.Desktop.Replacements.Should().HaveCount(empty ? 1 : 2);
            loop.GetRecoveryOperationIdsForDiagnostics().Should().ContainSingle();
            if (!empty)
                loop.GetPreparedSessionsForDiagnostics().Single().Surfaces.Single().Request.Bounds
                    .Should().Be(next.Displays[0].Bounds);
            else
            {
                runtime.Provider.SessionCount.Should().Be(1);
                await loop.SendAsync(new DisplayTopologyChangedCommand(CreateTopology() with { Revision = 4 }),
                    CancellationToken.None);
                DisplayReconciliationResult resumed = await loop.WaitForDisplayReconciliationAsync(4,
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
                resumed.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
                (await GetStateAsync(loop)).Sessions.Should().ContainSingle();
                runtime.Desktop.Replacements.Should().HaveCount(2);
            }
        }
        finally { gate.Release(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShellChangeImmediatelyCancelsPreparedApplyOrTopologyAndLateResultCannotCommit(bool topologyChange)
    {
        TestRuntime runtime = new(true, true);
        GatedSessionCoordinator? gate = null;
        await using HostCommandLoop loop = runtime.CreateCommandLoop(inner =>
            gate = new GatedSessionCoordinator(inner, blockedPrepareCall: 2));
        await SetTopologyAsync(loop);
        HostCommandResult initial = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(initial.State!.Generation, CancellationToken.None);
        HostCommandResult pending = await loop.SendAsync(topologyChange
            ? new DisplayTopologyChangedCommand(new DisplayTopology(2,
                [CreateTopology().Displays[0] with { Bounds = new DisplayBounds(0, 0, 2560, 1440) }]))
            : CreateApplyCommand(), CancellationToken.None);
        await gate!.Prepared.WaitAsync(TimeSpan.FromSeconds(2));
        FakeRendererSession provisional = await runtime.Provider.WaitForSessionAsync(1, CancellationToken.None);
        try
        {
            HostCommandResult recovery = await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
            ApplyGenerationResult superseded = await loop.WaitForApplyGenerationAsync(
                pending.State!.Generation, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            superseded.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
            superseded.FailureReason.Should().Be(ApplyFailureReason.Superseded);
            if (topologyChange)
            {
                DisplayReconciliationResult topology = await loop.WaitForDisplayReconciliationAsync(
                    2, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                topology.TerminalState.Should().Be(DisplayReconciliationTerminalState.Cancelled);
                topology.FailureReason.Should().Be(ApplyFailureReason.Superseded);
            }
            DesktopRecoveryResult recovered = await loop.WaitForDesktopRecoveryAsync(
                recovery.State!.Generation, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            recovered.TerminalState.Should().Be(DesktopRecoveryTerminalState.Succeeded);
            HostStateSnapshot committed = await GetStateAsync(loop);
            int replacements = runtime.Desktop.Replacements.Count;
            gate.Release();
            await WaitUntilAsync(() => provisional.IsDisposed, CancellationToken.None);
            HostStateSnapshot afterLateResult = await GetStateAsync(loop);
            afterLateResult.Sessions.Select(item => item.Session.Id)
                .Should().Equal(committed.Sessions.Select(item => item.Session.Id));
            runtime.Desktop.Replacements.Should().HaveCount(replacements);
            afterLateResult.Assignments.Should().Equal(committed.Assignments);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task ReconnectWithNewShellDoesNotApplyOfflineAssignmentToAnUnfamiliarDisplay()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult apply = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(apply.State!.Generation, CancellationToken.None);
        await loop.SendAsync(SessionState(2, true), CancellationToken.None);
        await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await loop.SendAsync(new DisplayTopologyChangedCommand(new DisplayTopology(2,
            [CreateTopology().Displays[0] with { Id = new DisplayId("unfamiliar") }])), CancellationToken.None);
        await loop.SendAsync(SessionState(3, false), CancellationToken.None);
        SessionEnvironmentResult resumed = await loop.WaitForSessionEnvironmentAsync(3,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        resumed.State.Should().Be(SessionEnvironmentTerminalState.Succeeded);
        HostStateSnapshot state = await GetStateAsync(loop);
        state.Sessions.Should().BeEmpty();
        state.Assignments.Should().ContainSingle(assignment => assignment.DisplayId == new DisplayId("primary"));
        runtime.Provider.SessionCount.Should().Be(1);
        runtime.Desktop.Replacements.Should().ContainSingle();
    }

    [Fact]
    public async Task RefreshOnlyWhileDisconnectedResumesWithoutSurfaceReplacement()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult apply = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(apply.State!.Generation, CancellationToken.None);
        await loop.SendAsync(SessionState(2, true), CancellationToken.None);
        await loop.SendAsync(new DisplayTopologyChangedCommand(new DisplayTopology(2,
            [CreateTopology().Displays[0] with { RefreshRateHz = 120 }])), CancellationToken.None);
        await loop.SendAsync(SessionState(3, false), CancellationToken.None);
        SessionEnvironmentResult result = await loop.WaitForSessionEnvironmentAsync(3, CancellationToken.None);
        result.Path.Should().Be(SessionEnvironmentPath.Resume);
        result.State.Should().Be(SessionEnvironmentTerminalState.Succeeded);
        (await GetStateAsync(loop)).Sessions[0].Session.State.Should().Be(PlaybackState.Playing);
        runtime.Provider.SessionCount.Should().Be(1);
        runtime.Desktop.Replacements.Should().ContainSingle();
    }

    [Fact]
    public async Task DisconnectDuringRecoveryCancelsOldRecoveryBeforeNewSessionRebuild()
    {
        TestRuntime runtime = new(true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult apply = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(apply.State!.Generation, CancellationToken.None);
        FakeRendererSession previous = await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);
        previous.SuppressSurfaceAttachedOnCount = 2;
        await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await WaitUntilAsync(() => previous.Commands.OfType<AttachRendererSurface>().Count() == 2,
            CancellationToken.None);
        long oldRecovery = loop.GetRecoveryOperationIdsForDiagnostics().Single();
        await loop.SendAsync(SessionState(2, true), CancellationToken.None);
        await loop.SendAsync(SessionState(3, false), CancellationToken.None);
        DesktopRecoveryResult abandoned = await loop.WaitForDesktopRecoveryAsync(oldRecovery,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        abandoned.TerminalState.Should().Be(DesktopRecoveryTerminalState.Cancelled);
        SessionEnvironmentResult rebuilt = await loop.WaitForSessionEnvironmentAsync(3,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        rebuilt.State.Should().Be(SessionEnvironmentTerminalState.Succeeded);
        rebuilt.Path.Should().Be(SessionEnvironmentPath.Rebuild);
        previous.IsDisposed.Should().BeTrue();
        runtime.Provider.SessionCount.Should().Be(2);
        runtime.Desktop.Replacements.Should().HaveCount(2);
    }

    [Fact]
    public async Task ShutdownCancelsAwaitingReconnectTerminalAndLateValidationIsNoOp()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        await loop.SendAsync(SessionState(2, true), CancellationToken.None);
        await loop.SendAsync(new SystemStateChangedCommand(new SystemStateChange(3,
            new SystemStateSnapshot(false, false, true, false, false, false),
            SystemStateChangeReason.SessionReconnectRequested, RequiresEnvironmentValidation: true)), CancellationToken.None);
        await loop.SendAsync(new ShutdownCommand(), CancellationToken.None);
        SessionEnvironmentResult stopped = await loop.WaitForSessionEnvironmentAsync(3, CancellationToken.None);
        stopped.State.Should().Be(SessionEnvironmentTerminalState.Cancelled);
        stopped.Reason.Should().Be(SessionEnvironmentFailureReason.HostStopping);
        HostCommandResult late = await loop.SendAsync(SessionState(4, false), CancellationToken.None);
        late.Accepted.Should().BeFalse();
        runtime.Provider.SessionCount.Should().Be(0);
        runtime.Desktop.Replacements.Should().BeEmpty();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionReconnectValidationFailureHasAwaitableTerminalAndCannotResumeLate(bool timeout)
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        await using WindowsDesktopHost desktop = new(new WindowsShellSnapshot("test", "26200", true, 1));
        using WindowsDisplayTopologySource displays = new();
        FakeExplorerChangeMonitor monitor = new();
        WindowsSystemStateSource source = new(new SystemStateSnapshot(false, false, false, false, false, false));
        await using WindowsDesktopEventBridge bridge = new(desktop, displays, loop, monitor, source,
            consumeNativeWindowSignals: false, sessionValidationBudget: TimeSpan.FromMilliseconds(100));
        bridge.Start();
        HostCommandResult apply = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(apply.State!.Generation, CancellationToken.None);
        bridge.ProcessWindowSignalForTests(new(DesktopWindowSignalKind.SessionDisconnected));
        bridge.ProcessWindowSignalForTests(new(DesktopWindowSignalKind.SessionReconnected));
        long revision = source.Current.Revision;
        await WaitForStateAsync(loop, state => state.SystemStateRevision == revision);
        if (!timeout)
        {
            monitor.RaiseValidationFailure(new InvalidOperationException("test validation failure"));
        }
        SessionEnvironmentResult result = await loop.WaitForSessionEnvironmentAsync(revision,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        result.State.Should().Be(SessionEnvironmentTerminalState.Failed);
        result.Reason.Should().Be(timeout ? SessionEnvironmentFailureReason.ValidationTimeout :
            SessionEnvironmentFailureReason.ValidationFailed);
        monitor.RaiseValidationCompleted();
        HostStateSnapshot failed = await GetStateAsync(loop);
        failed.SystemState.IsSessionDisconnected.Should().BeTrue();
        failed.Sessions[0].Session.State.Should().Be(PlaybackState.Suspended);
        failed.Assignments.Should().ContainSingle();
        runtime.Desktop.Replacements.Should().ContainSingle();
    }

    [Fact]
    public async Task EventBridgeReconnectUsesPinnedValidationAndIgnoresOtherSession()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        await using WindowsDesktopHost desktop = new(new WindowsShellSnapshot("test", "26200", true, 1));
        using WindowsDisplayTopologySource displays = new();
        FakeExplorerChangeMonitor monitor = new();
        WindowsSystemStateSource source = new(new SystemStateSnapshot(false, false, false, false, false, false));
        await using WindowsDesktopEventBridge bridge = new(desktop, displays, loop, monitor, source,
            consumeNativeWindowSignals: false);
        bridge.Start();
        HostCommandResult apply = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(apply.State!.Generation, CancellationToken.None);
        bridge.ProcessWindowSignalForTests(new(DesktopWindowSignalKind.SessionDisconnected,
            SessionId: System.Diagnostics.Process.GetCurrentProcess().SessionId + 1));
        source.Current.Snapshot.IsSessionDisconnected.Should().BeFalse();
        bridge.GetSessionEventHistoryForDiagnostics().Should().ContainSingle(e => !e.CurrentSessionMatches);
        bridge.ProcessWindowSignalForTests(new(DesktopWindowSignalKind.SessionDisconnected));
        bridge.ProcessWindowSignalForTests(new(DesktopWindowSignalKind.SessionReconnected));
        long stale = source.Current.Revision;
        await WaitForStateAsync(loop, state => state.SystemStateRevision == stale);
        bridge.ProcessWindowSignalForTests(new(DesktopWindowSignalKind.SessionDisconnected));
        await WaitForStateAsync(loop, state => state.SystemStateRevision == source.Current.Revision);
        SessionEnvironmentResult interrupted = await loop.WaitForSessionEnvironmentAsync(stale,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        interrupted.State.Should().Be(SessionEnvironmentTerminalState.Cancelled);
        interrupted.Reason.Should().Be(SessionEnvironmentFailureReason.SessionDisconnected);
        bridge.ProcessWindowSignalForTests(new(DesktopWindowSignalKind.SessionReconnected));
        long current = source.Current.Revision;
        await WaitForStateAsync(loop, state => state.SystemStateRevision == current);
        monitor.RaiseValidationCompleted(systemRevision: stale);
        monitor.RaiseValidationFailure(new IOException("old validation failure"), systemRevision: stale);
        source.Current.Snapshot.IsSessionDisconnected.Should().BeTrue();
        (await loop.WaitForSessionEnvironmentAsync(stale, CancellationToken.None)).State
            .Should().Be(SessionEnvironmentTerminalState.Cancelled);
        monitor.RaiseValidationCompleted(systemRevision: current);
        SessionEnvironmentResult result = await loop.WaitForSessionEnvironmentAsync(current, CancellationToken.None);
        result.State.Should().Be(SessionEnvironmentTerminalState.Succeeded);
        result.Path.Should().Be(SessionEnvironmentPath.Resume);
        runtime.Provider.SessionCount.Should().Be(1);
        runtime.Desktop.Replacements.Should().ContainSingle();
    }
    [Fact]
    public async Task FirstFramePreparedBeforeDisconnectCannotCommitEvenAfterReconnect()
    {
        TestRuntime runtime = new(true, true);
        GatedSessionCoordinator? gate = null;
        await using HostCommandLoop loop = runtime.CreateCommandLoop(inner =>
            gate = new GatedSessionCoordinator(inner, blockedPrepareCall: 1));
        await SetTopologyAsync(loop);
        HostCommandResult first = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await gate!.Prepared.WaitAsync(TimeSpan.FromSeconds(2));
        FakeRendererSession previous = await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);
        await loop.SendAsync(SessionState(2, true), CancellationToken.None);
        await loop.SendAsync(SessionState(3, false), CancellationToken.None);
        await WaitForStateAsync(loop, snapshot => snapshot.Sessions.Count == 1);
        gate.Release();
        await WaitUntilAsync(() => previous.IsDisposed, CancellationToken.None);
        runtime.Desktop.Replacements.Should().ContainSingle();
        (await loop.WaitForApplyGenerationAsync(first.State!.Generation, CancellationToken.None))
            .FailureReason.Should().Be(ApplyFailureReason.SessionUnavailable);
    }

    [Fact]
    public async Task StableSessionReconnectPreservesIdentityPauseAndDoesNotRepeatCommands()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult apply = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(apply.State!.Generation, CancellationToken.None);
        await loop.SendAsync(new ChangePlaybackIntentCommand(UserPlaybackIntent.Pause), CancellationToken.None);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);
        await loop.SendAsync(SessionState(2, true), CancellationToken.None);
        int commands = renderer.Commands.Count;
        await loop.SendAsync(SessionState(3, true), CancellationToken.None);
        renderer.Commands.Count.Should().Be(commands);
        await loop.SendAsync(SessionState(4, false), CancellationToken.None);
        commands = renderer.Commands.Count;
        await loop.SendAsync(SessionState(5, false), CancellationToken.None);
        renderer.Commands.Count.Should().Be(commands);
        HostStateSnapshot resumed = await GetStateAsync(loop);
        resumed.Sessions[0].Session.Id.Should().Be(renderer.Id);
        resumed.Sessions[0].Session.State.Should().Be(PlaybackState.Paused);
        resumed.Generation.Should().Be(1);
        runtime.Desktop.Replacements.Should().ContainSingle();
    }
    [Fact]
    public async Task DisconnectCancelsUncommittedApplyAndReconnectRebuildsRetainedAssignment()
    {
        TestRuntime runtime = new(false, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult apply = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        FakeRendererSession provisional = await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);
        await WaitUntilAsync(() => provisional.Commands.OfType<LoadRendererContent>().Any(), CancellationToken.None);
        await loop.SendAsync(SessionState(2, true), CancellationToken.None);
        provisional.PresentFirstFrame();
        ApplyGenerationResult canceled = await loop.WaitForApplyGenerationAsync(apply.State!.Generation, CancellationToken.None);
        canceled.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        canceled.FailureReason.Should().Be(ApplyFailureReason.SessionUnavailable);
        await WaitUntilAsync(() => provisional.IsDisposed, CancellationToken.None);
        runtime.Desktop.Replacements.Should().BeEmpty();
        HostStateSnapshot suspended = await GetStateAsync(loop);
        suspended.Sessions.Should().BeEmpty();
        suspended.Assignments.Should().ContainSingle();
        await loop.SendAsync(SessionState(3, false), CancellationToken.None);
        HostStateSnapshot restored = await WaitForStateAsync(loop, state => state.Sessions.Count == 1);
        restored.Sessions[0].Session.Id.Should().NotBe(provisional.Id);
        runtime.Desktop.Replacements.Should().ContainSingle();
    }

    [Fact]
    public async Task DisconnectedTopologyChurnOnlyPlansLatestRevisionOnValidatedReconnect()
    {
        TestRuntime runtime = new(true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult apply = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(apply.State!.Generation, CancellationToken.None);
        await loop.SendAsync(SessionState(2, true), CancellationToken.None);
        DisplayTopology initial = CreateTopology();
        foreach (long revision in new long[] { 2, 3, 4 })
        {
            await loop.SendAsync(new DisplayTopologyChangedCommand(new DisplayTopology(revision,
                [initial.Displays[0] with { Bounds = new DisplayBounds(0, 0, 1600, 900) }])), CancellationToken.None);
        }
        runtime.Provider.SessionCount.Should().Be(1);
        runtime.Desktop.Replacements.Should().ContainSingle();
        await loop.SendAsync(SessionState(3, false), CancellationToken.None);
        DisplayReconciliationResult terminal = await loop.WaitForDisplayReconciliationAsync(4, CancellationToken.None);
        terminal.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        runtime.Provider.SessionCount.Should().Be(2);
        runtime.Desktop.Replacements.Should().HaveCount(2);
        SessionEnvironmentResult sessionTerminal = await loop.WaitForSessionEnvironmentAsync(3, CancellationToken.None);
        sessionTerminal.State.Should().Be(SessionEnvironmentTerminalState.Succeeded);
        sessionTerminal.Path.Should().Be(SessionEnvironmentPath.Reconfigure);
    }

    [Fact]
    public async Task ShellChangesDuringDisconnectDoNotAttachUntilReconnectAndPreservePause()
    {
        TestRuntime runtime = new(true, true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop();
        await SetTopologyAsync(loop);
        HostCommandResult apply = await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await loop.WaitForApplyGenerationAsync(apply.State!.Generation, CancellationToken.None);
        await loop.SendAsync(new ChangePlaybackIntentCommand(UserPlaybackIntent.Pause), CancellationToken.None);
        await loop.SendAsync(SessionState(2, true), CancellationToken.None);
        await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        runtime.Provider.SessionCount.Should().Be(1);
        loop.RecoveryDiagnosticsCountForTests.Should().Be(0);
        await loop.SendAsync(SessionState(3, false), CancellationToken.None);
        HostStateSnapshot resumed = await WaitForStateAsync(loop,
            state => state.Generation > 1 && state.Sessions.Count == 1);
        resumed.Sessions[0].UserIntent.Should().Be(UserPlaybackIntent.Pause);
        resumed.Sessions[0].Session.State.Should().Be(PlaybackState.Paused);
        runtime.Provider.SessionCount.Should().Be(2);

        await loop.SendAsync(new ChangePlaybackIntentCommand(UserPlaybackIntent.Stop), CancellationToken.None);
        await loop.SendAsync(SessionState(4, true), CancellationToken.None);
        await loop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await loop.SendAsync(SessionState(5, false), CancellationToken.None);
        HostStateSnapshot stopped = await WaitForStateAsync(loop,
            state => state.Generation > resumed.Generation && state.Sessions.Count == 1);
        stopped.Sessions[0].UserIntent.Should().Be(UserPlaybackIntent.Stop);
        stopped.Sessions[0].Session.State.Should().Be(PlaybackState.Stopped);
        runtime.Provider.SessionCount.Should().Be(3);
    }

    private static SystemStateChangedCommand SessionState(long revision, bool disconnected) => new(
        new SystemStateChange(revision, new SystemStateSnapshot(false, false, disconnected, false, false, false),
            disconnected ? SystemStateChangeReason.SessionDisconnected : SystemStateChangeReason.SessionReconnected));

    [Fact]
    public async Task ApplyGenerationPublishesTerminalResultAndOrderedTimeline()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        HostCommandResult accepted = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        long generation = accepted.State!.Generation;
        ApplyGenerationResult result = await commandLoop.WaitForApplyGenerationAsync(
            generation,
            CancellationToken.None);

        result.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        result.FailureReason.Should().Be(ApplyFailureReason.None);
        result.Timeline.Select(entry => entry.Phase).Should().ContainInOrder(
            ApplyGenerationPhase.TopologyReady,
            ApplyGenerationPhase.SurfaceCreated,
            ApplyGenerationPhase.Initialized,
            ApplyGenerationPhase.AttachSent,
            ApplyGenerationPhase.SurfaceAttached,
            ApplyGenerationPhase.LoadSent,
            ApplyGenerationPhase.ContentLoaded,
            ApplyGenerationPhase.FirstFramePresented,
            ApplyGenerationPhase.SurfacesReplaced);
        result.Timeline.Should().OnlyContain(entry => entry.Generation == generation);
        result.Timeline.Where(entry => entry.SurfaceOrdinal > 0)
            .Should().OnlyContain(entry => entry.SurfaceOrdinal == 1);
        commandLoop.IsApplyDeadlineDisposedForDiagnostics(generation).Should().BeTrue();
    }

    [Fact]
    public async Task ActiveSessionRetiresGracefullyAfterOriginalApplyDeadlineHasElapsed()
    {
        ApplyDeadlineBudget shortApplyBudget = CreateShortApplyBudget();
        TestRuntime runtime = new(
            TimeSpan.FromMilliseconds(100),
            shortApplyBudget,
            true,
            true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        HostCommandResult firstApply = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            firstApply.State!.Generation,
            CancellationToken.None);
        FakeRendererSession firstRenderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        await Task.Delay(350);

        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 2 && state.Sessions.Count == 1);
        await WaitUntilAsync(() => firstRenderer.IsDisposed, CancellationToken.None);

        firstRenderer.Commands.OfType<ShutdownRenderer>().Should().ContainSingle();
        firstRenderer.ShutdownCompletedCount.Should().Be(1);
        commandLoop.GetRetirementHistoryForDiagnostics()
            .SelectMany(batch => batch.Sessions)
            .Should().ContainSingle(result =>
                result.SessionId == firstRenderer.Id && result.Graceful);
    }

    [Fact]
    public async Task DiagnosticLoopCanWaitForEachReplacedSessionRetirement()
    {
        TestRuntime runtime = new(true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        HostCommandResult first = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            first.State!.Generation,
            CancellationToken.None);
        FakeRendererSession firstRenderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        HostCommandResult second = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            second.State!.Generation,
            CancellationToken.None);
        SessionRetirementBatchResult retired =
            await commandLoop.WaitForSessionRetirementsForDiagnosticsAsync(
                [firstRenderer.Id],
                CancellationToken.None);

        SessionRetirementResult result = retired.Sessions.Should().ContainSingle().Subject;
        result.SessionId.Should().Be(firstRenderer.Id);
        result.Graceful.Should().BeTrue();
        result.OwnedResourcesCleaned.Should().BeTrue();
        firstRenderer.Commands.OfType<ShutdownRenderer>().Should().ContainSingle();
    }

    [Fact]
    public async Task DualDisplayLoopReplacementWaitsForBothPreviousSessions()
    {
        TestRuntime runtime = new(true, true, true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateDualDisplayTopology()),
            CancellationToken.None);
        ApplyWallpaperCommand apply = new(
            new WallpaperId("sample.wallpaper"),
            [new DisplayId("primary"), new DisplayId("secondary")],
            LayoutMode.PerDisplay,
            FitMode.Cover);

        HostCommandResult first = await commandLoop.SendAsync(apply, CancellationToken.None);
        ApplyGenerationResult firstTerminal = await commandLoop.WaitForApplyGenerationAsync(
            first.State!.Generation,
            CancellationToken.None);
        FakeRendererSession firstPrimary = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        FakeRendererSession firstSecondary = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);

        HostCommandResult second = await commandLoop.SendAsync(apply, CancellationToken.None);
        ApplyGenerationResult secondTerminal = await commandLoop.WaitForApplyGenerationAsync(
            second.State!.Generation,
            CancellationToken.None);
        SessionRetirementBatchResult retired =
            await commandLoop.WaitForSessionRetirementsForDiagnosticsAsync(
                [firstPrimary.Id, firstSecondary.Id],
                CancellationToken.None);
        HostStateSnapshot active = await GetStateAsync(commandLoop);

        firstTerminal.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        secondTerminal.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        foreach (long generation in new[] { firstTerminal.Generation, secondTerminal.Generation })
        {
            IReadOnlyList<ApplyTimelineEntry> timeline =
                commandLoop.GetApplyTimelineForDiagnostics(generation);
            timeline.Count(entry =>
                entry.Phase == ApplyGenerationPhase.FirstFramePresented).Should().Be(2);
            timeline.Count(entry =>
                entry.Phase == ApplyGenerationPhase.SurfacesReplaced).Should().Be(1);
        }
        retired.Sessions.Should().HaveCount(2).And.OnlyContain(result =>
            result.Graceful && result.OwnedResourcesCleaned);
        retired.Sessions.Select(result => result.SessionId).Should().BeEquivalentTo(
            [firstPrimary.Id, firstSecondary.Id]);
        firstPrimary.Commands.OfType<ShutdownRenderer>().Should().ContainSingle();
        firstSecondary.Commands.OfType<ShutdownRenderer>().Should().ContainSingle();
        active.Generation.Should().Be(secondTerminal.Generation);
        active.Sessions.Should().HaveCount(2);
        runtime.Desktop.Replacements.Should().HaveCount(2).And.OnlyContain(replacement =>
            replacement.ProvisionalSurfaceIds.Count == 2);
    }

    [Fact]
    public async Task DualDisplaySpanUsesOnePhysicalPixelRendererSurface()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        DisplayTopology topology = CreateDualDisplayTopology() with { Revision = 1 };
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(topology),
            CancellationToken.None);

        HostCommandResult applied = await commandLoop.SendAsync(
            new ApplyWallpaperCommand(
                new WallpaperId("sample.wallpaper"),
                topology.Displays.Select(display => display.Id).ToArray(),
                LayoutMode.Span,
                FitMode.Cover),
            CancellationToken.None);
        ApplyGenerationResult terminal = await commandLoop.WaitForApplyGenerationAsync(
            applied.State!.Generation,
            CancellationToken.None);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        HostStateSnapshot state = await GetStateAsync(commandLoop);

        terminal.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        state.Sessions.Should().ContainSingle();
        state.Sessions.Single().Session.Displays.Should()
            .BeEquivalentTo(topology.Displays.Select(display => display.Id));
        renderer.Commands.OfType<AttachRendererSurface>().Should().ContainSingle()
            .Which.ScaleFactor.Should().Be(1);
        runtime.Desktop.Replacements.Should().ContainSingle()
            .Which.ProvisionalSurfaceIds.Should().ContainSingle();
    }

    [Fact]
    public async Task FailedSpanReplacementPreservesPreviouslyCommittedSpan()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, false);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        DisplayTopology topology = CreateDualDisplayTopology() with { Revision = 1 };
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(topology),
            CancellationToken.None);
        HostCommandResult applied = await commandLoop.SendAsync(
            new ApplyWallpaperCommand(
                new WallpaperId("sample.wallpaper"),
                topology.Displays.Select(display => display.Id).ToArray(),
                LayoutMode.Span,
                FitMode.Cover),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            applied.State!.Generation,
            CancellationToken.None);
        FakeRendererSession committed = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        DisplayTopology changed = topology with
        {
            Revision = 2,
            Displays = topology.Displays.Select(display =>
                display.Id == new DisplayId("secondary")
                    ? display with
                    {
                        Bounds = new DisplayBounds(
                            display.Bounds.X,
                            display.Bounds.Y + 100,
                            display.Bounds.Width,
                            display.Bounds.Height),
                    }
                    : display).ToArray(),
        };
        HostCommandResult change = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(changed),
            CancellationToken.None);
        ApplyGenerationResult failed = await commandLoop.WaitForApplyGenerationAsync(
            change.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult reconciliation =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        HostStateSnapshot state = await GetStateAsync(commandLoop);

        failed.State.Should().Be(ApplyGenerationTerminalState.Failed);
        failed.FailureReason.Should().Be(ApplyFailureReason.FirstFrameTimeout);
        reconciliation.TerminalState.Should().Be(DisplayReconciliationTerminalState.Failed);
        state.Sessions.Should().ContainSingle()
            .Which.Session.Id.Should().Be(committed.Id);
        committed.IsDisposed.Should().BeFalse();
        runtime.Desktop.Replacements.Should().ContainSingle();
    }

    [Fact]
    public async Task SpanIntersectionIntroducedByTopologyChangeFailsClosed()
    {
        DisplayId left = new("left");
        DisplayId right = new("right");
        DisplayId middle = new("middle");
        DisplayDescriptor leftDisplay = new(
            left,
            "device-left",
            new DisplayBounds(0, 0, 100, 100),
            1,
            60,
            true);
        DisplayDescriptor rightDisplay = new(
            right,
            "device-right",
            new DisplayBounds(200, 0, 100, 100),
            1,
            60,
            false);
        DisplayTopology initialTopology = new(1, [leftDisplay, rightDisplay]);
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(initialTopology),
            CancellationToken.None);
        HostCommandResult applied = await commandLoop.SendAsync(
            new ApplyWallpaperCommand(
                new WallpaperId("sample.wallpaper"),
                [left, right],
                LayoutMode.Span,
                FitMode.Cover),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            applied.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot before = await GetStateAsync(commandLoop);

        DisplayDescriptor middleDisplay = new(
            middle,
            "device-middle",
            new DisplayBounds(100, 20, 100, 50),
            1,
            60,
            false);
        HostCommandResult rejected = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                2,
                [leftDisplay, middleDisplay, rightDisplay])),
            CancellationToken.None);
        DisplayReconciliationResult reconciliation =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        HostStateSnapshot after = await GetStateAsync(commandLoop);

        rejected.Accepted.Should().BeFalse();
        rejected.ErrorCode.Should().Be("layout.span.intersects_non_member_display");
        reconciliation.TerminalState.Should().Be(DisplayReconciliationTerminalState.Failed);
        reconciliation.FailureReason.Should().Be(ApplyFailureReason.TopologyFailed);
        after.DisplayTopology!.Revision.Should().Be(1);
        after.Sessions.Select(session => session.Session.Id).Should()
            .Equal(before.Sessions.Select(session => session.Session.Id));
        runtime.Provider.SessionCount.Should().Be(1);
        runtime.Desktop.Replacements.Should().ContainSingle();
    }

    [Fact]
    public async Task DisplayDisconnectRetiresOnlyThatSessionAndReconnectRestoresOfflineAssignment()
    {
        TestRuntime runtime = new(true, true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateDualDisplayTopology()),
            CancellationToken.None);
        HostCommandResult initial = await commandLoop.SendAsync(
            new ApplyWallpaperCommand(
                new WallpaperId("sample.wallpaper"),
                [new DisplayId("primary"), new DisplayId("secondary")],
                LayoutMode.PerDisplay,
                FitMode.Cover),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        FakeRendererSession originalPrimary = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        FakeRendererSession originalSecondary = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);

        HostCommandResult disconnectedCommand = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                3,
                [CreateDualDisplayTopology().Displays.Single(display => display.IsPrimary)])),
            CancellationToken.None);
        DisplayReconciliationResult disconnectedResult =
            await commandLoop.WaitForDisplayReconciliationAsync(3, CancellationToken.None);
        await WaitUntilAsync(() => originalSecondary.IsDisposed, CancellationToken.None);
        HostStateSnapshot disconnected = await GetStateAsync(commandLoop);

        disconnected.Sessions.Should().ContainSingle()
            .Which.Session.Id.Should().Be(originalPrimary.Id);
        disconnected.Assignments.Should().HaveCount(2);
        originalPrimary.IsDisposed.Should().BeFalse();
        disconnectedCommand.Accepted.Should().BeTrue();
        disconnectedResult.TerminalState.Should()
            .Be(DisplayReconciliationTerminalState.Succeeded);
        disconnectedResult.Kind.Should().Be(DisplayReconciliationKind.Reconfigure);

        HostCommandResult reconnected = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateDualDisplayTopology() with { Revision = 4 }),
            CancellationToken.None);
        ApplyGenerationResult terminal = await commandLoop.WaitForApplyGenerationAsync(
            reconnected.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult reconnectedResult =
            await commandLoop.WaitForDisplayReconciliationAsync(4, CancellationToken.None);
        HostStateSnapshot restored = await GetStateAsync(commandLoop);

        terminal.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        terminal.Timeline.Count(entry =>
            entry.Phase == ApplyGenerationPhase.FirstFramePresented).Should().Be(1);
        restored.Sessions.Should().HaveCount(2);
        restored.Sessions.Should().Contain(item => item.Session.Id == originalPrimary.Id);
        restored.Assignments.Should().HaveCount(2);
        reconnectedResult.TerminalState.Should()
            .Be(DisplayReconciliationTerminalState.Succeeded);
        reconnectedResult.Timeline.Should().Contain(entry =>
            entry.Phase == DisplayReconciliationPhase.SurfacesReplaced);
    }

    [Fact]
    public async Task DisplayBoundsChangeAtomicallyReplacesOnlyAffectedSession()
    {
        TestRuntime runtime = new(true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult initial = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        FakeRendererSession original = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        HostCommandResult changed = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                2,
                [CreateTopology().Displays[0] with
                {
                    Bounds = new DisplayBounds(-100, 50, 2560, 1440),
                }])),
            CancellationToken.None);
        ApplyGenerationResult terminal = await commandLoop.WaitForApplyGenerationAsync(
            changed.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult reconciliation =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        await WaitUntilAsync(() => original.IsDisposed, CancellationToken.None);
        HostStateSnapshot state = await GetStateAsync(commandLoop);

        terminal.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        terminal.Timeline.Count(entry =>
            entry.Phase == ApplyGenerationPhase.SurfacesReplaced).Should().Be(1);
        state.Sessions.Should().ContainSingle();
        state.Sessions[0].Session.Id.Should().NotBe(original.Id);
        runtime.Desktop.Replacements.Last().ReplacedSurfaceIds
            .Should().Equal(new SurfaceId("surface-1"));
        reconciliation.TerminalState.Should()
            .Be(DisplayReconciliationTerminalState.Succeeded);
        reconciliation.HostGeneration.Should().Be(changed.State.Generation);
        reconciliation.Timeline.Should().Contain(entry =>
            entry.Phase == DisplayReconciliationPhase.ActiveSurfacesResized &&
            entry.Outcome == ApplyStageOutcome.Success);
        original.Commands.Any(command => command is SetRendererBounds bounds &&
                bounds.Generation == changed.State.Generation &&
                bounds.Bounds == new DisplayBounds(-100, 50, 2560, 1440))
            .Should().BeTrue();
        runtime.Desktop.ResizedSurfaces.Should().ContainSingle(resize =>
            resize.SurfaceId == new SurfaceId("surface-1") &&
            resize.Request.Bounds == new DisplayBounds(-100, 50, 2560, 1440));
    }

    [Fact]
    public async Task DuplicateDisplayTopologyRevisionIsRejectedWithoutChangingState()
    {
        TestRuntime runtime = new();
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        HostCommandResult duplicate = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateTopology()),
            CancellationToken.None);

        duplicate.Accepted.Should().BeFalse();
        duplicate.ErrorCode.Should().Be("host.display.topology_stale");
        (await GetStateAsync(commandLoop)).DisplayTopology!.Revision.Should().Be(1);
    }

    [Fact]
    public async Task CompletedDisplayReconciliationJournalIsBounded()
    {
        TestRuntime runtime = new();
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        for (long revision = 2; revision <= 35; revision++)
        {
            DisplayDescriptor display = CreateTopology().Displays[0] with
            {
                RefreshRateHz = revision % 2 == 0 ? 120 : 60,
            };
            await commandLoop.SendAsync(
                new DisplayTopologyChangedCommand(new DisplayTopology(revision, [display])),
                CancellationToken.None);
            DisplayReconciliationResult result =
                await commandLoop.WaitForDisplayReconciliationAsync(
                    revision,
                    CancellationToken.None);
            result.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        }

        commandLoop.DisplayReconciliationDiagnosticsCountForTests.Should().Be(32);
    }

    [Fact]
    public async Task RefreshRateOnlyTopologyChangeCompletesAsNoChange()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult initial = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                2,
                [CreateTopology().Displays[0] with { RefreshRateHz = 120 }])),
            CancellationToken.None);
        DisplayReconciliationResult result =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);

        result.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        result.Kind.Should().Be(DisplayReconciliationKind.NoChange);
        result.HostGeneration.Should().BeNull();
        renderer.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public async Task PrimaryFlagOnlyTopologyChangePreservesDualDisplaySessionsWithoutReplace()
    {
        TestRuntime runtime = new(true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        DisplayTopology initialTopology = CreateDualDisplayTopology() with { Revision = 1 };
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(initialTopology),
            CancellationToken.None);
        HostCommandResult initial = await commandLoop.SendAsync(
            new ApplyWallpaperCommand(
                new WallpaperId("sample.wallpaper"),
                initialTopology.Displays.Select(display => display.Id).ToArray(),
                LayoutMode.PerDisplay,
                FitMode.Cover),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot before = await GetStateAsync(commandLoop);
        SessionId[] beforeSessionIds = before.Sessions
            .Select(session => session.Session.Id)
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .ToArray();
        FakeRendererSession firstRenderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        FakeRendererSession secondRenderer = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        int replacementsBefore = runtime.Desktop.Replacements.Count;

        DisplayTopology switched = initialTopology with
        {
            Revision = 2,
            Displays = initialTopology.Displays.Select(display => display with
            {
                IsPrimary = display.Id == new DisplayId("secondary"),
            }).ToArray(),
        };
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(switched),
            CancellationToken.None);
        DisplayReconciliationResult result =
            await commandLoop.WaitForDisplayReconciliationAsync(
                switched.Revision,
                CancellationToken.None);
        HostStateSnapshot after = await GetStateAsync(commandLoop);

        result.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        result.Kind.Should().Be(DisplayReconciliationKind.NoChange);
        result.HostGeneration.Should().BeNull();
        result.Timeline.Should().Contain(entry =>
            entry.Phase == DisplayReconciliationPhase.PlanCreated &&
            entry.Detail != null &&
            entry.Detail.Contains(
                nameof(DisplayTopologyChangeReason.PrimaryChanged),
                StringComparison.Ordinal));
        result.Timeline.Should().NotContain(entry =>
            entry.Phase == DisplayReconciliationPhase.SurfacesReplaced);
        after.Sessions.Select(session => session.Session.Id)
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .Should().Equal(beforeSessionIds);
        runtime.Desktop.Replacements.Should().HaveCount(replacementsBefore);
        firstRenderer.IsDisposed.Should().BeFalse();
        secondRenderer.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public async Task ScaleOnlyTopologyChangeReplacesAffectedDisplayAndPreservesOtherSession()
    {
        TestRuntime runtime = new(true, true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        DisplayTopology initialTopology = CreateDualDisplayTopology() with { Revision = 1 };
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(initialTopology),
            CancellationToken.None);
        HostCommandResult initial = await commandLoop.SendAsync(
            new ApplyWallpaperCommand(
                new WallpaperId("sample.wallpaper"),
                initialTopology.Displays.Select(display => display.Id).ToArray(),
                LayoutMode.PerDisplay,
                FitMode.Cover),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot before = await GetStateAsync(commandLoop);
        DisplayId primaryId = new("primary");
        DisplayId secondaryId = new("secondary");
        SessionId primarySessionId = before.Sessions.Single(session =>
            session.Session.Displays.Contains(primaryId)).Session.Id;
        SessionId secondarySessionId = before.Sessions.Single(session =>
            session.Session.Displays.Contains(secondaryId)).Session.Id;
        FakeRendererSession primaryRenderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        FakeRendererSession secondaryRenderer = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        int replacementsBefore = runtime.Desktop.Replacements.Count;

        DisplayTopology scaled = initialTopology with
        {
            Revision = 2,
            Displays = initialTopology.Displays.Select(display =>
                display.Id == secondaryId
                    ? display with { ScaleFactor = 1.75 }
                    : display).ToArray(),
        };
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(scaled),
            CancellationToken.None);
        DisplayReconciliationResult result =
            await commandLoop.WaitForDisplayReconciliationAsync(
                scaled.Revision,
                CancellationToken.None);
        await WaitUntilAsync(() => secondaryRenderer.IsDisposed, CancellationToken.None);
        HostStateSnapshot after = await GetStateAsync(commandLoop);

        result.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        result.Kind.Should().Be(DisplayReconciliationKind.Reconfigure);
        result.Timeline.Should().ContainSingle(entry =>
            entry.Phase == DisplayReconciliationPhase.SurfacesReplaced);
        after.Sessions.Should().HaveCount(2);
        after.Sessions.Single(session => session.Session.Displays.Contains(primaryId))
            .Session.Id.Should().Be(primarySessionId);
        after.Sessions.Single(session => session.Session.Displays.Contains(secondaryId))
            .Session.Id.Should().NotBe(secondarySessionId);
        runtime.Desktop.Replacements.Should().HaveCount(replacementsBefore + 1);
        primaryRenderer.IsDisposed.Should().BeFalse();
        secondaryRenderer.IsDisposed.Should().BeTrue();
        runtime.Provider.SessionCount.Should().Be(3);
    }

    [Fact]
    public async Task ZeroDisplayTopologySuspendsSessionsAndPreservesAssignments()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult initial = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(2, [])),
            CancellationToken.None);
        DisplayReconciliationResult result =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        await WaitUntilAsync(() => renderer.IsDisposed, CancellationToken.None);
        HostStateSnapshot suspended = await GetStateAsync(commandLoop);

        result.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        result.Kind.Should().Be(DisplayReconciliationKind.Suspend);
        suspended.Sessions.Should().BeEmpty();
        suspended.Assignments.Should().ContainSingle();
    }

    [Fact]
    public async Task ZeroDisplayRoundTripPreservesAssignmentsAndRestoresOriginalDisplaysAtomically()
    {
        TestRuntime runtime = new(true, true, true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        DisplayTopology initialTopology = CreateDualDisplayTopology() with { Revision = 1 };
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(initialTopology),
            CancellationToken.None);
        HostCommandResult initial = await commandLoop.SendAsync(
            new ApplyWallpaperCommand(
                new WallpaperId("sample.wallpaper"),
                initialTopology.Displays.Select(display => display.Id).ToArray(),
                LayoutMode.PerDisplay,
                FitMode.Cover),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        FakeRendererSession first = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        FakeRendererSession second = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);

        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(2, [])),
            CancellationToken.None);
        DisplayReconciliationResult suspendedResult =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        await WaitUntilAsync(
            () => first.IsDisposed && second.IsDisposed,
            CancellationToken.None);
        HostStateSnapshot suspended = await GetStateAsync(commandLoop);

        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(initialTopology with { Revision = 3 }),
            CancellationToken.None);
        DisplayReconciliationResult resumedResult =
            await commandLoop.WaitForDisplayReconciliationAsync(3, CancellationToken.None);
        HostStateSnapshot resumed = await GetStateAsync(commandLoop);

        suspendedResult.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        suspendedResult.Kind.Should().Be(DisplayReconciliationKind.Suspend);
        suspended.Sessions.Should().BeEmpty();
        suspended.Assignments.Should().HaveCount(2);
        resumedResult.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        resumedResult.Kind.Should().Be(DisplayReconciliationKind.Reconfigure);
        resumedResult.Timeline.Count(entry =>
            entry.Phase == DisplayReconciliationPhase.SurfacesReplaced).Should().Be(1);
        resumed.Sessions.Should().HaveCount(2);
        resumed.Assignments.Select(item => item.DisplayId)
            .Should().BeEquivalentTo(initialTopology.Displays.Select(item => item.Id));
        runtime.Desktop.Replacements.Should().HaveCount(2);
    }

    [Fact]
    public async Task FailedTopologyPreparationKeepsPreviouslyCommittedSession()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, false);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult initial = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        FakeRendererSession original = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        HostCommandResult changed = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                2,
                [CreateTopology().Displays[0] with
                {
                    Bounds = new DisplayBounds(0, 0, 2560, 1440),
                }])),
            CancellationToken.None);
        ApplyGenerationResult apply = await commandLoop.WaitForApplyGenerationAsync(
            changed.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult reconciliation =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        HostStateSnapshot state = await GetStateAsync(commandLoop);

        apply.State.Should().Be(ApplyGenerationTerminalState.Failed);
        reconciliation.TerminalState.Should().Be(DisplayReconciliationTerminalState.Failed);
        reconciliation.FailureReason.Should().Be(ApplyFailureReason.FirstFrameTimeout);
        state.Sessions.Should().ContainSingle()
            .Which.Session.Id.Should().Be(original.Id);
        original.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public async Task TransientShellTopologyMutationIsRetriedBeforeReconciliationFails()
    {
        TestRuntime runtime = new(true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult initial = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        runtime.Desktop.TransientTopologyFailuresRemaining = 1;

        HostCommandResult changed = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                2,
                [CreateTopology().Displays[0] with
                {
                    Bounds = new DisplayBounds(0, 0, 2560, 1440),
                }])),
            CancellationToken.None);
        ApplyGenerationResult apply = await commandLoop.WaitForApplyGenerationAsync(
            changed.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult reconciliation =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);

        apply.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        reconciliation.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        reconciliation.Timeline.Should().Contain(entry =>
            entry.Phase == DisplayReconciliationPhase.PreparationRetryScheduled &&
            entry.FailureReason == ApplyFailureReason.TopologyFailed);
        reconciliation.Timeline.Should().Contain(entry =>
            entry.Phase == DisplayReconciliationPhase.PreparationRetryStarted);
        runtime.Desktop.EnsureTopologyCallCount.Should().Be(3);
    }

    [Fact]
    public async Task ParentCoordinateMutationAtReplaceAbandonsProvisionalBatchAndRetries()
    {
        TestRuntime runtime = new(true, true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult initial = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        FakeRendererSession original = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        runtime.Desktop.TransientTopologyFailuresRemaining = 3;
        runtime.Desktop.TransientReplacementFailuresRemaining = 1;

        HostCommandResult changed = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                2,
                [CreateTopology().Displays[0] with
                {
                    Bounds = new DisplayBounds(0, 0, 1080, 1920),
                }])),
            CancellationToken.None);
        ApplyGenerationResult apply = await commandLoop.WaitForApplyGenerationAsync(
            changed.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult reconciliation =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        FakeRendererSession abandoned = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        FakeRendererSession committed = await runtime.Provider.WaitForSessionAsync(
            2,
            CancellationToken.None);
        await WaitUntilAsync(
            () => abandoned.IsDisposed && original.IsDisposed,
            CancellationToken.None);
        HostStateSnapshot state = await GetStateAsync(commandLoop);

        apply.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        reconciliation.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        reconciliation.Timeline.Should().Contain(entry =>
            entry.Phase == DisplayReconciliationPhase.PreparationRetryScheduled &&
            entry.FailureReason == ApplyFailureReason.TopologyFailed &&
            entry.Detail!.Contains("coordinate space", StringComparison.OrdinalIgnoreCase));
        reconciliation.Timeline.Should().Contain(entry =>
            entry.Phase == DisplayReconciliationPhase.PreparationRetryStarted);
        abandoned.IsDisposed.Should().BeTrue();
        state.Sessions.Should().ContainSingle()
            .Which.Session.Id.Should().Be(committed.Id);
        runtime.Desktop.Replacements.Should().HaveCount(3);
        runtime.Desktop.EnsureTopologyCallCount.Should().Be(6,
            "preparation and Replace mutation retries have independent bounded budgets");
    }

    [Fact]
    public async Task NewerTopologyCancelsStalePreparationAndCommitsOnlyLatestBounds()
    {
        TestRuntime runtime = new(true, false, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult initial = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);

        HostCommandResult stale = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                2,
                [CreateTopology().Displays[0] with
                {
                    Bounds = new DisplayBounds(10, 0, 1920, 1080),
                }])),
            CancellationToken.None);
        FakeRendererSession staleRenderer = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        HostCommandResult latest = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                3,
                [CreateTopology().Displays[0] with
                {
                    Bounds = new DisplayBounds(-100, 50, 2560, 1440),
                }])),
            CancellationToken.None);

        ApplyGenerationResult staleTerminal = await commandLoop.WaitForApplyGenerationAsync(
            stale.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult staleReconciliation =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        ApplyGenerationResult latestTerminal = await commandLoop.WaitForApplyGenerationAsync(
            latest.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult latestReconciliation =
            await commandLoop.WaitForDisplayReconciliationAsync(3, CancellationToken.None);
        await WaitUntilAsync(() => staleRenderer.IsDisposed, CancellationToken.None);

        staleTerminal.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        staleTerminal.FailureReason.Should().Be(ApplyFailureReason.Superseded);
        latestTerminal.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        staleReconciliation.TerminalState.Should()
            .Be(DisplayReconciliationTerminalState.Cancelled);
        staleReconciliation.FailureReason.Should().Be(ApplyFailureReason.Superseded);
        latestReconciliation.TerminalState.Should()
            .Be(DisplayReconciliationTerminalState.Succeeded);
        runtime.Desktop.Replacements.Should().HaveCount(2);
        runtime.Desktop.Replacements.Last().ProvisionalSurfaceIds
            .Should().Equal(new SurfaceId("surface-3"));
    }

    [Fact]
    public async Task NewerNoChangeTopologyImmediatelyCancelsStalePreparation()
    {
        TestRuntime runtime = new(true, false);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult initial = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        FakeRendererSession committed = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        HostCommandResult stale = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                2,
                [CreateTopology().Displays[0] with
                {
                    Bounds = new DisplayBounds(0, 0, 2560, 1440),
                }])),
            CancellationToken.None);
        FakeRendererSession provisional = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateTopology() with { Revision = 3 }),
            CancellationToken.None);

        ApplyGenerationResult staleApply = await commandLoop.WaitForApplyGenerationAsync(
            stale.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult staleResult =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        DisplayReconciliationResult finalResult =
            await commandLoop.WaitForDisplayReconciliationAsync(3, CancellationToken.None);
        await WaitUntilAsync(() => provisional.IsDisposed, CancellationToken.None);
        HostStateSnapshot finalState = await GetStateAsync(commandLoop);

        staleApply.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        staleApply.FailureReason.Should().Be(ApplyFailureReason.Superseded);
        staleResult.TerminalState.Should().Be(DisplayReconciliationTerminalState.Cancelled);
        staleResult.FailureReason.Should().Be(ApplyFailureReason.Superseded);
        finalResult.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        finalResult.Kind.Should().Be(DisplayReconciliationKind.NoChange);
        committed.IsDisposed.Should().BeFalse();
        finalState.Sessions.Should().ContainSingle()
            .Which.Session.Id.Should().Be(committed.Id);
        runtime.Desktop.Replacements.Should().ContainSingle();
    }

    [Fact]
    public async Task NewerSuspendTopologyCancelsStalePreparationAndRetiresCommittedSession()
    {
        TestRuntime runtime = new(true, false);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult initial = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        FakeRendererSession committed = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        HostCommandResult stale = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                2,
                [CreateTopology().Displays[0] with
                {
                    Bounds = new DisplayBounds(0, 0, 2560, 1440),
                }])),
            CancellationToken.None);
        FakeRendererSession provisional = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(3, [])),
            CancellationToken.None);

        ApplyGenerationResult staleApply = await commandLoop.WaitForApplyGenerationAsync(
            stale.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult staleResult =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        DisplayReconciliationResult suspendResult =
            await commandLoop.WaitForDisplayReconciliationAsync(3, CancellationToken.None);
        await WaitUntilAsync(
            () => committed.IsDisposed && provisional.IsDisposed,
            CancellationToken.None);
        HostStateSnapshot suspended = await GetStateAsync(commandLoop);

        staleApply.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        staleApply.FailureReason.Should().Be(ApplyFailureReason.Superseded);
        staleResult.TerminalState.Should().Be(DisplayReconciliationTerminalState.Cancelled);
        suspendResult.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        suspendResult.Kind.Should().Be(DisplayReconciliationKind.Suspend);
        suspended.Sessions.Should().BeEmpty();
        suspended.Assignments.Should().ContainSingle();
        runtime.Desktop.Replacements.Should().ContainSingle();
    }

    [Fact]
    public async Task NewerNoChangeTopologyInterruptsTransientRetryDelay()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult initial = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);
        runtime.Desktop.TransientTopologyFailuresRemaining = 10;

        HostCommandResult stale = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                2,
                [CreateTopology().Displays[0] with
                {
                    Bounds = new DisplayBounds(0, 0, 2560, 1440),
                }])),
            CancellationToken.None);
        await WaitUntilAsync(
            () => runtime.Desktop.EnsureTopologyCallCount >= 2,
            CancellationToken.None);
        int callsBeforeSupersede = runtime.Desktop.EnsureTopologyCallCount;
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateTopology() with { Revision = 3 }),
            CancellationToken.None);

        ApplyGenerationResult staleApply = await commandLoop.WaitForApplyGenerationAsync(
            stale.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult staleResult =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        DisplayReconciliationResult finalResult =
            await commandLoop.WaitForDisplayReconciliationAsync(3, CancellationToken.None);
        await Task.Delay(450);

        staleApply.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        staleApply.FailureReason.Should().Be(ApplyFailureReason.Superseded);
        staleResult.TerminalState.Should().Be(DisplayReconciliationTerminalState.Cancelled);
        finalResult.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        finalResult.Kind.Should().Be(DisplayReconciliationKind.NoChange);
        // The superseding no-change revision restores the transition-resized active Surface
        // through the generation-validated fast path. It must not start another full Raised
        // discovery, and the stale preparation retry must remain cancelled.
        runtime.Desktop.EnsureTopologyCallCount.Should().Be(callsBeforeSupersede);
    }

    [Fact]
    public async Task NewerTopologyQueuedWithFirstFramePreventsStalePreparedReplace()
    {
        TestRuntime runtime = new(true, true);
        GatedSessionCoordinator? gate = null;
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop(inner =>
            gate = new GatedSessionCoordinator(inner, blockedPrepareCall: 2));
        await SetTopologyAsync(commandLoop);
        HostCommandResult initial = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            initial.State!.Generation,
            CancellationToken.None);

        HostCommandResult stale = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(new DisplayTopology(
                2,
                [CreateTopology().Displays[0] with
                {
                    Bounds = new DisplayBounds(0, 0, 2560, 1440),
                }])),
            CancellationToken.None);
        await gate!.Prepared.WaitAsync(CancellationToken.None);
        FakeRendererSession provisional = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateTopology() with { Revision = 3 }),
            CancellationToken.None);
        gate.Release();
        ApplyGenerationResult staleApply = await commandLoop.WaitForApplyGenerationAsync(
            stale.State!.Generation,
            CancellationToken.None);
        DisplayReconciliationResult staleResult =
            await commandLoop.WaitForDisplayReconciliationAsync(2, CancellationToken.None);
        DisplayReconciliationResult finalResult =
            await commandLoop.WaitForDisplayReconciliationAsync(3, CancellationToken.None);
        await WaitUntilAsync(() => provisional.IsDisposed, CancellationToken.None);

        staleApply.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        staleApply.FailureReason.Should().Be(ApplyFailureReason.Superseded);
        staleResult.TerminalState.Should().Be(DisplayReconciliationTerminalState.Cancelled);
        finalResult.TerminalState.Should().Be(DisplayReconciliationTerminalState.Succeeded);
        finalResult.Kind.Should().Be(DisplayReconciliationKind.NoChange);
        runtime.Desktop.Replacements.Should().ContainSingle();
    }

    [Fact]
    public async Task DisplayTopologyForwarderReportsClosedHostChannel()
    {
        TestRuntime runtime = new(true);
        HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        DisplayTopologyEventForwarder forwarder = new(commandLoop);
        DisplayTopologyForwardingFailure? observed = null;
        forwarder.ForwardingFailed += (_, failure) => observed = failure;
        await commandLoop.DisposeAsync();

        bool forwarded = forwarder.TryForward(CreateTopology());

        forwarded.Should().BeFalse();
        forwarder.LastFailure.Should().NotBeNull();
        forwarder.LastFailure!.Topology.Should().BeEquivalentTo(CreateTopology());
        observed.Should().Be(forwarder.LastFailure);
        observed!.Reason.Should().Contain("closed");
    }

    [Fact]
    public async Task HostDisposeGracefullyRetiresSessionAfterOriginalApplyDeadlineHasElapsed()
    {
        TestRuntime runtime = new(
            TimeSpan.FromMilliseconds(100),
            CreateShortApplyBudget(),
            true);
        HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult apply = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            apply.State!.Generation,
            CancellationToken.None);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        await Task.Delay(350);

        await commandLoop.DisposeAsync();

        renderer.Commands.OfType<ShutdownRenderer>().Should().ContainSingle();
        renderer.ShutdownCompletedCount.Should().Be(1);
        renderer.IsDisposed.Should().BeTrue();
        commandLoop.RetainedApplyDiagnosticsCount.Should().Be(0);
        commandLoop.GetRetirementHistoryForDiagnostics()
            .SelectMany(batch => batch.Sessions)
            .Should().ContainSingle(result =>
                result.SessionId == renderer.Id && result.Graceful);
    }

    [Fact]
    public async Task CompletedGenerationJournalsAreBoundedAndEveryRetainedDeadlineIsDisposed()
    {
        TestRuntime runtime = new(Enumerable.Repeat(true, 36).ToArray());
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        long firstGeneration = 0;
        for (int index = 0; index < 36; index++)
        {
            HostCommandResult apply = await commandLoop.SendAsync(
                CreateApplyCommand(),
                CancellationToken.None);
            long generation = apply.State!.Generation;
            firstGeneration = firstGeneration == 0 ? generation : firstGeneration;
            await commandLoop.WaitForApplyGenerationAsync(generation, CancellationToken.None);
        }

        commandLoop.RetainedApplyDiagnosticsCount.Should().Be(32);
        Func<Task> readPruned = () => commandLoop.WaitForApplyGenerationAsync(
            firstGeneration,
            CancellationToken.None);
        await readPruned.Should().ThrowAsync<InvalidOperationException>();
        for (long generation = 5; generation <= 36; generation++)
        {
            commandLoop.IsApplyDeadlineDisposedForDiagnostics(generation).Should().BeTrue();
        }
    }

    [Fact]
    public async Task RetirementTimeoutIsRecordedBeforeForcedOwnedResourceCleanup()
    {
        TestRuntime runtime = new(
            TimeSpan.FromMilliseconds(100),
            ApplyDeadlineBudget.Default,
            new SessionRetirementPolicy(
                TimeSpan.FromMilliseconds(30),
                TimeSpan.FromMilliseconds(30)),
            true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        FakeRendererSession renderer = new(
            new SessionId("retirement-timeout"),
            7,
            autoPresentFirstFrame: true)
        {
            SuppressShutdownCompleted = true,
        };

        SessionRetirementBatchResult batch = await runtime.Coordinator.RetireAsync(
            [CreatePreparedSession(renderer, "primary", "retirement-timeout-surface", 1)],
            CancellationToken.None);

        SessionRetirementResult result = batch.Sessions.Should().ContainSingle().Subject;
        result.ShutdownSent.Should().BeTrue();
        result.ShutdownCompleted.Should().BeFalse();
        result.Issues.Should().ContainSingle(issue =>
            issue.Kind == SessionRetirementIssueKind.ShutdownTimeout);
        result.RendererDisposed.Should().BeTrue();
        result.SurfacesCleaned.Should().BeTrue();
        batch.Graceful.Should().BeFalse();
        batch.OwnedResourcesCleaned.Should().BeTrue();
        runtime.Desktop.DestroyedSurfaces.Should().Contain(
            new SurfaceId("retirement-timeout-surface"));
    }

    [Fact]
    public async Task TwoSessionRetirementBudgetsAreIndependentAndBatchResultsAggregate()
    {
        TestRuntime runtime = new(
            TimeSpan.FromMilliseconds(100),
            ApplyDeadlineBudget.Default,
            new SessionRetirementPolicy(
                TimeSpan.FromMilliseconds(30),
                TimeSpan.FromMilliseconds(30)),
            true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        FakeRendererSession slow = new(new SessionId("slow"), 9, true)
        {
            SuppressShutdownCompleted = true,
        };
        FakeRendererSession healthy = new(new SessionId("healthy"), 9, true);

        SessionRetirementBatchResult batch = await runtime.Coordinator.RetireAsync(
            [
                CreatePreparedSession(slow, "primary", "surface-a", 1),
                CreatePreparedSession(healthy, "secondary", "surface-b", 2),
            ],
            CancellationToken.None);

        batch.Sessions.Should().HaveCount(2);
        batch.Sessions.Single(item => item.SessionId == slow.Id)
            .Issues.Should().ContainSingle(issue =>
                issue.Kind == SessionRetirementIssueKind.ShutdownTimeout);
        batch.Sessions.Single(item => item.SessionId == healthy.Id)
            .Graceful.Should().BeTrue();
        batch.Graceful.Should().BeFalse();
        batch.OwnedResourcesCleaned.Should().BeTrue();
        slow.IsDisposed.Should().BeTrue();
        healthy.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task MultiSessionRetirementStartsRendererShutdownsInParallel()
    {
        TestRuntime runtime = new(
            TimeSpan.FromMilliseconds(100),
            ApplyDeadlineBudget.Default,
            new SessionRetirementPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)),
            true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        FakeRendererSession first = new(new SessionId("parallel-a"), 10, true)
        {
            SuppressShutdownCompleted = true,
        };
        FakeRendererSession second = new(new SessionId("parallel-b"), 10, true)
        {
            SuppressShutdownCompleted = true,
        };
        using CancellationTokenSource cancellation = new();

        Task<SessionRetirementBatchResult> retirement = runtime.Coordinator.RetireAsync(
            [
                CreatePreparedSession(first, "primary", "parallel-surface-a", 1),
                CreatePreparedSession(second, "secondary", "parallel-surface-b", 2),
            ],
            cancellation.Token);
        await WaitUntilAsync(
            () => first.Commands.OfType<ShutdownRenderer>().Any() &&
                second.Commands.OfType<ShutdownRenderer>().Any(),
            CancellationToken.None,
            maximumAttempts: 50);
        cancellation.Cancel();
        SessionRetirementBatchResult result = await retirement;

        result.Sessions.Should().HaveCount(2);
        result.Sessions.Should().OnlyContain(item =>
            item.Issues.Any(issue => issue.Kind == SessionRetirementIssueKind.ShutdownCancelled));
        result.OwnedResourcesCleaned.Should().BeTrue();
    }

    [Fact]
    public async Task DerivedDeadlineClassifiesPendingFirstFrameAndCleansResources()
    {
        ApplyDeadlineBudget budget = new(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5));
        TestRuntime runtime = new(TimeSpan.FromSeconds(5), budget, false);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        HostCommandResult accepted = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        ApplyGenerationResult result = await commandLoop.WaitForApplyGenerationAsync(
            accepted.State!.Generation,
            CancellationToken.None);

        result.State.Should().Be(ApplyGenerationTerminalState.Failed);
        result.FailureReason.Should().Be(ApplyFailureReason.DeadlineExceeded);
        result.TerminalPhase.Should().Be(ApplyGenerationPhase.FirstFramePresented);
        result.TotalDeadline.Should().Be(TimeSpan.FromMilliseconds(30));
        await WaitUntilAsync(
            () => runtime.Desktop.DestroyedSurfaces.Contains(new SurfaceId("surface-1")),
            CancellationToken.None);
        commandLoop.GetApplyTimelineForDiagnostics(result.Generation)
            .Should().Contain(entry =>
                entry.Phase == ApplyGenerationPhase.CleanupVerified &&
                entry.Outcome == ApplyStageOutcome.Success);
    }

    [Fact]
    public void ApplyDeadlineScalesWithSequentialSurfaceCount()
    {
        ApplyDeadlineBudget budget = new(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(6));

        budget.Calculate(2).Should().Be(TimeSpan.FromSeconds(27));
    }

    [Fact]
    public void ProductCandidateDisplaySelectionResolvesPrimaryAllAndExplicitTargets()
    {
        DisplayTopology topology = CreateDualDisplayTopology();

        RendererProductCandidateDiagnostics.ResolveDisplayIds(
                topology,
                ProductCandidateDisplaySelection.Primary)
            .Should().Equal(new DisplayId("primary"));
        RendererProductCandidateDiagnostics.ResolveDisplayIds(
                topology,
                ProductCandidateDisplaySelection.All)
            .Should().Equal(new DisplayId("primary"), new DisplayId("secondary"));
        RendererProductCandidateDiagnostics.ResolveDisplayIds(
                topology,
                ProductCandidateDisplaySelection.Explicit(new DisplayId("secondary")))
            .Should().Equal(new DisplayId("secondary"));
    }

    [Fact]
    public void ProductCandidateExplicitSelectionRejectsUnknownDisplay()
    {
        Action act = () => RendererProductCandidateDiagnostics.ResolveDisplayIds(
            CreateDualDisplayTopology(),
            ProductCandidateDisplaySelection.Explicit(new DisplayId("missing")));

        act.Should().Throw<ArgumentException>().WithMessage("*missing*");
    }

    [Fact]
    public async Task OneOfTwoInitialFirstFramesFailingRollsBackTheEntireProvisionalBatch()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, false);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateDualDisplayTopology()),
            CancellationToken.None);

        HostCommandResult accepted = await commandLoop.SendAsync(
            new ApplyWallpaperCommand(
                new WallpaperId("sample.wallpaper"),
                [new DisplayId("primary"), new DisplayId("secondary")],
                LayoutMode.PerDisplay,
                FitMode.Cover),
            CancellationToken.None);
        ApplyGenerationResult terminal = await commandLoop.WaitForApplyGenerationAsync(
            accepted.State!.Generation,
            CancellationToken.None);
        FakeRendererSession firstRenderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        FakeRendererSession secondRenderer = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        await WaitUntilAsync(() => firstRenderer.IsDisposed && secondRenderer.IsDisposed,
            CancellationToken.None);

        terminal.State.Should().Be(ApplyGenerationTerminalState.Failed);
        terminal.TerminalPhase.Should().Be(ApplyGenerationPhase.FirstFramePresented);
        (await GetStateAsync(commandLoop)).Sessions.Should().BeEmpty();
        runtime.Desktop.Replacements.Should().BeEmpty();
        runtime.Desktop.DestroyedSurfaces.Should().Contain(
            [new SurfaceId("surface-1"), new SurfaceId("surface-2")]);
    }

    [Fact]
    public async Task ProvisionalShutdownFailureDoesNotHideThePrimaryApplyFailure()
    {
        TestRuntime runtime = new(TimeSpan.FromSeconds(2), true, false);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateDualDisplayTopology()),
            CancellationToken.None);

        HostCommandResult accepted = await commandLoop.SendAsync(
            new ApplyWallpaperCommand(
                new WallpaperId("sample.wallpaper"),
                [new DisplayId("primary"), new DisplayId("secondary")],
                LayoutMode.PerDisplay,
                FitMode.Cover),
            CancellationToken.None);
        FakeRendererSession secondRenderer = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        secondRenderer.FailShutdownSend = true;
        secondRenderer.FailBeforeFirstFrame();

        ApplyGenerationResult terminal = await commandLoop.WaitForApplyGenerationAsync(
            accepted.State!.Generation,
            CancellationToken.None);
        await WaitUntilAsync(
            () => secondRenderer.IsDisposed &&
                runtime.Desktop.DestroyedSurfaces.Count == 2,
            CancellationToken.None);

        terminal.State.Should().Be(ApplyGenerationTerminalState.Failed);
        terminal.TerminalPhase.Should().Be(ApplyGenerationPhase.FirstFramePresented);
        terminal.FailureReason.Should().Be(ApplyFailureReason.FirstFrameRejected);
        commandLoop.GetApplyTimelineForDiagnostics(terminal.Generation).Should().Contain(entry =>
            entry.Phase == ApplyGenerationPhase.ShutdownCompleted &&
            entry.Outcome == ApplyStageOutcome.Rejected &&
            entry.FailureReason == ApplyFailureReason.ShutdownFailed);
        commandLoop.GetApplyTimelineForDiagnostics(terminal.Generation).Should().Contain(entry =>
            entry.SurfaceOrdinal == 2 &&
            entry.Phase == ApplyGenerationPhase.CleanupVerified &&
            entry.Outcome == ApplyStageOutcome.Success);
        (await GetStateAsync(commandLoop)).Sessions.Should().BeEmpty();
        runtime.Desktop.Replacements.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyPublishesSessionOnlyAfterFirstFrame()
    {
        TestRuntime runtime = new(false);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        HostCommandResult accepted = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        accepted.Accepted.Should().BeTrue();
        HostStateSnapshot beforeFirstFrame = await GetStateAsync(commandLoop);
        beforeFirstFrame.Sessions.Should().BeEmpty();
        renderer.Commands.Should().Contain(command => command is AttachRendererSurface);
        renderer.Commands.Should().Contain(command => command is LoadRendererContent);

        renderer.PresentFirstFrame();
        HostStateSnapshot afterFirstFrame = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions.Count == 1);

        afterFirstFrame.Assignments.Should().ContainSingle();
        afterFirstFrame.Sessions.Should().ContainSingle();
        afterFirstFrame.Sessions[0].Session.State.Should().Be(PlaybackState.Playing);
    }

    [Fact]
    public async Task ReplacementKeepsOldRendererUntilNewFirstFrame()
    {
        TestRuntime runtime = new(true, false);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        HostStateSnapshot firstState = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions.Count == 1);
        FakeRendererSession firstRenderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        FakeRendererSession secondRenderer = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);

        firstRenderer.IsDisposed.Should().BeFalse();
        (await GetStateAsync(commandLoop)).Sessions[0].Session.Id
            .Should().Be(firstState.Sessions[0].Session.Id);

        secondRenderer.PresentFirstFrame();
        HostStateSnapshot replaced = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions.Count == 1 &&
                state.Sessions[0].Session.Generation == 2);
        await WaitUntilAsync(
            () => firstRenderer.IsDisposed,
            CancellationToken.None);

        replaced.Sessions[0].Session.Id.Should().NotBe(firstState.Sessions[0].Session.Id);
        firstRenderer.Commands.Should().Contain(command => command is ShutdownRenderer);
        runtime.Desktop.DestroyedSurfaces.Should().Contain(new SurfaceId("surface-1"));
    }

    [Fact]
    public async Task ReplacementFailureKeepsOldSessionAndCleansProvisionalResources()
    {
        TestRuntime runtime = new(true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        HostStateSnapshot original = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions.Count == 1);
        FakeRendererSession originalRenderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        runtime.Desktop.FailNextReplacement = true;

        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        FakeRendererSession replacementRenderer = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        await WaitUntilAsync(
            () => replacementRenderer.IsDisposed &&
                runtime.Desktop.DestroyedSurfaces.Contains(new SurfaceId("surface-2")),
            CancellationToken.None);
        HostStateSnapshot afterFailure = await GetStateAsync(commandLoop);

        afterFailure.Sessions.Should().ContainSingle();
        afterFailure.Sessions[0].Session.Id.Should().Be(original.Sessions[0].Session.Id);
        originalRenderer.IsDisposed.Should().BeFalse();
        runtime.Desktop.DestroyedSurfaces.Should().NotContain(new SurfaceId("surface-1"));
    }

    [Fact]
    public async Task DualDisplayPartialReplacementFailureKeepsBothCommittedSessionsAndAllowsRetry()
    {
        TestRuntime runtime = new(true, true, true, false, true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateDualDisplayTopology()),
            CancellationToken.None);
        ApplyWallpaperCommand apply = new(
            new WallpaperId("sample.wallpaper"),
            [new DisplayId("primary"), new DisplayId("secondary")],
            LayoutMode.PerDisplay,
            FitMode.Cover);

        HostCommandResult acceptedA = await commandLoop.SendAsync(apply, CancellationToken.None);
        ApplyGenerationResult resultA = await commandLoop.WaitForApplyGenerationAsync(
            acceptedA.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot stateA = await GetStateAsync(commandLoop);
        SessionId[] sessionIdsA = stateA.Sessions.Select(item => item.Session.Id).ToArray();

        HostCommandResult acceptedB = await commandLoop.SendAsync(apply, CancellationToken.None);
        FakeRendererSession failedB = await runtime.Provider.WaitForSessionAsync(
            3,
            CancellationToken.None);
        failedB.FailBeforeFirstFrame();
        ApplyGenerationResult resultB = await commandLoop.WaitForApplyGenerationAsync(
            acceptedB.State!.Generation,
            CancellationToken.None);
        await WaitUntilAsync(
            () => runtime.Provider.SessionCount == 4 &&
                runtime.Desktop.DestroyedSurfaces.Contains(new SurfaceId("surface-4")),
            CancellationToken.None);
        HostStateSnapshot stateB = await GetStateAsync(commandLoop);

        resultA.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        resultB.State.Should().Be(ApplyGenerationTerminalState.Failed);
        resultB.TerminalPhase.Should().Be(ApplyGenerationPhase.FirstFramePresented);
        stateB.Generation.Should().Be(2);
        stateB.Sessions.Select(item => item.Session.Id).Should().BeEquivalentTo(sessionIdsA);
        stateB.Sessions.Should().OnlyContain(item => item.Session.Generation == 1);
        runtime.Desktop.Replacements.Should().ContainSingle();
        runtime.Desktop.DestroyedSurfaces.Should().Contain(
            [new SurfaceId("surface-3"), new SurfaceId("surface-4")]);
        runtime.Desktop.DestroyedSurfaces.Should().NotContain(
            [new SurfaceId("surface-1"), new SurfaceId("surface-2")]);

        HostCommandResult acceptedC = await commandLoop.SendAsync(apply, CancellationToken.None);
        ApplyGenerationResult resultC = await commandLoop.WaitForApplyGenerationAsync(
            acceptedC.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot stateC = await GetStateAsync(commandLoop);

        resultC.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        stateC.Generation.Should().Be(3);
        stateC.Sessions.Should().HaveCount(2);
        stateC.Sessions.Select(item => item.Session.Id).Should().NotIntersectWith(sessionIdsA);
        runtime.Desktop.Replacements.Should().HaveCount(2);
        await WaitUntilAsync(
            () => runtime.Desktop.DestroyedSurfaces.Contains(new SurfaceId("surface-1")) &&
                runtime.Desktop.DestroyedSurfaces.Contains(new SurfaceId("surface-2")),
            CancellationToken.None);
    }

    [Fact]
    public async Task OldDualDisplayRetirementTimeoutDoesNotRollbackNewCommittedSessions()
    {
        TestRuntime runtime = new(
            TimeSpan.FromSeconds(2),
            ApplyDeadlineBudget.Default,
            new SessionRetirementPolicy(
                TimeSpan.FromMilliseconds(30),
                TimeSpan.FromMilliseconds(30)),
            true,
            true,
            true,
            true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateDualDisplayTopology()),
            CancellationToken.None);
        ApplyWallpaperCommand apply = new(
            new WallpaperId("sample.wallpaper"),
            [new DisplayId("primary"), new DisplayId("secondary")],
            LayoutMode.PerDisplay,
            FitMode.Cover);

        HostCommandResult acceptedA = await commandLoop.SendAsync(apply, CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            acceptedA.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot stateA = await GetStateAsync(commandLoop);
        FakeRendererSession secondA = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        secondA.SuppressShutdownCompleted = true;

        HostCommandResult acceptedB = await commandLoop.SendAsync(apply, CancellationToken.None);
        ApplyGenerationResult resultB = await commandLoop.WaitForApplyGenerationAsync(
            acceptedB.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot stateB = await GetStateAsync(commandLoop);
        SessionRetirementBatchResult retiredA =
            await commandLoop.WaitForSessionRetirementsForDiagnosticsAsync(
                stateA.Sessions.Select(item => item.Session.Id).ToArray(),
                CancellationToken.None);
        HostStateSnapshot afterRetirementTimeout = await GetStateAsync(commandLoop);

        resultB.State.Should().Be(ApplyGenerationTerminalState.Succeeded);
        stateB.Sessions.Should().HaveCount(2);
        stateB.Sessions.Should().OnlyContain(item => item.Session.Generation == 2);
        afterRetirementTimeout.Sessions.Select(item => item.Session.Id)
            .Should().BeEquivalentTo(stateB.Sessions.Select(item => item.Session.Id));
        retiredA.Sessions.Should().HaveCount(2);
        retiredA.Sessions.Single(item => item.SurfaceOrdinal == 1).Graceful.Should().BeTrue();
        SessionRetirementResult timedOut = retiredA.Sessions.Single(item =>
            item.SurfaceOrdinal == 2);
        timedOut.Graceful.Should().BeFalse();
        timedOut.Issues.Should().Contain(item =>
            item.Kind == SessionRetirementIssueKind.ShutdownTimeout);
        timedOut.OwnedResourcesCleaned.Should().BeTrue();
        runtime.Desktop.DestroyedSurfaces.Should().Contain(
            [new SurfaceId("surface-1"), new SurfaceId("surface-2")]);
        runtime.Desktop.DestroyedSurfaces.Should().NotContain(
            [new SurfaceId("surface-3"), new SurfaceId("surface-4")]);
    }

    [Fact]
    public async Task ExplorerRecoveryRebuildsSurfaceAndPreservesRendererSession()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        HostStateSnapshot original = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions.Count == 1);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        HostCommandResult recoveryAccepted = await commandLoop.SendAsync(
            new ExplorerRestartedCommand(),
            CancellationToken.None);
        DesktopRecoveryResult recoveryResult = await commandLoop.WaitForDesktopRecoveryAsync(
            recoveryAccepted.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot recovered = await GetStateAsync(commandLoop);

        recoveryResult.TerminalState.Should().Be(DesktopRecoveryTerminalState.Succeeded);
        recoveryResult.CompletionMode.Should().Be(DesktopRecoveryCompletionMode.Reattached);
        recoveryResult.Timeline.Should().Contain(entry =>
            entry.Phase == DesktopRecoveryPhase.SurfaceAttached);
        recoveryResult.Timeline.Should().Contain(entry =>
            entry.Phase == DesktopRecoveryPhase.FirstFramePresented);
        recoveryResult.Timeline.Should().Contain(entry =>
            entry.Phase == DesktopRecoveryPhase.SurfacesReplaced);
        recoveryResult.Timeline.Should().Contain(entry =>
            entry.Phase == DesktopRecoveryPhase.CleanupVerified);
        recovered.Generation.Should().Be(2);
        recovered.Sessions.Should().ContainSingle();
        recovered.Sessions[0].Session.Id.Should().Be(original.Sessions[0].Session.Id);
        renderer.IsDisposed.Should().BeFalse();
        renderer.Commands.Should().Contain(command => command is SetRendererBounds);
        runtime.Desktop.Replacements.Last().ProvisionalSurfaceIds
            .Should().Equal(new SurfaceId("surface-2"));
        runtime.Desktop.Replacements.Last().ReplacedSurfaceIds
            .Should().Equal(new SurfaceId("surface-1"));
    }

    [Fact]
    public async Task RecoveryAttachWriteFailureAbandonsOldGenerationAndBuildsFreshSession()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        HostStateSnapshot original = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions.Count == 1);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        renderer.FailAttachOnCount = 2;

        HostCommandResult recoveryAccepted = await commandLoop.SendAsync(
            new ExplorerRestartedCommand(),
            CancellationToken.None);
        DesktopRecoveryResult recoveryResult = await commandLoop.WaitForDesktopRecoveryAsync(
            recoveryAccepted.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot rebuilt = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 1);

        recoveryResult.TerminalState.Should().Be(DesktopRecoveryTerminalState.Succeeded);
        recoveryResult.CompletionMode.Should().Be(DesktopRecoveryCompletionMode.Rebuilt);
        recoveryResult.Timeline.Should().Contain(entry =>
            entry.Phase == DesktopRecoveryPhase.RebuildScheduled);
        recoveryResult.Timeline.Should().Contain(entry =>
            entry.Phase == DesktopRecoveryPhase.RebuildStarted);
        renderer.IsDisposed.Should().BeTrue();
        renderer.Commands.OfType<AttachRendererSurface>().Should().HaveCount(2);
        runtime.Desktop.AbandonedSurfaces.Should().Contain(new SurfaceId("surface-1"));
        runtime.Desktop.AbandonedSurfaces.Should().Contain(new SurfaceId("surface-2"));
        rebuilt.Assignments.Should().ContainSingle();
        rebuilt.Sessions.Single().Session.Id.Should().NotBe(original.Sessions.Single().Session.Id);
    }

    [Fact]
    public async Task RecoverySurfaceAttachedTimeoutDoesNotLeaveOldSessionActive()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 1);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        renderer.SuppressSurfaceAttachedOnCount = 2;

        await commandLoop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await WaitUntilAsync(() => renderer.IsDisposed, CancellationToken.None);
        HostStateSnapshot invalidated = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions.Count == 0);

        invalidated.Sessions.Should().BeEmpty();
        invalidated.Assignments.Should().ContainSingle();
        renderer.Commands.OfType<AttachRendererSurface>().Should().HaveCount(2);
        await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 1);
    }

    [Fact]
    public async Task RendererFailureAfterSurfaceAttachedTriggersFreshSessionRebuild()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 1);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        renderer.FailAfterSurfaceAttachedOnCount = 2;

        HostCommandResult accepted = await commandLoop.SendAsync(
            new ExplorerRestartedCommand(),
            CancellationToken.None);
        DesktopRecoveryResult recoveryResult = await commandLoop.WaitForDesktopRecoveryAsync(
            accepted.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot rebuilt = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 1);

        recoveryResult.TerminalState.Should().Be(DesktopRecoveryTerminalState.Succeeded);
        recoveryResult.CompletionMode.Should().Be(DesktopRecoveryCompletionMode.Rebuilt);
        recoveryResult.Timeline.Should().ContainSingle(entry =>
            entry.Phase == DesktopRecoveryPhase.FirstFramePresented &&
            entry.Outcome == ApplyStageOutcome.Rejected &&
            entry.FailureReason == ApplyFailureReason.FirstFrameRejected);
        recoveryResult.Timeline.Should().ContainSingle(entry =>
            entry.Phase == DesktopRecoveryPhase.BatchAbandoned);
        renderer.IsDisposed.Should().BeTrue();
        rebuilt.Assignments.Should().ContainSingle();
        renderer.Commands.OfType<AttachRendererSurface>().Should().HaveCount(2);
    }

    [Fact]
    public async Task RecoveryFirstFrameTimeoutTriggersFreshSessionRebuild()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 1);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        renderer.SuppressFirstFrameOnAttachCount = 2;

        HostCommandResult accepted = await commandLoop.SendAsync(
            new ExplorerRestartedCommand(),
            CancellationToken.None);
        DesktopRecoveryResult recoveryResult = await commandLoop.WaitForDesktopRecoveryAsync(
            accepted.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot rebuilt = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 1);

        recoveryResult.TerminalState.Should().Be(DesktopRecoveryTerminalState.Succeeded);
        recoveryResult.CompletionMode.Should().Be(DesktopRecoveryCompletionMode.Rebuilt);
        recoveryResult.Timeline.Should().ContainSingle(entry =>
            entry.Phase == DesktopRecoveryPhase.FirstFramePresented &&
            entry.Outcome == ApplyStageOutcome.Rejected &&
            entry.FailureReason == ApplyFailureReason.FirstFrameTimeout);
        recoveryResult.Timeline.Should().ContainSingle(entry =>
            entry.Phase == DesktopRecoveryPhase.BatchAbandoned);
        renderer.IsDisposed.Should().BeTrue();
        renderer.Commands.OfType<AttachRendererSurface>().Should().HaveCount(2);
        rebuilt.Assignments.Should().ContainSingle();
    }

    [Fact]
    public async Task RecoveryReplaceFailureInvalidatesSessionBeforeFreshRebuild()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 1);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        runtime.Desktop.FailNextReplacement = true;

        await commandLoop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await WaitUntilAsync(() => renderer.IsDisposed, CancellationToken.None);
        (await GetStateAsync(commandLoop)).Sessions.Should().BeEmpty();
        HostStateSnapshot rebuilt = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 1);

        rebuilt.Assignments.Should().ContainSingle();
        runtime.Desktop.AbandonedSurfaces.Should().Contain(new SurfaceId("surface-1"));
        runtime.Desktop.AbandonedSurfaces.Should().Contain(new SurfaceId("surface-2"));
    }

    [Fact]
    public async Task RepeatedExplorerSignalIsCoalescedDuringRecovery()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 1);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        renderer.SuppressSurfaceAttachedOnCount = 2;

        HostCommandResult firstSignal = await commandLoop.SendAsync(
            new ExplorerRestartedCommand(),
            CancellationToken.None);
        HostCommandResult duplicateSignal = await commandLoop.SendAsync(
            new ExplorerRestartedCommand(),
            CancellationToken.None);
        DesktopRecoveryResult recoveryResult = await commandLoop.WaitForDesktopRecoveryAsync(
            firstSignal.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot rebuilt = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 1);

        duplicateSignal.State!.Generation.Should().Be(firstSignal.State.Generation);
        recoveryResult.TerminalState.Should().Be(DesktopRecoveryTerminalState.Succeeded);
        recoveryResult.CompletionMode.Should().Be(DesktopRecoveryCompletionMode.Rebuilt);
        rebuilt.Generation.Should().Be(3);
        renderer.Commands.OfType<AttachRendererSurface>().Should().HaveCount(2);
    }

    [Fact]
    public async Task CompletedDesktopRecoveryJournalIsBounded()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 1);

        for (int index = 0; index < 34; index++)
        {
            HostCommandResult accepted = await commandLoop.SendAsync(
                new ExplorerRestartedCommand(),
                CancellationToken.None);
            DesktopRecoveryResult result = await commandLoop.WaitForDesktopRecoveryAsync(
                accepted.State!.Generation,
                CancellationToken.None);
            result.TerminalState.Should().Be(DesktopRecoveryTerminalState.Succeeded);
        }

        commandLoop.RecoveryDiagnosticsCountForTests.Should().Be(32);
    }

    [Fact]
    public async Task OneOfTwoDisplayRecoveryFailuresAbandonsEveryDisplayAndRebuildsAllSessions()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, true, true, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateDualDisplayTopology()),
            CancellationToken.None);
        HostCommandResult initialApply = await commandLoop.SendAsync(
            new ApplyWallpaperCommand(
                new WallpaperId("sample.wallpaper"),
                [new DisplayId("primary"), new DisplayId("secondary")],
                LayoutMode.PerDisplay,
                FitMode.Cover),
            CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 2);
        ApplyGenerationResult initialTerminal = await commandLoop.WaitForApplyGenerationAsync(
            initialApply.State!.Generation,
            CancellationToken.None);
        initialTerminal.TotalDeadline.Should().Be(TimeSpan.FromSeconds(82));
        initialTerminal.Timeline.Count(entry =>
            entry.Phase == ApplyGenerationPhase.FirstFramePresented).Should().Be(2);
        initialTerminal.Timeline.Count(entry =>
            entry.Phase == ApplyGenerationPhase.SurfacesReplaced).Should().Be(1);
        initialTerminal.Timeline
            .Where(entry => entry.Phase == ApplyGenerationPhase.FirstFramePresented)
            .Select(entry => entry.SurfaceOrdinal)
            .Should().Equal(1, 2);
        FakeRendererSession first = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        FakeRendererSession second = await runtime.Provider.WaitForSessionAsync(
            1,
            CancellationToken.None);
        second.FailAttachOnCount = 2;

        HostCommandResult recoveryAccepted = await commandLoop.SendAsync(
            new ExplorerRestartedCommand(),
            CancellationToken.None);
        DesktopRecoveryResult recoveryResult = await commandLoop.WaitForDesktopRecoveryAsync(
            recoveryAccepted.State!.Generation,
            CancellationToken.None);
        HostStateSnapshot rebuilt = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 2);

        recoveryResult.TerminalState.Should().Be(DesktopRecoveryTerminalState.Succeeded);
        recoveryResult.CompletionMode.Should().Be(DesktopRecoveryCompletionMode.Rebuilt);
        recoveryResult.Timeline.Count(entry =>
            entry.Phase == DesktopRecoveryPhase.SurfaceCreated).Should().Be(2);
        recoveryResult.Timeline.Should().Contain(entry =>
            entry.Phase == DesktopRecoveryPhase.BatchAbandoned);
        recoveryResult.Timeline.Should().ContainSingle(entry =>
            entry.Phase == DesktopRecoveryPhase.AttachSent &&
            entry.Outcome == ApplyStageOutcome.Rejected &&
            entry.FailureReason == ApplyFailureReason.AttachWriteFailed &&
            entry.SurfaceOrdinal == 2 &&
            entry.DisplayIds.SequenceEqual(new[] { new DisplayId("secondary") }));
        recoveryResult.Timeline.Should().ContainSingle(entry =>
            entry.Phase == DesktopRecoveryPhase.BatchAbandoned);
        first.IsDisposed.Should().BeTrue();
        second.IsDisposed.Should().BeTrue();
        first.Commands.OfType<AttachRendererSurface>().Should().HaveCount(2);
        second.Commands.OfType<AttachRendererSurface>().Should().HaveCount(2);
        rebuilt.Assignments.Should().HaveCount(2);
        runtime.Desktop.AbandonedSurfaces.Should().Contain(
            [
                new SurfaceId("surface-1"),
                new SurfaceId("surface-2"),
                new SurfaceId("surface-3"),
                new SurfaceId("surface-4"),
            ]);
    }

    [Fact]
    public async Task RecoveryRebuildStopsAfterRetryBudgetAndOpensCircuit()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), true, false, false, false, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 1);
        FakeRendererSession original = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        original.SuppressSurfaceAttachedOnCount = 2;

        HostCommandResult recoveryAccepted = await commandLoop.SendAsync(
            new ExplorerRestartedCommand(),
            CancellationToken.None);
        FakeRendererSession finalAttempt = await runtime.Provider.WaitForSessionAsync(
            3,
            CancellationToken.None);
        await WaitUntilAsync(() => finalAttempt.IsDisposed, CancellationToken.None);
        HostStateSnapshot failedClosed = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 5 && state.Sessions.Count == 0);
        ApplyGenerationResult circuitResult = await commandLoop.WaitForApplyGenerationAsync(
            failedClosed.Generation,
            CancellationToken.None);
        DesktopRecoveryResult recoveryResult = await commandLoop.WaitForDesktopRecoveryAsync(
            recoveryAccepted.State!.Generation,
            CancellationToken.None);

        recoveryResult.TerminalState.Should().Be(DesktopRecoveryTerminalState.CircuitBroken);
        recoveryResult.CompletionMode.Should().BeNull();
        failedClosed.Assignments.Should().ContainSingle();
        circuitResult.State.Should().Be(ApplyGenerationTerminalState.CircuitBroken);
        circuitResult.TerminalPhase.Should().Be(ApplyGenerationPhase.FirstFramePresented);
        circuitResult.FailureReason.Should().Be(ApplyFailureReason.FirstFrameTimeout);
        runtime.Provider.SessionCount.Should().Be(4);
        HostCommandResult repeatedSignal = await commandLoop.SendAsync(
            new ExplorerRestartedCommand(),
            CancellationToken.None);
        repeatedSignal.Accepted.Should().BeFalse();
        repeatedSignal.ErrorCode.Should().Be("host.desktop.recovery_circuit_open");
        await Task.Delay(100);
        runtime.Provider.SessionCount.Should().Be(4);

        HostCommandResult explicitApply = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        explicitApply.Accepted.Should().BeTrue();
        HostStateSnapshot manuallyRecovered = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 6 && state.Sessions.Count == 1);
        manuallyRecovered.Assignments.Should().ContainSingle();
        runtime.Provider.SessionCount.Should().Be(5);
    }

    [Fact]
    public async Task FailedPreparationReleasesProvisionalRendererAndSurface()
    {
        TestRuntime runtime = new(false);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        renderer.FailBeforeFirstFrame();
        await WaitUntilAsync(
            () => renderer.IsDisposed &&
                runtime.Desktop.DestroyedSurfaces.Contains(new SurfaceId("surface-1")),
            CancellationToken.None);
        HostStateSnapshot state = await GetStateAsync(commandLoop);

        state.Sessions.Should().BeEmpty();
        state.Assignments.Should().BeEmpty();
        renderer.Commands.OfType<ShutdownRenderer>()
            .Should().ContainSingle()
            .Which.Reason.Should().Be("preparation-failed");
    }

    [Fact]
    public async Task FirstFrameStageTimeoutReleasesProvisionalResources()
    {
        TestRuntime runtime = new(TimeSpan.FromMilliseconds(50), false);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        HostCommandResult accepted = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        ApplyGenerationResult terminal = await commandLoop.WaitForApplyGenerationAsync(
            accepted.State!.Generation,
            CancellationToken.None);
        await WaitUntilAsync(
            () => renderer.IsDisposed &&
                runtime.Desktop.DestroyedSurfaces.Contains(new SurfaceId("surface-1")),
            CancellationToken.None);

        (await GetStateAsync(commandLoop)).Sessions.Should().BeEmpty();
        terminal.State.Should().Be(ApplyGenerationTerminalState.Failed);
        terminal.TerminalPhase.Should().Be(ApplyGenerationPhase.FirstFramePresented);
        terminal.FailureReason.Should().Be(ApplyFailureReason.FirstFrameTimeout);
    }

    [Fact]
    public async Task NewApplyImmediatelyCompletesPreviousGenerationAsSuperseded()
    {
        TestRuntime runtime = new(false, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult first = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);

        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        ApplyGenerationResult superseded = await commandLoop.WaitForApplyGenerationAsync(
            first.State!.Generation,
            CancellationToken.None);

        superseded.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        superseded.FailureReason.Should().Be(ApplyFailureReason.Superseded);
        FakeRendererSession abandoned = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        await WaitUntilAsync(
            () => abandoned.IsDisposed &&
                runtime.Desktop.DestroyedSurfaces.Contains(new SurfaceId("surface-1")),
            CancellationToken.None);
    }

    [Fact]
    public async Task HostShutdownCompletesPendingApplyAsCancelled()
    {
        TestRuntime runtime = new(TimeSpan.FromSeconds(5), false);
        HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult accepted = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        Task<ApplyGenerationResult> terminal = commandLoop.WaitForApplyGenerationAsync(
            accepted.State!.Generation,
            CancellationToken.None);
        await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);

        await commandLoop.DisposeAsync();
        ApplyGenerationResult result = await terminal.WaitAsync(TimeSpan.FromSeconds(2));

        result.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        result.FailureReason.Should().Be(ApplyFailureReason.Cancelled);
    }

    [Fact]
    public async Task ShutdownIsIdempotentAndRejectsLaterExternalMutations()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        HostCommandResult accepted = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await commandLoop.WaitForApplyGenerationAsync(
            accepted.State!.Generation,
            CancellationToken.None);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        HostCommandResult first = await commandLoop.SendAsync(
            new ShutdownCommand(),
            CancellationToken.None);
        HostCommandResult second = await commandLoop.SendAsync(
            new ShutdownCommand(),
            CancellationToken.None);
        HostCommandResult apply = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        HostCommandResult topology = await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateTopology() with { Revision = 2 }),
            CancellationToken.None);
        HostCommandResult explorer = await commandLoop.SendAsync(
            new ExplorerRestartedCommand(),
            CancellationToken.None);
        await commandLoop.WaitForPendingRetirementsForDiagnosticsAsync(CancellationToken.None);

        first.Accepted.Should().BeTrue();
        second.Accepted.Should().BeTrue();
        second.StateRevision.Should().Be(first.StateRevision);
        apply.ErrorCode.Should().Be("host.stopping");
        topology.ErrorCode.Should().Be("host.stopping");
        explorer.ErrorCode.Should().Be("host.stopping");
        renderer.Commands.OfType<ShutdownRenderer>().Should().ContainSingle();
        renderer.ShutdownCompletedCount.Should().Be(1);
        first.State!.ShutdownRequested.Should().BeTrue();
        first.State.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task FullyPreparedApplyCannotCommitAfterShutdownStarts()
    {
        TestRuntime runtime = new(true);
        GatedSessionCoordinator? gated = null;
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop(inner =>
        {
            gated = new GatedSessionCoordinator(inner, blockedPrepareCall: 1);
            return gated;
        });
        await SetTopologyAsync(commandLoop);
        HostCommandResult accepted = await commandLoop.SendAsync(
            CreateApplyCommand(),
            CancellationToken.None);
        await gated!.Prepared;

        await commandLoop.SendAsync(new ShutdownCommand(), CancellationToken.None);
        gated.Release();
        ApplyGenerationResult terminal = await commandLoop.WaitForApplyGenerationAsync(
            accepted.State!.Generation,
            CancellationToken.None);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        await WaitUntilAsync(() => renderer.IsDisposed, CancellationToken.None);

        terminal.State.Should().Be(ApplyGenerationTerminalState.Cancelled);
        terminal.FailureReason.Should().Be(ApplyFailureReason.Cancelled);
        runtime.Desktop.Replacements.Should().BeEmpty();
        (await GetStateAsync(commandLoop)).Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task LateOlderGenerationCannotReplaceCurrentSession()
    {
        TestRuntime runtime = new(false, true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);

        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        FakeRendererSession older = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);

        HostStateSnapshot current = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions.Count == 1 && state.Sessions[0].Session.Generation == 2);
        SessionId currentSessionId = current.Sessions[0].Session.Id;

        older.PresentFirstFrame();
        await WaitUntilAsync(() => older.IsDisposed, CancellationToken.None);
        HostStateSnapshot afterLateCompletion = await GetStateAsync(commandLoop);

        afterLateCompletion.Sessions.Should().ContainSingle();
        afterLateCompletion.Sessions[0].Session.Id.Should().Be(currentSessionId);
        afterLateCompletion.Sessions[0].Session.Generation.Should().Be(2);
    }

    [Fact]
    public async Task UserPauseUpdatesStateAndSendsTypedRendererCommand()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        HostStateSnapshot active = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions.Count == 1);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        await commandLoop.SendAsync(
            new ChangePlaybackIntentCommand(UserPlaybackIntent.Pause),
            CancellationToken.None);
        HostStateSnapshot paused = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions[0].Session.State == PlaybackState.Paused);
        await WaitUntilAsync(
            () => renderer.Commands.Any(command =>
                command is ChangeRendererState { TargetState: PlaybackState.Paused }),
            CancellationToken.None);

        paused.Sessions[0].Session.Id.Should().Be(active.Sessions[0].Session.Id);
        paused.Sessions[0].UserIntent.Should().Be(UserPlaybackIntent.Pause);
    }

    [Fact]
    public async Task SystemStateRevisionSuspendsAndRejectsStaleResume()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 1);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);
        SystemStateSnapshot locked = new(false, true, false, false, false, false);
        SystemStateSnapshot unlocked = locked with { IsSessionLocked = false };

        await commandLoop.SendAsync(
            new SystemStateChangedCommand(new SystemStateChange(
                2,
                locked,
                SystemStateChangeReason.SessionLocked)),
            CancellationToken.None);
        await WaitForStateAsync(
            commandLoop,
            state => state.Sessions[0].Session.State == PlaybackState.Suspended);

        await commandLoop.SendAsync(
            new SystemStateChangedCommand(new SystemStateChange(
                1,
                unlocked,
                SystemStateChangeReason.SessionUnlocked)),
            CancellationToken.None);
        HostStateSnapshot stale = await GetStateAsync(commandLoop);
        stale.SystemStateRevision.Should().Be(2);
        stale.SystemState.IsSessionLocked.Should().BeTrue();
        stale.Sessions[0].Session.State.Should().Be(PlaybackState.Suspended);

        await commandLoop.SendAsync(
            new SystemStateChangedCommand(new SystemStateChange(
                3,
                unlocked,
                SystemStateChangeReason.SessionUnlocked)),
            CancellationToken.None);
        await WaitForStateAsync(
            commandLoop,
            state => state.Sessions[0].Session.State == PlaybackState.Playing);
        renderer.Commands.Any(command =>
            command is ChangeRendererState { TargetState: PlaybackState.Suspended })
            .Should().BeTrue();
        renderer.Commands.Any(command =>
            command is ChangeRendererState { TargetState: PlaybackState.Playing })
            .Should().BeTrue();
    }

    [Fact]
    public async Task ValidatedUnlockPreservesUserPauseAndRecordsPolicyReason()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 1);
        await commandLoop.SendAsync(new ChangePlaybackIntentCommand(UserPlaybackIntent.Pause), CancellationToken.None);
        await commandLoop.SendAsync(new SystemStateChangedCommand(new SystemStateChange(2,
            new SystemStateSnapshot(false, true, false, false, false, false),
            SystemStateChangeReason.SessionLocked)), CancellationToken.None);
        await commandLoop.SendAsync(new SystemStateChangedCommand(new SystemStateChange(3,
            new SystemStateSnapshot(false, false, false, false, false, false),
            SystemStateChangeReason.SessionUnlocked)), CancellationToken.None);

        HostStateSnapshot final = await GetStateAsync(commandLoop);
        final.Sessions.Should().ContainSingle();
        final.Sessions[0].UserIntent.Should().Be(UserPlaybackIntent.Pause);
        final.Sessions[0].Session.State.Should().Be(PlaybackState.Paused);
        commandLoop.GetPlaybackPolicyEvidenceForDiagnostics()[^1].PolicyReason
            .Should().Be(PlaybackDecisionReason.UserRequest);
        commandLoop.GetPlaybackPolicyEvidenceForDiagnostics()[^1].State
            .Should().Be(PlaybackState.Paused);
    }

    [Fact]
    public async Task ApplyCommittedWhileLockedStartsInSuspendedPolicyState()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(
            new SystemStateChangedCommand(new SystemStateChange(
                1,
                new SystemStateSnapshot(false, true, false, false, false, false),
                SystemStateChangeReason.SessionLocked)),
            CancellationToken.None);

        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        HostStateSnapshot committed = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions.Count == 1 &&
                state.Sessions[0].Session.State == PlaybackState.Suspended);
        FakeRendererSession renderer = await runtime.Provider.WaitForSessionAsync(
            0,
            CancellationToken.None);

        committed.SystemState.IsSessionLocked.Should().BeTrue();
        renderer.Commands.Any(command =>
            command is ChangeRendererState { TargetState: PlaybackState.Suspended })
            .Should().BeTrue();
    }

    [Fact]
    public async Task RendererExitFaultsOnlyMatchingGeneration()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        HostStateSnapshot active = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions.Count == 1);
        WallpaperSession session = active.Sessions[0].Session;

        await commandLoop.SendAsync(
            new RendererExitedCommand(session.Id, session.Generation - 1, -1),
            CancellationToken.None);
        (await GetStateAsync(commandLoop)).Sessions[0].Session.State
            .Should().Be(PlaybackState.Playing);

        await commandLoop.SendAsync(
            new RendererExitedCommand(session.Id, session.Generation, -1),
            CancellationToken.None);
        (await GetStateAsync(commandLoop)).Sessions[0].Session.State
            .Should().Be(PlaybackState.Faulted);
    }

    [Fact]
    public async Task BootstrapLoadsPolicyAndRestoresConnectedAssignmentsAsOneGeneration()
    {
        TestRuntime runtime = new(true);
        await using HostCommandLoop commandLoop = runtime.CreateCommandLoop();
        UserPlaybackPolicy policy = new(true, false, true, true, false, 60, 20);
        WallpaperAssignment connected = new(
            new DisplayId("primary"),
            new WallpaperId("sample.wallpaper"),
            LayoutMode.PerDisplay,
            FitMode.Contain,
            null);
        WallpaperAssignment disconnected = connected with { DisplayId = new DisplayId("offline") };
        FakeHostConfigurationStore store = new(
            new HostConfiguration(policy, [connected, disconnected]));
        HostBootstrapper bootstrapper = new(store);

        HostBootstrapResult result = await bootstrapper.InitializeAsync(
            commandLoop,
            CreateTopology(),
            CancellationToken.None);
        HostStateSnapshot state = await WaitForStateAsync(commandLoop, value => value.Sessions.Count == 1);

        result.ConnectedAssignments.Should().ContainSingle().Which.Should().Be(connected);
        result.RestoreResult!.Accepted.Should().BeTrue();
        result.RestoreGenerationResult!.State.Should()
            .Be(ApplyGenerationTerminalState.Succeeded);
        state.Generation.Should().Be(1);
        state.PlaybackPolicy.Should().Be(policy);
        state.Assignments.Should().BeEquivalentTo([connected, disconnected]);

        await bootstrapper.PersistAsync(state, CancellationToken.None);
        store.SavedPolicy.Should().Be(policy);
        store.SavedAssignments.Should().BeEquivalentTo([connected, disconnected]);
    }

    [Fact]
    public async Task WindowsHostRuntimeStartsValidatesStopsAndPersistsExactlyOnce()
    {
        TestRuntime testRuntime = new(true);
        HostCommandLoop commandLoop = testRuntime.CreateCommandLoop();
        FakeHostConfigurationStore store = new(
            new HostConfiguration(UserPlaybackPolicy.Default, []));
        FakeDisplayTopologySource topologySource = new(CreateTopology());
        FakeWindowsEnvironmentEventBridge eventBridge = new();
        int platformDisposeCount = 0;
        WindowsHostRuntime runtime = new(
            commandLoop,
            new HostBootstrapper(store),
            topologySource,
            eventBridge,
            () =>
            {
                platformDisposeCount++;
                return ValueTask.CompletedTask;
            });

        await runtime.StartAsync(CancellationToken.None);
        Task firstStop = runtime.StopAsync(CancellationToken.None);
        Task secondStop = runtime.StopAsync(CancellationToken.None);
        await Task.WhenAll(firstStop, secondStop);

        runtime.State.Should().Be(WindowsHostRuntimeState.Stopped);
        runtime.LastFault.Should().BeNull();
        eventBridge.StartCount.Should().Be(1);
        eventBridge.ValidationCount.Should().Be(1);
        eventBridge.DisposeCount.Should().Be(1);
        platformDisposeCount.Should().Be(1);
        store.SavedPolicy.Should().Be(UserPlaybackPolicy.Default);
        store.SavedAssignments.Should().BeEmpty();
    }

    [Fact]
    public async Task WindowsEventBridgeValidatesTaskbarSignalsBeforeStartingOneRecovery()
    {
        TestRuntime testRuntime = new(true);
        await using HostCommandLoop commandLoop = testRuntime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await using WindowsDesktopHost desktopHost = new(
            new WindowsShellSnapshot("test", "26200", true, 1));
        using WindowsDisplayTopologySource displaySource = new();
        FakeExplorerChangeMonitor monitor = new();
        WindowsSystemStateSource systemStateSource = new(
            new SystemStateSnapshot(false, false, false, false, false, false));
        await using WindowsDesktopEventBridge bridge = new(
            desktopHost,
            displaySource,
            commandLoop,
            monitor,
            systemStateSource, consumeNativeWindowSignals: false);
        bridge.Start();

        for (int index = 0; index < 10; index++)
        {
            bridge.ProcessWindowSignalForTests(
                new DesktopWindowSignal(DesktopWindowSignalKind.TaskbarCreated));
        }

        monitor.RequestCount.Should().Be(10);
        bridge.RawTaskbarSignalCount.Should().Be(10);
        bridge.ValidatedShellChangeCount.Should().Be(0);
        bridge.ForwardedRecoveryCount.Should().Be(0);
        commandLoop.RecoveryDiagnosticsCountForTests.Should().Be(0);

        monitor.RaiseValidationFailure(new IOException("Simulated Shell probe failure."));
        bridge.LastExplorerValidationFailure.Should().NotBeNull();
        commandLoop.RecoveryDiagnosticsCountForTests.Should().Be(0);

        monitor.RaiseRestarted();
        await WaitUntilAsync(
            () => commandLoop.RecoveryDiagnosticsCountForTests == 1,
            CancellationToken.None);

        bridge.ValidatedShellChangeCount.Should().Be(1);
        bridge.ForwardedRecoveryCount.Should().Be(1);
        commandLoop.RecoveryDiagnosticsCountForTests.Should().Be(1);

        await commandLoop.SendAsync(new ShutdownCommand(), CancellationToken.None);
        monitor.RaiseRestarted();

        bridge.LastExplorerForwardingFailure!.Kind.Should()
            .Be(ExplorerEventForwardingFailureKind.HostStopping);
        bridge.ForwardedRecoveryCount.Should().Be(1);
        commandLoop.RecoveryDiagnosticsCountForTests.Should().Be(1);

        bridge.ProcessWindowSignalForTests(
            new DesktopWindowSignal(DesktopWindowSignalKind.SessionLocked));
        bridge.LastSystemStateForwardingFailure!.Kind.Should()
            .Be(SystemStateForwardingFailureKind.HostStopping);

        await bridge.DisposeAsync();
        monitor.RaiseRestarted();
        bridge.ValidatedShellChangeCount.Should().Be(2);
        bridge.ForwardedRecoveryCount.Should().Be(1);
    }

    [Fact]
    public async Task WindowsEventBridgeKeepsRendererSuspendedUntilUnlockValidationCompletes()
    {
        TestRuntime testRuntime = new(true);
        await using HostCommandLoop commandLoop = testRuntime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await using WindowsDesktopHost desktopHost = new(
            new WindowsShellSnapshot("test", "26200", true, 1));
        using WindowsDisplayTopologySource displaySource = new();
        FakeExplorerChangeMonitor monitor = new();
        WindowsSystemStateSource systemStateSource = new(
            new SystemStateSnapshot(false, false, false, false, false, false));
        await using WindowsDesktopEventBridge bridge = new(
            desktopHost,
            displaySource,
            commandLoop,
            monitor,
            systemStateSource, consumeNativeWindowSignals: false);
        bridge.Start();
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 1);

        bridge.ProcessWindowSignalForTests(
            new DesktopWindowSignal(DesktopWindowSignalKind.SessionLocked));
        await WaitForStateAsync(
            commandLoop,
            state => state.Sessions[0].Session.State == PlaybackState.Suspended);

        bridge.ProcessWindowSignalForTests(
            new DesktopWindowSignal(DesktopWindowSignalKind.SessionUnlocked));
        await Task.Delay(50);
        HostStateSnapshot pending = await GetStateAsync(commandLoop);
        pending.SystemState.IsSessionLocked.Should().BeTrue();
        pending.Sessions[0].Session.State.Should().Be(PlaybackState.Suspended);
        monitor.RequestCount.Should().Be(1);

        monitor.RaiseValidationCompleted(shellAvailable: false);
        await Task.Delay(50);
        (await GetStateAsync(commandLoop)).Sessions[0].Session.State
            .Should().Be(PlaybackState.Suspended);

        monitor.RaiseValidationCompleted(shellAvailable: true);
        HostStateSnapshot resumed = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions[0].Session.State == PlaybackState.Playing);
        resumed.SystemState.IsSessionLocked.Should().BeFalse();
        bridge.ForwardedSystemStateCount.Should().Be(4);
    }

    [Theory]
    [InlineData(
        DesktopWindowSignalKind.DisplayPoweredOff,
        DesktopWindowSignalKind.DisplayPoweredOn,
        SystemStateChangeReason.DisplayOn)]
    [InlineData(
        DesktopWindowSignalKind.SystemSuspending,
        DesktopWindowSignalKind.SystemResuming,
        SystemStateChangeReason.SystemResumed)]
    public async Task WindowsEventBridgeValidatesEnvironmentBeforeAutomaticResume(
        DesktopWindowSignalKind suspendSignal,
        DesktopWindowSignalKind resumeSignal,
        SystemStateChangeReason completedReason)
    {
        TestRuntime testRuntime = new(true);
        await using HostCommandLoop commandLoop = testRuntime.CreateCommandLoop();
        await SetTopologyAsync(commandLoop);
        await using WindowsDesktopHost desktopHost = new(
            new WindowsShellSnapshot("test", "26200", true, 1));
        using WindowsDisplayTopologySource displaySource = new();
        FakeExplorerChangeMonitor monitor = new();
        WindowsSystemStateSource systemStateSource = new(
            new SystemStateSnapshot(false, false, false, false, false, false));
        await using WindowsDesktopEventBridge bridge = new(
            desktopHost,
            displaySource,
            commandLoop,
            monitor,
            systemStateSource, consumeNativeWindowSignals: false);
        bridge.Start();
        await commandLoop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        await WaitForStateAsync(commandLoop, state => state.Sessions.Count == 1);

        bridge.ProcessWindowSignalForTests(new DesktopWindowSignal(suspendSignal));
        await WaitForStateAsync(
            commandLoop,
            state => state.Sessions[0].Session.State == PlaybackState.Suspended);
        bridge.ProcessWindowSignalForTests(new DesktopWindowSignal(resumeSignal));
        await Task.Delay(50);
        (await GetStateAsync(commandLoop)).Sessions[0].Session.State
            .Should().Be(PlaybackState.Suspended);

        monitor.RaiseValidationCompleted(shellAvailable: true);
        HostStateSnapshot resumed = await WaitForStateAsync(
            commandLoop,
            state => state.Sessions[0].Session.State == PlaybackState.Playing);

        resumed.SystemState.IsSystemSuspended.Should().BeFalse();
        resumed.SystemState.IsDisplayOff.Should().BeFalse();
        resumed.SystemStateReason.Should().Be(completedReason);
        monitor.RequestCount.Should().Be(1);
        bridge.GetSystemStateHistoryForDiagnostics().Should().Contain(change =>
            change.Reason == completedReason);
    }

    private static ApplyWallpaperCommand CreateApplyCommand() =>
        new(
            new WallpaperId("sample.wallpaper"),
            [new DisplayId("primary")],
            LayoutMode.PerDisplay,
            FitMode.Cover);

    private static ApplyDeadlineBudget CreateShortApplyBudget() => new(
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50));

    private static PreparedSession CreatePreparedSession(
        FakeRendererSession renderer,
        string displayId,
        string surfaceId,
        int ordinal)
    {
        DisplayId display = new(displayId);
        return new PreparedSession(
            new WallpaperSession(
                renderer.Id,
                new WallpaperId("sample.wallpaper"),
                [display],
                new RendererId("fake.video"),
                PlaybackState.Playing,
                renderer.Generation),
            renderer,
            [new PreparedSurface(
                new DesktopSurface(new SurfaceId(surfaceId), [display], (ulong)ordinal),
                new SurfaceRequest(
                    [display],
                    new DisplayBounds(0, 0, 1920, 1080),
                    FitMode.Cover))],
            ordinal);
    }

    private static async Task SetTopologyAsync(HostCommandLoop commandLoop)
    {
        await commandLoop.SendAsync(
            new DisplayTopologyChangedCommand(CreateTopology()),
            CancellationToken.None);
    }

    private static DisplayTopology CreateTopology() =>
        new(
            1,
            [
                new DisplayDescriptor(
                    new DisplayId("primary"),
                    "display/primary",
                    new DisplayBounds(0, 0, 1920, 1080),
                    1,
                    60,
                    true),
            ]);

    private static DisplayTopology CreateDualDisplayTopology() =>
        new(
            2,
            [
                new DisplayDescriptor(
                    new DisplayId("primary"),
                    "display/primary",
                    new DisplayBounds(0, 0, 1920, 1080),
                    1,
                    60,
                    true),
                new DisplayDescriptor(
                    new DisplayId("secondary"),
                    "display/secondary",
                    new DisplayBounds(1920, 0, 1920, 1080),
                    1,
                    60,
                    false),
            ]);

    private static async Task<HostStateSnapshot> GetStateAsync(HostCommandLoop commandLoop)
    {
        HostCommandResult result = await commandLoop.SendAsync(
            new GetHostStateCommand(),
            CancellationToken.None);
        return result.State!;
    }

    private static async Task<HostStateSnapshot> WaitForStateAsync(
        HostCommandLoop commandLoop,
        Func<HostStateSnapshot, bool> predicate)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            HostStateSnapshot state = await GetStateAsync(commandLoop);
            if (predicate(state))
            {
                return state;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Host state did not reach the expected condition.");
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        CancellationToken cancellationToken,
        int maximumAttempts = 500)
    {
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException("The expected asynchronous condition was not reached.");
    }

    private sealed class TestRuntime
    {
        private readonly TimeSpan? rendererStageTimeout;
        private readonly ApplyDeadlineBudget? applyDeadlineBudget;
        private readonly SessionRetirementPolicy? retirementPolicy;

        public TestRuntime(params bool[] autoPresentFirstFrame)
        {
            Provider = new FakeRendererProvider(autoPresentFirstFrame);
        }

        public TestRuntime(TimeSpan rendererStageTimeout, params bool[] autoPresentFirstFrame)
            : this(autoPresentFirstFrame)
        {
            this.rendererStageTimeout = rendererStageTimeout;
        }

        public TestRuntime(
            TimeSpan rendererStageTimeout,
            ApplyDeadlineBudget applyDeadlineBudget,
            params bool[] autoPresentFirstFrame)
            : this(rendererStageTimeout, autoPresentFirstFrame)
        {
            this.applyDeadlineBudget = applyDeadlineBudget;
        }

        public TestRuntime(
            TimeSpan rendererStageTimeout,
            ApplyDeadlineBudget applyDeadlineBudget,
            SessionRetirementPolicy retirementPolicy,
            params bool[] autoPresentFirstFrame)
            : this(rendererStageTimeout, applyDeadlineBudget, autoPresentFirstFrame)
        {
            this.retirementPolicy = retirementPolicy;
        }

        public FakeRendererProvider Provider { get; }

        public FakeDesktopHost Desktop { get; } = new();

        public SessionCoordinator Coordinator { get; private set; } = null!;

        public HostCommandLoop CreateCommandLoop(
            Func<ISessionCoordinator, ISessionCoordinator>? decorateCoordinator = null)
        {
            Coordinator = new SessionCoordinator(
                new FakeWallpaperRepository(),
                new DefaultLayoutPlanner(),
                new RendererProviderSelector([Provider]),
                Desktop,
                new RuntimeEnvironment("win-x64", true, new HashSet<string>()),
                rendererStageTimeout,
                applyDeadlineBudget,
                retirementPolicy);
            ISessionCoordinator selected = decorateCoordinator?.Invoke(Coordinator) ?? Coordinator;
            return new HostCommandLoop(selected, new DefaultPlaybackPolicyEvaluator());
        }
    }

    private sealed class GatedSessionCoordinator : ISessionCoordinator
    {
        private readonly ISessionCoordinator inner;
        private readonly int blockedPrepareCall;
        private readonly TaskCompletionSource prepared =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int prepareCalls;
        private int recoveryCalls;
        private readonly int blockedRecoveryCall;

        public GatedSessionCoordinator(ISessionCoordinator inner, int blockedPrepareCall, int blockedRecoveryCall = 0)
        {
            this.inner = inner;
            this.blockedPrepareCall = blockedPrepareCall;
            this.blockedRecoveryCall = blockedRecoveryCall;
        }

        public Task Prepared => prepared.Task;

        public void Release() => release.TrySetResult();

        public async Task<PreparedApply> PrepareApplyAsync(
            IReadOnlyList<WallpaperAssignment> assignments,
            DisplayTopology topology,
            long generation,
            ApplyGenerationOperation operation,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref prepareCalls);
            PreparedApply result = await inner.PrepareApplyAsync(
                    assignments,
                    topology,
                    generation,
                    operation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (call == blockedPrepareCall)
            {
                prepared.TrySetResult();
                // Deliberately hold a fully prepared result after FirstFrame so the Host can
                // accept a newer topology before the old internal completion is posted.
                await release.Task.ConfigureAwait(false);
            }
            return result;
        }

        public int GetPlannedSurfaceCount(
            IReadOnlyList<WallpaperAssignment> assignments,
            DisplayTopology topology) => inner.GetPlannedSurfaceCount(assignments, topology);

        public TimeSpan GetApplyDeadline(int surfaceCount) =>
            inner.GetApplyDeadline(surfaceCount);

        public Task<IReadOnlyList<ActiveSurfaceResizeResult>> ResizeActiveSurfacesAsync(
            IReadOnlyList<ActiveSurfaceResizeRequest> requests,
            DisplayTopology topology,
            long generation,
            CancellationToken cancellationToken) => inner.ResizeActiveSurfacesAsync(
                requests,
                topology,
                generation,
                cancellationToken);

        public Task<SessionRetirementBatchResult> RetireAsync(
            IReadOnlyList<PreparedSession> sessions,
            CancellationToken cancellationToken) => inner.RetireAsync(sessions, cancellationToken);

        public Task ReplaceSurfacesAsync(
            PreparedApply preparedApply,
            IReadOnlyList<PreparedSession> replacedSessions,
            CancellationToken cancellationToken) =>
            inner.ReplaceSurfacesAsync(preparedApply, replacedSessions, cancellationToken);

        public async Task<PreparedRecovery> PrepareRecoveryAsync(
            IReadOnlyList<PreparedSession> activeSessions,
            DisplayTopology topology,
            long hostGeneration,
            Action<DesktopRecoveryMilestone>? observer,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref recoveryCalls);
            PreparedRecovery result = await inner.PrepareRecoveryAsync(
                activeSessions,
                topology,
                hostGeneration,
                observer,
                cancellationToken).ConfigureAwait(false);
            if (call == blockedRecoveryCall)
            {
                prepared.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
            return result;
        }

        public Task ReplaceRecoveredSurfacesAsync(
            PreparedRecovery recovery,
            CancellationToken cancellationToken) =>
            inner.ReplaceRecoveredSurfacesAsync(recovery, cancellationToken);

        public Task DestroySurfacesAsync(
            IEnumerable<PreparedSurface> surfaces,
            CancellationToken cancellationToken) =>
            inner.DestroySurfacesAsync(surfaces, cancellationToken);

        public Task AbandonSurfacesAsync(
            IEnumerable<PreparedSurface> surfaces,
            CancellationToken cancellationToken) =>
            inner.AbandonSurfacesAsync(surfaces, cancellationToken);

        public Task AbandonRecoveryAsync(
            PreparedRecovery recovery,
            CancellationToken cancellationToken) =>
            inner.AbandonRecoveryAsync(recovery, cancellationToken);
    }

    private sealed class FakeWallpaperRepository : IWallpaperRepository
    {
        private readonly WallpaperLibraryEntry entry = new(
            new WallpaperDefinition(
                new WallpaperId("sample.wallpaper"),
                "1.0.0",
                WallpaperKind.Video,
                "content/wallpaper.mp4",
                new WallpaperMetadata(
                    "Sample",
                    null,
                    null,
                    null,
                    new HashSet<string>()),
                new WallpaperCapabilities(false, false, false, false, 30),
                WallpaperOrigin.Native),
            "C:\\LiveWallTest\\sample.wallpaper",
            new CompatibilityReport(CompatibilityGrade.Native, []));

        public Task<WallpaperLibraryEntry?> FindAsync(
            WallpaperId id,
            CancellationToken cancellationToken) =>
            Task.FromResult<WallpaperLibraryEntry?>(id == entry.Definition.Id ? entry : null);

        public Task<IReadOnlyList<WallpaperLibraryEntry>> ListAsync(
            WallpaperQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WallpaperLibraryEntry>>([entry]);

        public Task SaveImportedAsync(
            ImportedWallpaper wallpaper,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(WallpaperId id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeHostConfigurationStore : IHostConfigurationStore
    {
        private readonly HostConfiguration configuration;

        public FakeHostConfigurationStore(HostConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public UserPlaybackPolicy? SavedPolicy { get; private set; }

        public IReadOnlyList<WallpaperAssignment>? SavedAssignments { get; private set; }

        public Task<HostConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostConfigurationLoadResult(
                configuration,
                ConfigurationFileStatus.Loaded,
                ConfigurationFileStatus.Loaded));

        public Task SavePlaybackPolicyAsync(
            UserPlaybackPolicy policy,
            CancellationToken cancellationToken)
        {
            SavedPolicy = policy;
            return Task.CompletedTask;
        }

        public Task SaveAssignmentsAsync(
            IReadOnlyList<WallpaperAssignment> assignments,
            CancellationToken cancellationToken)
        {
            SavedAssignments = assignments.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDisplayTopologySource(DisplayTopology current)
        : IDisplayTopologySource
    {
        public DisplayTopology Current { get; } = current;

        public event EventHandler<DisplayTopologyChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeWindowsEnvironmentEventBridge
        : IWindowsEnvironmentEventBridge
    {
        public int StartCount { get; private set; }

        public int ValidationCount { get; private set; }

        public int DisposeCount { get; private set; }

        public void Start() => StartCount++;

        public void RequestEnvironmentValidation() => ValidationCount++;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeExplorerChangeMonitor : IExplorerChangeMonitor
    {
        public int RequestCount { get; private set; }

        public event EventHandler<ExplorerRestartedEventArgs>? Restarted;

        public event EventHandler<ExplorerValidationFailedEventArgs>? ValidationFailed;

        public event EventHandler<ExplorerValidationCompletedEventArgs>? ValidationCompleted;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        private long requestedSystemRevision;

        public void RequestValidation(long systemRevision = 0)
        {
            RequestCount++;
            if (systemRevision > 0)
            {
                requestedSystemRevision = systemRevision;
            }
        }

        public void RaiseRestarted() => Restarted?.Invoke(
            this,
            new ExplorerRestartedEventArgs(
                ExplorerChangeReason.ProgmanRecreated,
                10,
                11));

        public void RaiseValidationFailure(Exception exception, long? systemRevision = null) =>
            ValidationFailed?.Invoke(this, new ExplorerValidationFailedEventArgs(exception,
                systemRevision ?? requestedSystemRevision));

        public void RaiseValidationCompleted(bool shellAvailable = true, bool shellChanged = false,
            long? systemRevision = null) =>
            ValidationCompleted?.Invoke(
                this,
                new ExplorerValidationCompletedEventArgs(
                    shellAvailable,
                    shellChanged,
                    shellAvailable ? 11UL : 0UL,
                    systemRevision ?? requestedSystemRevision));

        public void Dispose()
        {
        }
    }

    private sealed class FakeRendererProvider : IRendererProvider
    {
        private readonly bool[] autoPresentFirstFrame;
        private readonly List<FakeRendererSession> sessions = [];

        public FakeRendererProvider(bool[] autoPresentFirstFrame)
        {
            this.autoPresentFirstFrame = autoPresentFirstFrame;
        }

        public RendererDescriptor Descriptor { get; } = new(
            new RendererId("fake.video"),
            "Fake Video Renderer",
            new HashSet<WallpaperKind> { WallpaperKind.Video },
            new HashSet<string>());

        public int SessionCount => sessions.Count;

        public RendererMatch Match(WallpaperDefinition wallpaper, RuntimeEnvironment environment) =>
            new(wallpaper.Kind == WallpaperKind.Video, 100, "Test renderer");

        public Task<IRendererSession> CreateAsync(
            RendererLaunchContext context,
            CancellationToken cancellationToken)
        {
            bool autoPresent = sessions.Count < autoPresentFirstFrame.Length &&
                autoPresentFirstFrame[sessions.Count];
            FakeRendererSession session = new(context.SessionId, context.Generation, autoPresent);
            sessions.Add(session);
            return Task.FromResult<IRendererSession>(session);
        }

        public async Task<FakeRendererSession> WaitForSessionAsync(
            int index,
            CancellationToken cancellationToken)
        {
            while (sessions.Count <= index)
            {
                await Task.Delay(10, cancellationToken);
            }

            return sessions[index];
        }
    }

    private sealed class FakeRendererSession : IRendererSession
    {
        private readonly Channel<RendererEvent> events = Channel.CreateUnbounded<RendererEvent>();
        private readonly bool autoPresentFirstFrame;
        private long currentGeneration;
        private int attachCount;

        public FakeRendererSession(
            SessionId id,
            long generation,
            bool autoPresentFirstFrame)
        {
            Id = id;
            currentGeneration = generation;
            this.autoPresentFirstFrame = autoPresentFirstFrame;
        }

        public SessionId Id { get; }

        public long Generation => currentGeneration;

        public ConcurrentQueue<RendererCommand> Commands { get; } = new();

        public bool IsDisposed { get; private set; }

        public int? FailAttachOnCount { get; set; }

        public int? SuppressSurfaceAttachedOnCount { get; set; }

        public int? FailAfterSurfaceAttachedOnCount { get; set; }

        public int? SuppressFirstFrameOnAttachCount { get; set; }

        public bool SuppressShutdownCompleted { get; set; }

        public bool FailShutdownSend { get; set; }

        public int ShutdownCompletedCount { get; private set; }

        public IAsyncEnumerable<RendererEvent> ReadEventsAsync(CancellationToken cancellationToken) =>
            events.Reader.ReadAllAsync(cancellationToken);

        public Task SendAsync(RendererCommand command, CancellationToken cancellationToken)
        {
            Commands.Enqueue(command);
            if (command is AttachRendererSurface attach)
            {
                attachCount++;
                currentGeneration = attach.Generation;
                if (FailAttachOnCount == attachCount)
                {
                    throw new IOException("Simulated AttachSurface transport failure.");
                }

                if (SuppressSurfaceAttachedOnCount == attachCount)
                {
                    return Task.CompletedTask;
                }

                events.Writer.TryWrite(new RendererSurfaceAttached(currentGeneration));
                if (FailAfterSurfaceAttachedOnCount == attachCount)
                {
                    events.Writer.TryWrite(new RendererFailed(
                        currentGeneration,
                        "renderer.test.recovery_crash",
                        "Simulated Renderer failure after SurfaceAttached.",
                        Recoverable: true));
                    return Task.CompletedTask;
                }

                if (autoPresentFirstFrame && attachCount > 1 &&
                    SuppressFirstFrameOnAttachCount != attachCount)
                {
                    PresentFirstFrame();
                }
            }

            if (command is SetRendererBounds)
            {
                events.Writer.TryWrite(new RendererFirstFramePresented(currentGeneration));
            }

            if (autoPresentFirstFrame && command is LoadRendererContent)
            {
                events.Writer.TryWrite(new RendererContentLoaded(currentGeneration));
                PresentFirstFrame();
            }
            else if (command is LoadRendererContent)
            {
                events.Writer.TryWrite(new RendererContentLoaded(currentGeneration));
            }

            if (command is ShutdownRenderer shutdown)
            {
                if (FailShutdownSend)
                {
                    throw new IOException("Simulated Shutdown transport failure.");
                }
                currentGeneration = shutdown.Generation;
                if (!SuppressShutdownCompleted)
                {
                    ShutdownCompletedCount++;
                    events.Writer.TryWrite(new RendererShutdownCompleted(currentGeneration));
                }
            }

            return Task.CompletedTask;
        }

        public void PresentFirstFrame()
        {
            events.Writer.TryWrite(new RendererFirstFramePresented(currentGeneration));
        }

        public void FailBeforeFirstFrame() =>
            events.Writer.TryWrite(new RendererFailed(
                currentGeneration,
                "renderer.test.failure",
                "Test renderer failed before presenting a frame.",
                Recoverable: false));

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDesktopHost : IDesktopHost
    {
        private int nextSurfaceId;

        public ConcurrentQueue<SurfaceId> DestroyedSurfaces { get; } = new();

        public ConcurrentQueue<SurfaceId> AbandonedSurfaces { get; } = new();

        public ConcurrentQueue<SurfaceReplacement> Replacements { get; } = new();

        public ConcurrentQueue<(SurfaceId SurfaceId, SurfaceRequest Request)> ResizedSurfaces
        { get; } = new();

        public bool FailNextReplacement { get; set; }

        public int TransientTopologyFailuresRemaining { get; set; }

        public int TransientReplacementFailuresRemaining { get; set; }

        public int EnsureTopologyCallCount { get; private set; }

        public Task<DesktopTopology> EnsureTopologyAsync(
            DisplayTopology displays,
            CancellationToken cancellationToken)
        {
            EnsureTopologyCallCount++;
            if (TransientTopologyFailuresRemaining > 0)
            {
                TransientTopologyFailuresRemaining--;
                throw new InvalidOperationException(
                    "UnexpectedTopologyMutation: simulated transient Shell mutation.");
            }
            return Task.FromResult(new DesktopTopology(displays.Revision, []));
        }

        public Task<DesktopSurface> CreateSurfaceAsync(
            SurfaceRequest request,
            CancellationToken cancellationToken)
        {
            int id = Interlocked.Increment(ref nextSurfaceId);
            return Task.FromResult(new DesktopSurface(
                new SurfaceId($"surface-{id}"),
                request.DisplayIds,
                (ulong)id));
        }

        public Task ReplaceSurfacesAsync(
            SurfaceReplacement replacement,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Replacements.Enqueue(replacement);
            if (TransientReplacementFailuresRemaining > 0)
            {
                TransientReplacementFailuresRemaining--;
                throw new InvalidOperationException(
                    "UnexpectedTopologyMutation: simulated parent coordinate-space change.");
            }
            if (FailNextReplacement)
            {
                FailNextReplacement = false;
                throw new InvalidOperationException("Simulated Surface replacement failure.");
            }

            return Task.CompletedTask;
        }

        public Task ResizeSurfaceAsync(
            SurfaceId surfaceId,
            SurfaceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResizedSurfaces.Enqueue((surfaceId, request));
            return Task.CompletedTask;
        }

        public Task DestroySurfaceAsync(
            SurfaceId surfaceId,
            CancellationToken cancellationToken)
        {
            DestroyedSurfaces.Enqueue(surfaceId);
            return Task.CompletedTask;
        }

        public Task AbandonSurfaceAsync(
            SurfaceId surfaceId,
            CancellationToken cancellationToken)
        {
            AbandonedSurfaces.Enqueue(surfaceId);
            return Task.CompletedTask;
        }
    }
}
