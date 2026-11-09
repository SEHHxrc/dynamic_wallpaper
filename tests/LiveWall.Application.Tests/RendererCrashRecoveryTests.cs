using FluentAssertions;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.CommandLoop;
using LiveWall.Host.Orchestration;

namespace LiveWall.Application.Tests;

// ARCHITECTURE §11.5: restart at once, then after 2 s and 8 s; a fourth failure inside 60 s opens the circuit.
public sealed partial class HostCommandLoopTests
{
    [Fact]
    public async Task AnActiveRendererFailureIsRestartedAtOnceWithAFreshRendererAndSurface()
    {
        ManualTimeProvider time = new();
        TestRuntime runtime = new(true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop(timeProvider: time);
        WallpaperSession first = await ApplyPrimaryAsync(loop);
        FakeRendererSession crashed = await runtime.Provider.WaitForSessionAsync(0, CancellationToken.None);

        await CrashAsync(loop, first);
        WallpaperSession restarted = await WaitForReplacementAsync(loop, first.Id);
        await WaitForRecoveryAsync(loop, RendererRecoveryOutcome.Restarted);

        restarted.WallpaperId.Should().Be(first.WallpaperId);
        restarted.Displays.Should().Equal(first.Displays);
        runtime.Provider.SessionCount.Should().Be(2);
        runtime.Desktop.Replacements.Should().HaveCount(2);
        await WaitUntilAsync(() => crashed.IsDisposed, CancellationToken.None);
        Outcomes(loop).Should().Equal(
            RendererRecoveryOutcome.RestartScheduled,
            RendererRecoveryOutcome.RestartStarted,
            RendererRecoveryOutcome.Restarted);
        RendererRecoveryEntry scheduled = loop.GetRendererRecoveryJournalForDiagnostics()[0];
        scheduled.RecentFailures.Should().Be(1);
        scheduled.Delay.Should().Be(TimeSpan.Zero);
        scheduled.Detail.Should().Be("renderer.process.exited");
        (await GetStateAsync(loop)).Assignments.Should().ContainSingle();
    }

    [Fact]
    public async Task RepeatedFailuresBackOffTwoThenEightSecondsAndThenOpenTheCircuit()
    {
        ManualTimeProvider time = new();
        TestRuntime runtime = new(true, true, true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop(timeProvider: time);
        WallpaperSession session = await ApplyPrimaryAsync(loop);

        await CrashAsync(loop, session);
        session = await WaitForReplacementAsync(loop, session.Id);
        foreach (TimeSpan delay in new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8) })
        {
            await CrashAsync(loop, session);
            await WaitUntilAsync(() => time.PendingTimerCount == 1, CancellationToken.None);
            time.Advance(delay - TimeSpan.FromMilliseconds(1));
            (await GetStateAsync(loop)).Sessions.Should().BeEmpty();
            time.Advance(TimeSpan.FromMilliseconds(1));
            session = await WaitForReplacementAsync(loop, session.Id);
        }

        await CrashAsync(loop, session);
        await WaitForRecoveryAsync(loop, RendererRecoveryOutcome.CircuitBroken);
        time.Advance(TimeSpan.FromMinutes(5));

        HostStateSnapshot state = await GetStateAsync(loop);
        state.Sessions.Should().BeEmpty();
        state.Assignments.Should().ContainSingle();
        runtime.Provider.SessionCount.Should().Be(4);
        ScheduledDelays(loop).Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));
        RendererRecoveryEntry circuit = loop.GetRendererRecoveryJournalForDiagnostics()
            .Single(entry => entry.Outcome == RendererRecoveryOutcome.CircuitBroken);
        circuit.RecentFailures.Should().Be(4);
    }

    [Fact]
    public async Task FailuresOlderThanTheSixtySecondWindowAreForgotten()
    {
        ManualTimeProvider time = new();
        TestRuntime runtime = new(true, true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop(timeProvider: time);
        WallpaperSession session = await ApplyPrimaryAsync(loop);

        await CrashAsync(loop, session);
        session = await WaitForReplacementAsync(loop, session.Id);
        time.Advance(TimeSpan.FromSeconds(61));
        await CrashAsync(loop, session);
        await WaitForReplacementAsync(loop, session.Id);

        ScheduledDelays(loop).Should().Equal(TimeSpan.Zero, TimeSpan.Zero);
        loop.GetRendererRecoveryJournalForDiagnostics()
            .Where(entry => entry.Outcome == RendererRecoveryOutcome.RestartScheduled)
            .Select(entry => entry.RecentFailures)
            .Should().Equal(1, 1);
    }

    [Fact]
    public async Task RestartAttemptsThatFailCountTowardsTheCircuit()
    {
        ManualTimeProvider time = new();
        TestRuntime runtime = new(true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop(timeProvider: time);
        WallpaperSession session = await ApplyPrimaryAsync(loop);

        await OpenCircuitWithFailingRestartsAsync(loop, runtime, time, session);

        HostStateSnapshot state = await GetStateAsync(loop);
        state.Sessions.Should().BeEmpty();
        state.Assignments.Should().ContainSingle();
        runtime.Provider.SessionCount.Should().Be(4);
        Outcomes(loop).Count(outcome => outcome == RendererRecoveryOutcome.RestartFailed).Should().Be(3);
        ScheduledDelays(loop).Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task AnExplicitApplyClosesTheCircuitAndStartsCountingAgain()
    {
        ManualTimeProvider time = new();
        TestRuntime runtime = new(true, false, false, false, true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop(timeProvider: time);
        WallpaperSession session = await ApplyPrimaryAsync(loop);
        await OpenCircuitWithFailingRestartsAsync(loop, runtime, time, session);
        runtime.Provider.ConfigureSession = null;

        await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        WallpaperSession applied = (await WaitForStateAsync(loop, state => state.Sessions.Count == 1)).Sessions[0].Session;
        await CrashAsync(loop, applied);
        await WaitForReplacementAsync(loop, applied.Id);

        RendererRecoveryEntry last = loop.GetRendererRecoveryJournalForDiagnostics()
            .Last(entry => entry.Outcome == RendererRecoveryOutcome.RestartScheduled);
        last.RecentFailures.Should().Be(1);
        last.Delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task OnlyTheFailedDisplayIsRestarted()
    {
        ManualTimeProvider time = new();
        TestRuntime runtime = new(true, true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop(timeProvider: time);
        DisplayId primary = new("primary");
        DisplayId secondary = new("secondary");
        await loop.SendAsync(new DisplayTopologyChangedCommand(CreateDualDisplayTopology()), CancellationToken.None);
        await loop.SendAsync(
            new ApplyWallpaperCommand(new WallpaperId("sample.wallpaper"), [primary, secondary],
                LayoutMode.PerDisplay, FitMode.Cover),
            CancellationToken.None);
        HostStateSnapshot applied = await WaitForStateAsync(loop, state => state.Sessions.Count == 2);
        WallpaperSession failed = applied.Sessions.Single(item => item.Session.Displays[0] == secondary).Session;
        WallpaperSession kept = applied.Sessions.Single(item => item.Session.Displays[0] == primary).Session;

        await CrashAsync(loop, failed);
        HostStateSnapshot restarted = await WaitForStateAsync(loop, state =>
            state.Sessions.Count == 2 && state.Sessions.All(item => item.Session.Id != failed.Id));

        restarted.Sessions.Should().Contain(item => item.Session.Id == kept.Id);
        restarted.Assignments.Should().HaveCount(2);
        runtime.Provider.SessionCount.Should().Be(3);
        loop.GetRendererRecoveryJournalForDiagnostics()[0].Displays.Should().Equal(secondary);
    }

    [Fact]
    public async Task APendingRestartIsDroppedWhenAnExplicitApplyGetsThereFirst()
    {
        ManualTimeProvider time = new();
        TestRuntime runtime = new(true, true, true);
        await using HostCommandLoop loop = runtime.CreateCommandLoop(timeProvider: time);
        WallpaperSession session = await ApplyPrimaryAsync(loop);
        await CrashAsync(loop, session);
        session = await WaitForReplacementAsync(loop, session.Id);
        await CrashAsync(loop, session);
        await WaitUntilAsync(() => time.PendingTimerCount == 1, CancellationToken.None);

        await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        WallpaperSession applied = await WaitForReplacementAsync(loop, session.Id);
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitForRecoveryAsync(loop, RendererRecoveryOutcome.Superseded);

        (await GetStateAsync(loop)).Sessions.Should().ContainSingle().Which.Session.Id.Should().Be(applied.Id);
        runtime.Provider.SessionCount.Should().Be(3);
        loop.GetRendererRecoveryJournalForDiagnostics()[^1].Detail.Should().Be("already-presented");
    }

    [Fact]
    public async Task HostShutdownEndsAPendingRestartWithoutReportingAFault()
    {
        ManualTimeProvider time = new();
        TestRuntime runtime = new(true, true);
        HostCommandLoop loop = runtime.CreateCommandLoop(timeProvider: time);
        WallpaperSession session = await ApplyPrimaryAsync(loop);
        await CrashAsync(loop, session);
        session = await WaitForReplacementAsync(loop, session.Id);
        await CrashAsync(loop, session);
        await WaitUntilAsync(() => time.PendingTimerCount == 1, CancellationToken.None);

        Func<Task> dispose = async () => await loop.DisposeAsync();

        await dispose.Should().CompleteWithinAsync(TimeSpan.FromSeconds(10));
        runtime.Provider.SessionCount.Should().Be(2);
        Outcomes(loop).Count(outcome => outcome == RendererRecoveryOutcome.RestartStarted).Should().Be(1);
    }

    private static async Task OpenCircuitWithFailingRestartsAsync(
        HostCommandLoop loop,
        TestRuntime runtime,
        ManualTimeProvider time,
        WallpaperSession session)
    {
        runtime.Provider.ConfigureSession = renderer => renderer.FailAttachOnCount = 1;
        await CrashAsync(loop, session);
        foreach (TimeSpan delay in new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8) })
        {
            await WaitUntilAsync(() => time.PendingTimerCount == 1, CancellationToken.None);
            time.Advance(delay);
        }

        await WaitForRecoveryAsync(loop, RendererRecoveryOutcome.CircuitBroken);
    }

    private static async Task<WallpaperSession> ApplyPrimaryAsync(HostCommandLoop loop)
    {
        await SetTopologyAsync(loop);
        await loop.SendAsync(CreateApplyCommand(), CancellationToken.None);
        return (await WaitForStateAsync(loop, state => state.Sessions.Count == 1)).Sessions[0].Session;
    }

    private static Task<HostCommandResult> CrashAsync(HostCommandLoop loop, WallpaperSession session) =>
        loop.SendAsync(
            new RendererEventCommand(
                session.Id,
                session.Generation,
                new RendererFailed(session.Generation, "renderer.process.exited", "Simulated Renderer crash.", Recoverable: true)),
            CancellationToken.None);

    private static async Task<WallpaperSession> WaitForReplacementAsync(HostCommandLoop loop, SessionId previous) =>
        (await WaitForStateAsync(loop, state => state.Sessions.Count == 1 && state.Sessions[0].Session.Id != previous))
            .Sessions[0].Session;

    private static Task WaitForRecoveryAsync(HostCommandLoop loop, RendererRecoveryOutcome outcome) =>
        WaitUntilAsync(
            () => loop.GetRendererRecoveryJournalForDiagnostics().Any(entry => entry.Outcome == outcome),
            CancellationToken.None);

    private static RendererRecoveryOutcome[] Outcomes(HostCommandLoop loop) =>
        loop.GetRendererRecoveryJournalForDiagnostics().Select(entry => entry.Outcome).ToArray();

    private static TimeSpan[] ScheduledDelays(HostCommandLoop loop) =>
        loop.GetRendererRecoveryJournalForDiagnostics()
            .Where(entry => entry.Outcome == RendererRecoveryOutcome.RestartScheduled)
            .Select(entry => entry.Delay)
            .ToArray();
}
