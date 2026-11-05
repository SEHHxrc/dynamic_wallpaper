using System.IO.Pipes;
using FluentAssertions;
using LiveWall.Application.Sessions;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Domain.Sessions;
using LiveWall.Host.Orchestration;
using LiveWall.Infrastructure.Ipc;

namespace LiveWall.Application.Tests;

public sealed class RendererAcknowledgedExitTests
{
    [Fact]
    public async Task ActiveFailureNotificationDoesNotConsumeTheStageEvent()
    {
        await using Connection connection = await Connection.CreateAsync();
        connection.Process.Exit(9);
        RendererFailed failure = await connection.Session.ActiveFailure.WaitAsync(TimeSpan.FromSeconds(3));
        failure.ErrorCode.Should().Be("renderer.process.exited");
        await connection.ConsumeAsync<RendererFailed>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPlaybackReplyCannotReleaseBoundsOrNewAttachment(bool newBinding)
    {
        await using Connection connection = await Connection.CreateAsync();
        await connection.RunningAsync();
        await connection.Session.SendAsync(new ChangeRendererState(1, LiveWall.Domain.Playback.PlaybackState.Paused), CancellationToken.None);
        await connection.ReadCommandAsync();
        if (newBinding)
            await connection.Session.SendAsync(new AttachRendererSurface(2, 22, new(0, 0, 640, 360), 1), CancellationToken.None);
        else
            await connection.Session.SendAsync(new SetRendererBounds(2, new(0, 0, 640, 360), 1), CancellationToken.None);
        await connection.ReadCommandAsync();
        await connection.EmitAsync(RendererEventTypes.PlaybackStateChanged, new PlaybackStateChangedPayload("Paused", null, null), 1);
        if (newBinding)
        {
            await connection.EmitAsync(RendererEventTypes.SurfaceAttached, new SurfaceAttachedPayload(23), 2);
            await connection.ConsumeAsync<RendererSurfaceAttached>(); // Stale playback reply is not a new stage.
        }
        else await connection.ConsumeAsync<RendererPlaybackStateChanged>();
        await connection.EmitAsync(RendererEventTypes.FirstFramePresented, new FirstFramePresentedPayload(1), newBinding ? 2 : 1);
        await connection.ConsumeAsync<RendererFirstFramePresented>();
    }

    [Fact]
    public async Task UnsolicitedPlaybackFeedbackIsRejected()
    {
        await using Connection connection = await Connection.CreateAsync();
        await connection.RunningAsync();
        await connection.EmitAsync(RendererEventTypes.PlaybackStateChanged, new PlaybackStateChangedPayload("Paused", null, null), 1);
        RendererFailed failure = await connection.Session.ActiveFailure.WaitAsync(TimeSpan.FromSeconds(3));
        failure.ErrorCode.Should().Be("renderer.protocol.invalid_state");
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    public async Task AcknowledgedRendererCanReturnNormallyBeforeItsProcessIsTerminated(int exitDelay)
    {
        await using Connection connection = await Connection.CreateAsync();
        await connection.AcknowledgeAsync();
        Task naturalExit = Task.Run(async () => { await Task.Delay(exitDelay); connection.Process.Exit(0); });
        await connection.Session.DisposeAsync();
        await naturalExit;
        connection.Process.Terminations.Should().Be(0, "ShutdownCompleted precedes the actual process return");
        connection.Process.ExitCode.Should().Be(0);
        connection.Process.Disposals.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcknowledgedButStalledRendererIsStillTerminatedWithinItsOwnBudget(bool ignoresCancellation)
    {
        await using Connection connection = await Connection.CreateAsync(TimeSpan.FromMilliseconds(50));
        connection.Process.IgnoresCancellation = ignoresCancellation;
        await connection.AcknowledgeAsync();

        await connection.Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        connection.Process.Terminations.Should().Be(1);
        connection.Process.ExitCode.Should().Be(-1);
        connection.Process.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task UnacknowledgedSessionIsTerminatedWithoutWaitingForTheNormalExitGracePeriod()
    {
        await using Connection connection = await Connection.CreateAsync(TimeSpan.FromSeconds(5));

        await connection.Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        connection.Process.Terminations.Should().Be(1);
        connection.Process.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task RepeatedAndConcurrentDisposalWaitForTheSameCleanup()
    {
        await using Connection connection = await Connection.CreateAsync();
        await connection.AcknowledgeAsync();

        Task first = connection.Session.DisposeAsync().AsTask();
        Task second = connection.Session.DisposeAsync().AsTask();
        second.Should().BeSameAs(first);
        second.IsCompleted.Should().BeFalse();
        connection.Process.Exit(0);
        await Task.WhenAll(first, second);
        await connection.Session.DisposeAsync();

        connection.Process.Terminations.Should().Be(0);
        connection.Process.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task AlreadyExitedRendererIsNotTerminatedAgain()
    {
        await using Connection connection = await Connection.CreateAsync();
        await connection.AcknowledgeAsync();
        connection.Process.Exit(0);

        await connection.Session.DisposeAsync();

        connection.Process.Terminations.Should().Be(0);
        connection.Process.Disposals.Should().Be(1);
    }

    private sealed class ProcessHandle : IRendererProcessHandle
    {
        private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 1234;
        public bool HasExited => exit.Task.IsCompleted;
        public int? ExitCode { get; private set; }
        public int Terminations { get; private set; }
        public int Disposals { get; private set; }
        public bool IgnoresCancellation { get; set; }
        public Task WaitForExitAsync(CancellationToken token) => IgnoresCancellation ? exit.Task : exit.Task.WaitAsync(token);
        public void Exit(int code)
        {
            if (HasExited) return;
            ExitCode = code; exit.TrySetResult();
        }
        public void Terminate() { Terminations++; Exit(-1); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly NamedPipeServerStream server;
        private readonly NamedPipeClientStream client;
        private readonly LengthPrefixedJsonChannel renderer;
        public ProcessHandle Process { get; } = new();
        public RendererProtocolSession Session { get; }
        private Connection(NamedPipeServerStream server, NamedPipeClientStream client, TimeSpan? exitTimeout)
        {
            this.server = server; this.client = client;
            renderer = new(client, leaveOpen: true);
            Session = new(new SessionId("acknowledged-exit"), 1, new(server, leaveOpen: true), Process,
                processExitTimeout: exitTimeout);
        }
        public static async Task<Connection> CreateAsync(TimeSpan? exitTimeout = null)
        {
            string pipe = @"LOCAL\livewall.ackexit." + Guid.NewGuid().ToString("N");
            NamedPipeServerStream server = LocalNamedPipeFactory.CreateServer(pipe, 1, true);
            NamedPipeClientStream client = LocalNamedPipeFactory.CreateClient(pipe);
            using CancellationTokenSource budget = new(TimeSpan.FromSeconds(5));
            Task connected = server.WaitForConnectionAsync(budget.Token);
            await client.ConnectAsync(budget.Token); await connected;
            return new(server, client, exitTimeout);
        }
        public async Task AcknowledgeAsync()
        {
            using CancellationTokenSource budget = new(TimeSpan.FromSeconds(5));
            await Session.SendAsync(new ShutdownRenderer(1, "test-complete"), budget.Token);
            RendererEnvelope shutdown = await renderer.ReadAsync<RendererEnvelope>(budget.Token);
            shutdown.Type.Should().Be(RendererCommandTypes.Shutdown);
            await renderer.WriteAsync(RendererProtocolMapper.CreateEnvelope("acknowledged-exit", 1,
                RendererEventTypes.ShutdownCompleted, new ShutdownCompletedPayload("test-complete")), budget.Token);
            await using var events = Session.ReadEventsAsync(budget.Token).GetAsyncEnumerator();
            (await events.MoveNextAsync()).Should().BeTrue();
            events.Current.Should().BeOfType<RendererShutdownCompleted>();
        }
        public Task<RendererEnvelope> ReadCommandAsync() => renderer.ReadAsync<RendererEnvelope>().AsTask();
        public Task EmitAsync<T>(string type, T payload, long generation) => renderer.WriteAsync(
            RendererProtocolMapper.CreateEnvelope("acknowledged-exit", generation, type, payload)).AsTask();
        public async Task ConsumeAsync<T>() where T : RendererEvent
        {
            using CancellationTokenSource budget = new(TimeSpan.FromSeconds(3));
            await using var events = Session.ReadEventsAsync(budget.Token).GetAsyncEnumerator();
            (await events.MoveNextAsync()).Should().BeTrue();
            events.Current.Should().BeOfType<T>();
        }
        public async Task RunningAsync()
        {
            await Session.SendAsync(new AttachRendererSurface(1, 11, new(0, 0, 320, 180), 1), CancellationToken.None);
            await ReadCommandAsync(); await EmitAsync(RendererEventTypes.SurfaceAttached, new SurfaceAttachedPayload(12), 1);
            await ConsumeAsync<RendererSurfaceAttached>();
            var wallpaper = new LiveWall.Domain.Wallpapers.WallpaperDefinition(new("test"), "1", LiveWall.Domain.Wallpapers.WallpaperKind.Video,
                "motion.avi", new("test", null, null, null, new HashSet<string>()), new(false, false, false, false, 24), LiveWall.Domain.Wallpapers.WallpaperOrigin.Native);
            await Session.SendAsync(new LoadRendererContent(1, wallpaper, @"E:\content"), CancellationToken.None);
            await ReadCommandAsync(); await EmitAsync(RendererEventTypes.ContentLoaded, new ContentLoadedPayload("test"), 1);
            await ConsumeAsync<RendererContentLoaded>();
            await EmitAsync(RendererEventTypes.FirstFramePresented, new FirstFramePresentedPayload(1), 1);
            await ConsumeAsync<RendererFirstFramePresented>();
        }
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await renderer.DisposeAsync(); await client.DisposeAsync(); await server.DisposeAsync();
        }
    }
}
