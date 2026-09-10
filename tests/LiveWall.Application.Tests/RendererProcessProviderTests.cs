using System.IO.Pipes;
using FluentAssertions;
using LiveWall.Application.Sessions;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Diagnostics;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.Orchestration;
using LiveWall.Infrastructure.Ipc;

namespace LiveWall.Application.Tests;

public sealed class RendererProcessProviderTests
{
    private static readonly Lock EnvironmentGate = new();

    [Fact]
    public async Task CreateReturnsOnlyAfterHelloInitializeAndInitialized()
    {
        using FakeRendererProcessLauncher launcher = new(FakeRendererBehavior.CompleteHandshake);
        RendererProcessProvider provider = CreateProvider(launcher, TimeSpan.FromSeconds(2));

        await using IRendererSession session = await provider.CreateAsync(
            CreateContext(),
            CancellationToken.None);

        session.Id.Should().Be(new SessionId("protocol-session"));
        launcher.InitializeObserved.Should().BeTrue();
        launcher.Terminated.Should().BeFalse();
    }

    [Fact]
    public async Task ObservedCreateReportsProcessAndHandshakeMilestonesInOrder()
    {
        using FakeRendererProcessLauncher launcher = new(FakeRendererBehavior.CompleteHandshake);
        RendererProcessProvider provider = CreateProvider(launcher, TimeSpan.FromSeconds(2));
        List<RendererProcessMilestone> milestones = [];

        await using IRendererSession session = await provider.CreateObservedAsync(
            CreateContext(),
            milestone =>
            {
                if (!milestone.IsPhaseStart)
                {
                    milestones.Add(milestone);
                }
            },
            CancellationToken.None);

        milestones.Select(milestone => milestone.Phase).Should().Equal(
            ApplyGenerationPhase.ProcessStarted,
            ApplyGenerationPhase.PipeConnected,
            ApplyGenerationPhase.HelloReceived,
            ApplyGenerationPhase.Initialized);
        milestones.Should().OnlyContain(milestone =>
            milestone.Outcome == ApplyStageOutcome.Success &&
            milestone.FailureReason == ApplyFailureReason.None);
    }

    [Fact]
    public async Task InvalidHelloTokenNeverReturnsHalfInitializedSession()
    {
        using FakeRendererProcessLauncher launcher = new(FakeRendererBehavior.InvalidToken);
        RendererProcessProvider provider = CreateProvider(launcher, TimeSpan.FromSeconds(2));

        Func<Task> act = async () => await provider.CreateAsync(
            CreateContext(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*authentication failed*");
        launcher.Terminated.Should().BeTrue();
    }

    [Fact]
    public async Task HandshakeTimeoutTerminatesRendererAndReportsTimeout()
    {
        using FakeRendererProcessLauncher launcher = new(FakeRendererBehavior.NeverConnect);
        RendererProcessProvider provider = CreateProvider(launcher, TimeSpan.FromMilliseconds(50));

        Func<Task> act = async () => await provider.CreateAsync(
            CreateContext(),
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        launcher.Terminated.Should().BeTrue();
    }

    [Fact]
    public async Task ObservedHandshakeTimeoutIdentifiesPipeConnectionStage()
    {
        using FakeRendererProcessLauncher launcher = new(FakeRendererBehavior.NeverConnect);
        RendererProcessProvider provider = CreateProvider(launcher, TimeSpan.FromMilliseconds(50));
        List<RendererProcessMilestone> milestones = [];

        Func<Task> act = async () => await provider.CreateObservedAsync(
            CreateContext(),
            milestones.Add,
            CancellationToken.None);

        ApplyStageException failure = (await act.Should().ThrowAsync<ApplyStageException>())
            .Which;
        failure.Phase.Should().Be(ApplyGenerationPhase.PipeConnected);
        failure.Reason.Should().Be(ApplyFailureReason.PipeConnectionFailed);
        milestones.Should().ContainSingle(milestone =>
            !milestone.IsPhaseStart &&
            milestone.Phase == ApplyGenerationPhase.PipeConnected &&
            milestone.Outcome == ApplyStageOutcome.Timeout);
        launcher.Terminated.Should().BeTrue();
    }

    [Fact]
    public async Task DuplicateRendererMessageIdBecomesFatalProtocolFailure()
    {
        using FakeRendererProcessLauncher launcher = new(FakeRendererBehavior.DuplicateSurfaceEvent);
        RendererProcessProvider provider = CreateProvider(launcher, TimeSpan.FromSeconds(2));
        await using IRendererSession session = await provider.CreateAsync(
            CreateContext(),
            CancellationToken.None);
        await session.SendAsync(
            new AttachRendererSurface(
                1,
                42,
                new LiveWall.Domain.Displays.DisplayBounds(0, 0, 100, 100),
                1),
            CancellationToken.None);

        await using IAsyncEnumerator<RendererEvent> events = session
            .ReadEventsAsync(CancellationToken.None)
            .GetAsyncEnumerator();
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Should().BeOfType<RendererSurfaceAttached>();
        (await events.MoveNextAsync()).Should().BeTrue();
        RendererFailed failure = events.Current.Should().BeOfType<RendererFailed>().Subject;
        failure.ErrorCode.Should().Be("renderer.protocol.invalid_state");
        failure.Message.Should().Contain("reused messageId");
    }

    [Fact]
    public async Task UnknownRendererEventBecomesFatalProtocolFailure()
    {
        using FakeRendererProcessLauncher launcher = new(FakeRendererBehavior.UnknownEvent);
        RendererProcessProvider provider = CreateProvider(launcher, TimeSpan.FromSeconds(2));
        await using IRendererSession session = await provider.CreateAsync(
            CreateContext(),
            CancellationToken.None);
        await session.SendAsync(
            new AttachRendererSurface(
                1,
                42,
                new LiveWall.Domain.Displays.DisplayBounds(0, 0, 100, 100),
                1),
            CancellationToken.None);

        await using IAsyncEnumerator<RendererEvent> events = session
            .ReadEventsAsync(CancellationToken.None)
            .GetAsyncEnumerator();
        (await events.MoveNextAsync()).Should().BeTrue();
        RendererFailed failure = events.Current.Should().BeOfType<RendererFailed>().Subject;
        failure.Message.Should().Contain("UnknownAfterInitialization");
    }

    [Fact]
    public async Task UnexpectedRendererProcessExitIsPublishedAsRecoverableFailure()
    {
        using FakeRendererProcessLauncher launcher = new(FakeRendererBehavior.ExitAfterHandshake);
        RendererProcessProvider provider = CreateProvider(launcher, TimeSpan.FromSeconds(2));
        await using IRendererSession session = await provider.CreateAsync(
            CreateContext(),
            CancellationToken.None);

        await using IAsyncEnumerator<RendererEvent> events = session
            .ReadEventsAsync(CancellationToken.None)
            .GetAsyncEnumerator();
        (await events.MoveNextAsync()).Should().BeTrue();
        RendererFailed failure = events.Current.Should().BeOfType<RendererFailed>().Subject;
        failure.ErrorCode.Should().Be("renderer.process.exited");
        failure.Recoverable.Should().BeTrue();
    }

    [Fact]
    public async Task DiagnosticEnvironmentFactoryReceivesLaunchCoordinatesAndTargetsChildOnly()
    {
        using FakeRendererProcessLauncher launcher = new(FakeRendererBehavior.CompleteHandshake);
        List<(int Iteration, int SurfaceOrdinal)> coordinates = [];
        RendererProcessProvider provider = new(
            new RendererProcessProviderOptions(
                new RendererDescriptor(
                    new RendererId("test.renderer"),
                    "Test Renderer",
                    new HashSet<WallpaperKind> { WallpaperKind.Video },
                    new HashSet<string> { "HwndChild" }),
                "renderer-test.exe",
                "tests",
                ["HwndChild"],
                "en-US",
                TimeSpan.FromSeconds(2)),
            launcher,
            (_, iteration, surfaceOrdinal) =>
            {
                coordinates.Add((iteration, surfaceOrdinal));
                return new Dictionary<string, string> { ["LIVEWALL_TEST_FAULT"] = "injected" };
            });

        await using IRendererSession session = await provider.CreateAsync(
            CreateContext(),
            CancellationToken.None);

        coordinates.Should().ContainSingle().Which.Should().Be((1, 1));
        launcher.LastRequest.Should().NotBeNull();
        launcher.LastRequest!.EnvironmentVariables.Should().Contain(
            "LIVEWALL_TEST_FAULT",
            "injected");
    }

    [Fact]
    public void ProbeFaultPlanRoundTripsAndClearsEnvironmentImmediately()
    {
        ProbeFaultPlan expected = new(
            1,
            2,
            ProbeFaultPhase.FirstFramePresented,
            ProbeFaultAction.SuppressEvent);

        lock (EnvironmentGate)
        {
            Environment.SetEnvironmentVariable(
                ProbeFaultPlan.EnvironmentVariableName,
                expected.Serialize());
            ProbeFaultPlan? actual = ProbeFaultPlan.ReadAndClearFromEnvironment();

            actual.Should().Be(expected);
            Environment.GetEnvironmentVariable(ProbeFaultPlan.EnvironmentVariableName)
                .Should().BeNull();
        }
    }

    private static RendererProcessProvider CreateProvider(
        IRendererProcessLauncher launcher,
        TimeSpan timeout) =>
        new(
            new RendererProcessProviderOptions(
                new RendererDescriptor(
                    new RendererId("test.renderer"),
                    "Test Renderer",
                    new HashSet<WallpaperKind> { WallpaperKind.Video },
                    new HashSet<string> { "HwndChild" }),
                "renderer-test.exe",
                "tests",
                ["HwndChild"],
                "en-US",
                timeout),
            launcher);

    private static RendererLaunchContext CreateContext() => new(
        new SessionId("protocol-session"),
        1,
        new WallpaperDefinition(
            new WallpaperId("test.wallpaper"),
            "1.0.0",
            WallpaperKind.Video,
            "video.mp4",
            new WallpaperMetadata("Test", null, null, null, new HashSet<string>()),
            new WallpaperCapabilities(false, false, false, false, 30),
            WallpaperOrigin.Native),
        "C:\\content");

    private enum FakeRendererBehavior
    {
        CompleteHandshake,
        InvalidToken,
        NeverConnect,
        DuplicateSurfaceEvent,
        UnknownEvent,
        ExitAfterHandshake,
    }

    private sealed class FakeRendererProcessLauncher(FakeRendererBehavior behavior)
        : IRendererProcessLauncher, IDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private Task? runTask;

        public bool InitializeObserved { get; private set; }

        public bool Terminated { get; private set; }

        public RendererProcessStartRequest? LastRequest { get; private set; }

        public IRendererProcessHandle Start(RendererProcessStartRequest request)
        {
            LastRequest = request;
            runTask = RunAsync(request, lifetime.Token);
            return new FakeRendererProcessHandle(this);
        }

        public void Dispose() => lifetime.Dispose();

        private async Task RunAsync(
            RendererProcessStartRequest request,
            CancellationToken cancellationToken)
        {
            if (behavior == FakeRendererBehavior.NeverConnect)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            await using NamedPipeClientStream pipe = LocalNamedPipeFactory.CreateClient(request.PipeName);
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await using LengthPrefixedJsonChannel channel = new(pipe);
            string token = behavior == FakeRendererBehavior.InvalidToken
                ? new string('0', 64)
                : request.AuthenticationToken;
            await channel.WriteAsync(RendererProtocolMapper.CreateEnvelope(
                    request.SessionId,
                    0,
                    RendererEventTypes.Hello,
                    new HelloPayload("fake", "1.0", ["HwndChild"], token)),
                cancellationToken).ConfigureAwait(false);
            if (behavior == FakeRendererBehavior.InvalidToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            RendererEnvelope initialize = await channel.ReadAsync<RendererEnvelope>(cancellationToken)
                .ConfigureAwait(false);
            InitializeObserved = initialize.Type == RendererCommandTypes.Initialize;
            await channel.WriteAsync(RendererProtocolMapper.CreateEnvelope(
                    request.SessionId,
                    initialize.Generation,
                    RendererEventTypes.Initialized,
                    new InitializedPayload(["HwndChild"])),
                cancellationToken).ConfigureAwait(false);
            if (behavior == FakeRendererBehavior.ExitAfterHandshake)
            {
                return;
            }
            if (behavior is FakeRendererBehavior.DuplicateSurfaceEvent or
                FakeRendererBehavior.UnknownEvent)
            {
                RendererEnvelope attach = await channel.ReadAsync<RendererEnvelope>(cancellationToken)
                    .ConfigureAwait(false);
                if (behavior == FakeRendererBehavior.UnknownEvent)
                {
                    await channel.WriteAsync(RendererProtocolMapper.CreateEnvelope(
                            request.SessionId,
                            attach.Generation,
                            "UnknownAfterInitialization",
                            new EmptyPayload()),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    RendererEnvelope attached = new(
                        ProtocolVersion.Version1,
                        "duplicate-event-id",
                        request.SessionId,
                        attach.Generation,
                        RendererEventTypes.SurfaceAttached,
                        System.Text.Json.JsonSerializer.SerializeToElement(
                            new SurfaceAttachedPayload(84)));
                    await channel.WriteAsync(attached, cancellationToken).ConfigureAwait(false);
                    await channel.WriteAsync(attached, cancellationToken).ConfigureAwait(false);
                }
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        private sealed class FakeRendererProcessHandle(FakeRendererProcessLauncher owner)
            : IRendererProcessHandle
        {
            public int Id => 4242;

            public bool HasExited => owner.runTask?.IsCompleted ?? false;

            public int? ExitCode => HasExited ? 0 : null;

            public Task WaitForExitAsync(CancellationToken cancellationToken) =>
                (owner.runTask ?? Task.CompletedTask).WaitAsync(cancellationToken);

            public void Terminate()
            {
                owner.Terminated = true;
                owner.lifetime.Cancel();
            }

            public async ValueTask DisposeAsync()
            {
                if (owner.runTask is not null)
                {
                    try
                    {
                        await owner.runTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                owner.lifetime.Dispose();
            }
        }
    }
}
