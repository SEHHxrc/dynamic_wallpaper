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

namespace LiveWall.Application.Tests;

public sealed class HostCommandLoopTests
{
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

        await commandLoop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await WaitUntilAsync(
            () => runtime.Desktop.Replacements.Count == 2 &&
                runtime.Desktop.AbandonedSurfaces.Contains(new SurfaceId("surface-1")),
            CancellationToken.None);
        HostStateSnapshot recovered = await GetStateAsync(commandLoop);

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

        await commandLoop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        HostStateSnapshot rebuilt = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 1);

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

        await commandLoop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        HostStateSnapshot rebuilt = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 1);

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

        await commandLoop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        HostStateSnapshot rebuilt = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 1);

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

        await commandLoop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        await commandLoop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        HostStateSnapshot rebuilt = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 1);

        rebuilt.Generation.Should().Be(3);
        renderer.Commands.OfType<AttachRendererSurface>().Should().HaveCount(2);
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

        await commandLoop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
        HostStateSnapshot rebuilt = await WaitForStateAsync(
            commandLoop,
            state => state.Generation == 3 && state.Sessions.Count == 2);

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

        await commandLoop.SendAsync(new ExplorerRestartedCommand(), CancellationToken.None);
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
        state.Generation.Should().Be(1);
        state.PlaybackPolicy.Should().Be(policy);
        state.Assignments.Should().ContainSingle().Which.Should().Be(connected);

        await bootstrapper.PersistAsync(state, CancellationToken.None);
        store.SavedPolicy.Should().Be(policy);
        store.SavedAssignments.Should().BeEquivalentTo([connected]);
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

        public HostCommandLoop CreateCommandLoop()
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
            return new HostCommandLoop(Coordinator, new DefaultPlaybackPolicyEvaluator());
        }
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
                currentGeneration = shutdown.Generation;
                if (!SuppressShutdownCompleted)
                {
                    ShutdownCompletedCount++;
                    events.Writer.TryWrite(new RendererShutdownCompleted(currentGeneration));
                }
            }

            return Task.CompletedTask;
        }

        public void PresentFirstFrame() =>
            events.Writer.TryWrite(new RendererFirstFramePresented(currentGeneration));

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

        public bool FailNextReplacement { get; set; }

        public Task<DesktopTopology> EnsureTopologyAsync(
            DisplayTopology displays,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DesktopTopology(displays.Revision, []));

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
            if (FailNextReplacement)
            {
                FailNextReplacement = false;
                throw new InvalidOperationException("Simulated Surface replacement failure.");
            }

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
