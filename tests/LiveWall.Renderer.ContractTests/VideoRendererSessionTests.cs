using System.IO.Pipes;
using System.Text.Json;
using FluentAssertions;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Infrastructure.Ipc;
using LiveWall.Renderer.Video.Protocol;

namespace LiveWall.Renderer.ContractTests;

public sealed class VideoRendererSessionTests
{
    [Theory]
    [InlineData("Pause")]
    [InlineData("Resume")]
    [InlineData("Suspend")]
    public async Task ShutdownSuppressesAPlaybackCompletionThatIgnoresCancellation(string command)
    {
        await using Harness run = await Harness.CreateAsync();
        await run.LoadAsync();
        run.Backend.BlockOperation = "Playback";
        run.Backend.ReturnLateControl = true;
        await run.SendAsync(command, new EmptyPayload());
        await run.Backend.OperationEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // StopAsync requires the next event to be ShutdownCompleted, never playback success.
        await run.StopAsync();
        run.Backend.DisposeCount.Should().Be(1);
    }

    [Theory]
    [InlineData("Throttle")]
    [InlineData("SetProperties")]
    [InlineData("SetAudioFrame")]
    [InlineData("CapturePreview")]
    public async Task UnimplementedCommandsFailExplicitlyWithoutSuccess(string command)
    {
        await using Harness run = await Harness.CreateAsync();
        await run.LoadAsync();
        await run.SendAsync(command, new EmptyPayload());
        var error = await run.ExpectAsync(RendererEventTypes.FatalError);
        error.Payload.GetProperty("code").GetString().Should().Be(RendererErrorCodes.ProtocolMessageUnsupported);
        await run.ExpectFailureAsync();
        run.Backend.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task FormalWireOrderSupportsPausedReattachAndBoundsWithoutAnotherLoad()
    {
        await using Harness run = await Harness.CreateAsync();
        await run.LoadAsync();
        await run.SendAsync(RendererCommandTypes.Pause, new EmptyPayload());
        (await run.ExpectAsync(RendererEventTypes.PlaybackStateChanged)).Payload.GetProperty("state").GetString().Should().Be("Paused");
        await run.SendAsync(RendererCommandTypes.SetBounds, new SetBoundsPayload(0, 0, 640, 360, 1), 2);
        (await run.ExpectAsync(RendererEventTypes.FirstFramePresented)).Generation.Should().Be(1);
        await run.SendAsync(RendererCommandTypes.AttachSurface, new AttachSurfacePayload(22, 640, 360, 1), 2);
        (await run.ExpectAsync(RendererEventTypes.SurfaceAttached)).Generation.Should().Be(2);
        (await run.ExpectAsync(RendererEventTypes.FirstFramePresented)).Generation.Should().Be(2);
        run.Backend.LoadCount.Should().Be(1);
        run.Backend.Playback.Should().Be("Paused");
        run.Backend.Submissions.Should().HaveCount(2);
        run.Backend.Submissions[1].FrameSequence.Should().BeGreaterThan(run.Backend.Submissions[0].FrameSequence);
        run.Backend.Submissions[1].Ticket.BindingEpoch.Should().NotBe(run.Backend.Submissions[0].Ticket.BindingEpoch);
        await run.StopAsync(2);
        run.Backend.DisposeCount.Should().Be(1);
    }

    [Theory]
    [InlineData("AttachSurface")]
    [InlineData("LoadWallpaper")]
    [InlineData("Play")]
    [InlineData("SetBounds")]
    public async Task CommandsBeforeInitializeFailWithoutInitializingBackend(string type)
    {
        await using Harness run = await Harness.CreateAsync();
        await run.SendAsync(type, new EmptyPayload());
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
        run.Backend.InitializeCount.Should().Be(0);
        run.Backend.DisposeCount.Should().Be(1);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("session")]
    [InlineData("message")]
    [InlineData("payload")]
    [InlineData("generation")]
    public async Task InvalidEnvelopeNeverReachesBackend(string field)
    {
        await using Harness run = await Harness.CreateAsync();
        RendererEnvelope envelope = run.Envelope(RendererCommandTypes.Initialize, Initialize);
        envelope = field switch
        {
            "version" => envelope with { Protocol = new(2, 0) },
            "session" => envelope with { SessionId = "another" },
            "message" => envelope with { MessageId = null! },
            "payload" => envelope with { Payload = JsonSerializer.SerializeToElement<object?>(null) },
            _ => envelope with { Generation = -1 },
        };
        await run.Host.WriteAsync(envelope);
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
        run.Backend.InitializeCount.Should().Be(0);
    }

    [Fact]
    public async Task DuplicateMessageIsNotAppliedTwice()
    {
        await using Harness run = await Harness.CreateAsync();
        RendererEnvelope initialize = run.Envelope(RendererCommandTypes.Initialize, Initialize);
        await run.Host.WriteAsync(initialize);
        await run.ExpectAsync(RendererEventTypes.Initialized);
        await run.Host.WriteAsync(initialize);
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
        run.Backend.InitializeCount.Should().Be(1);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("minor")]
    public async Task NumericEnvelopeFieldsCannotBeCoercedFromStrings(string field)
    {
        await using Harness run = await Harness.CreateAsync();
        RendererEnvelope initialize = run.Envelope(RendererCommandTypes.Initialize, Initialize);
        System.Text.Json.Nodes.JsonNode raw = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(initialize, VideoProtocolValidation.Json))!;
        if (field == "generation") raw["generation"] = "1";
        else raw["protocol"]!["minor"] = "0";
        await run.Host.WriteAsync(raw);
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
        run.Backend.InitializeCount.Should().Be(0);
    }

    [Fact]
    public async Task UnknownCommandHasExplicitUnsupportedCode()
    {
        await using Harness run = await Harness.CreateAsync();
        await run.InitializeAsync();
        await run.SendAsync("ExecuteNativeCommand", new EmptyPayload());
        RendererEnvelope failure = await run.ExpectAsync(RendererEventTypes.FatalError);
        failure.Payload.GetProperty("code").GetString().Should().Be(RendererErrorCodes.ProtocolMessageUnsupported);
        await run.ExpectFailureAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCancelsPreparationWithoutFirstFrameAndAcknowledgesOnlyAfterCleanup(bool lateProof)
    {
        await using Harness run = await Harness.CreateAsync();
        run.Backend.BlockFrame = true;
        run.Backend.ReturnLateProof = lateProof;
        await run.InitializeAndAttachAsync();
        await run.SendAsync(RendererCommandTypes.LoadWallpaper, run.Content);
        await run.ExpectAsync(RendererEventTypes.ContentLoaded);
        await run.Backend.FrameEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await run.StopAsync();
        run.Backend.FrameCancelled.Should().BeTrue();
        run.Backend.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task InvalidSubmissionNeverEmitsFirstFrame()
    {
        await using Harness run = await Harness.CreateAsync();
        run.Backend.InvalidSubmission = true;
        await run.InitializeAndAttachAsync();
        await run.SendAsync(RendererCommandTypes.LoadWallpaper, run.Content);
        await run.ExpectAsync(RendererEventTypes.ContentLoaded);
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
        run.Backend.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task StageDeadlineHasExplicitFailureAndDisposesBackend()
    {
        await using Harness run = await Harness.CreateAsync(TimeSpan.FromMilliseconds(120));
        run.Backend.BlockInitialize = true;
        await run.SendAsync(RendererCommandTypes.Initialize, Initialize);
        RendererEnvelope failure = await run.ExpectAsync(RendererEventTypes.FatalError);
        failure.Payload.GetProperty("code").GetString().Should().Be("renderer.video.stage_timeout");
        failure.Payload.GetProperty("details").GetProperty("phase").GetString().Should().Be("Initializing");
        await run.ExpectFailureAsync();
        run.Backend.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task InitializationDeadlineDoesNotWaitForeverForACommand()
    {
        await using Harness run = await Harness.CreateAsync(TimeSpan.FromMilliseconds(120));
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
        run.Backend.InitializeCount.Should().Be(0);
    }

    [Fact]
    public async Task IdleBackendFailureClosesSessionWithoutWaitingForAnotherHostCommand()
    {
        await using Harness run = await Harness.CreateAsync();
        await run.LoadAsync();
        run.Backend.Fail.TrySetException(new IOException("synthetic device loss"));
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
        run.Backend.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task IdleClientEventFailureIsObservedByControlActor()
    {
        await using Harness run = await Harness.CreateAsync();
        await run.LoadAsync();
        run.Backend.HealthFailure = true;
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
        run.Backend.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task CleanupFailureCannotAcknowledgeShutdownOrDisposeTwice()
    {
        await using Harness run = await Harness.CreateAsync();
        await run.InitializeAndAttachAsync();
        run.Backend.CleanupFailure = true;
        await run.SendAsync(RendererCommandTypes.Shutdown, new ShutdownPayload("cleanup-failure"));
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
        run.Backend.DisposeCount.Should().Be(1);
    }

    [Theory]
    [InlineData("Initialize")]
    [InlineData("Attach")]
    [InlineData("Load")]
    public async Task ShutdownPreemptsEveryPreparationOperation(string operation)
    {
        await using Harness run = await Harness.CreateAsync();
        run.Backend.BlockOperation = operation;
        if (operation == "Initialize") await run.SendAsync(RendererCommandTypes.Initialize, Initialize);
        else
        {
            await run.InitializeAsync();
            await run.SendAsync(RendererCommandTypes.AttachSurface, new AttachSurfacePayload(11, 320, 180, 1));
            if (operation == "Load")
            {
                await run.ExpectAsync(RendererEventTypes.SurfaceAttached);
                await run.SendAsync(RendererCommandTypes.LoadWallpaper, run.Content);
            }
        }
        await run.Backend.OperationEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await run.StopAsync();
        run.Backend.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadBeforeAttachDoesNotReachMediaBackend()
    {
        await using Harness run = await Harness.CreateAsync();
        await run.InitializeAsync();
        await run.SendAsync(RendererCommandTypes.LoadWallpaper, run.Content);
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
        run.Backend.LoadCount.Should().Be(0);
    }

    [Fact]
    public async Task UnsupportedCapabilityIsRejectedBeforeNativeInitialization()
    {
        await using Harness run = await Harness.CreateAsync();
        await run.SendAsync(RendererCommandTypes.Initialize, Initialize with { RequestedCapabilities = ["SharedTexture"] });
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
        run.Backend.InitializeCount.Should().Be(0);
    }

    [Theory]
    [InlineData("SetFit")]
    [InlineData("SetVolume")]
    public async Task InvalidPlaybackOptionsAreRejectedBeforeBackend(string command)
    {
        await using Harness run = await Harness.CreateAsync();
        await run.LoadAsync();
        if (command == "SetFit") await run.SendAsync(command, new SetFitPayload("native-command"));
        else await run.SendAsync(command, new SetVolumePayload(2, false));
        await run.ExpectAsync(RendererEventTypes.FatalError);
        await run.ExpectFailureAsync();
    }

    [Theory]
    [InlineData("../escape.avi")]
    [InlineData("content/../motion.avi")]
    [InlineData("content//motion.avi")]
    [InlineData("https://example.test/motion.avi")]
    [InlineData("content/motion.avi:stream")]
    [InlineData("content/motion.avi.")]
    [InlineData("content/missing.avi")]
    [InlineData("content/motion.exe")]
    public async Task ContentPathsFailClosedBeforeBackendLoad(string entryPoint)
    {
        await using Harness run = await Harness.CreateAsync();
        await run.InitializeAndAttachAsync();
        await run.SendAsync(RendererCommandTypes.LoadWallpaper, run.Content with { EntryPoint = entryPoint });
        RendererEnvelope rejection = await run.ExpectAsync(RendererEventTypes.FatalError);
        rejection.Payload.GetProperty("code").GetString().Should().Be(RendererErrorCodes.ContentInvalidPath);
        await run.ExpectFailureAsync();
        run.Backend.LoadCount.Should().Be(0);
    }

    private static InitializePayload Initialize => new("video", ["HwndChild", "DirectComposition"], "zh-CN");

    private sealed class Backend : IVideoSessionBackend
    {
        public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Failure => Fail.Task;
        public bool HealthFailure { get; set; }
        public bool CleanupFailure { get; set; }
        public bool ReturnLateProof { get; set; }
        public bool ReturnLateControl { get; set; }
        public string? BlockOperation { get; set; }
        public TaskCompletionSource OperationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void CheckHealth() { if (HealthFailure) throw new IOException("synthetic media failure"); }
        public TaskCompletionSource FrameEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockFrame { get; set; }
        public bool BlockInitialize { get; set; }
        public bool InvalidSubmission { get; set; }
        public bool FrameCancelled { get; private set; }
        public int InitializeCount { get; private set; }
        public int LoadCount { get; private set; }
        public int DisposeCount { get; private set; }
        public string Playback { get; private set; } = "Playing";
        public List<VideoFrameSubmission> Submissions { get; } = [];
        public async Task InitializeAsync(CancellationToken token)
        { InitializeCount++; await BlockAsync("Initialize", token); if (BlockInitialize) await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        public async Task<ulong> AttachAsync(AttachSurfacePayload surface, CancellationToken token)
        { await BlockAsync("Attach", token); return surface.WindowHandle + 1; }
        public async Task LoadAsync(string mediaPath, CancellationToken token) { LoadCount++; await BlockAsync("Load", token); }
        private async Task BlockAsync(string operation, CancellationToken token)
        {
            if (BlockOperation == operation) { OperationEntered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        }
        public async Task<VideoFrameSubmission> SubmitAsync(VideoFrameTicket ticket, CancellationToken token)
        {
            FrameEntered.TrySetResult();
            try { if (BlockFrame) await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { FrameCancelled = true; if (!ReturnLateProof) throw; }
            VideoFrameSubmission proof = new(ticket, Submissions.Count + 1, ticket.Width, ticket.Height,
                true, true, 0, 0, InvalidSubmission ? 1 : 0, 0);
            Submissions.Add(proof); return proof;
        }
        public Task<VideoFrameSubmission> BoundsAsync(VideoFrameTicket ticket, CancellationToken token) =>
            Task.FromResult(new VideoFrameSubmission(ticket, Submissions[^1].FrameSequence, ticket.Width, ticket.Height,
                true, true, null, null, 0, null, true));
        public async Task PlaybackAsync(string state, CancellationToken token)
        {
            try { await BlockAsync("Playback", token); }
            catch (OperationCanceledException) when (ReturnLateControl) { }
            Playback = state;
        }
        public Task FitAsync(string fit, CancellationToken token) => Task.CompletedTask;
        public Task VolumeAsync(double volume, bool muted, CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (CleanupFailure) throw new IOException("synthetic cleanup failure");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly NamedPipeServerStream server;
        private readonly NamedPipeClientStream client;
        private readonly LengthPrefixedJsonChannel renderer;
        private readonly string root;
        private readonly string sessionId = Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        public Backend Backend { get; } = new();
        public LengthPrefixedJsonChannel Host { get; }
        public Task Run { get; private set; } = Task.CompletedTask;
        public LoadWallpaperPayload Content => new("motion", "Video", root, "content/motion.avi", new Dictionary<string, JsonElement>());
        private Harness(NamedPipeServerStream server, NamedPipeClientStream client)
        {
            this.server = server; this.client = client;
            Host = new(server, leaveOpen: true); renderer = new(client, VideoProtocolValidation.Json, leaveOpen: true);
            root = Path.Combine(Path.GetTempPath(), "LiveWall.Video.Session.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "content"));
            File.WriteAllBytes(Path.Combine(root, "content", "motion.avi"), [1]);
        }
        public static async Task<Harness> CreateAsync(TimeSpan? budget = null)
        {
            string name = @"LOCAL\livewall.videotest." + Guid.NewGuid().ToString("N");
            NamedPipeServerStream server = LocalNamedPipeFactory.CreateServer(name, 1, true);
            NamedPipeClientStream client = LocalNamedPipeFactory.CreateClient(name);
            Task connected = server.WaitForConnectionAsync();
            await client.ConnectAsync(3000); await connected;
            Harness harness = new(server, client);
            harness.Run = new VideoRendererSession(harness.sessionId, harness.Backend, budget)
                .RunAsync(harness.renderer, "test-only-credential", harness.lifetime.Token);
            await harness.ExpectAsync(RendererEventTypes.Hello);
            return harness;
        }
        public RendererEnvelope Envelope<T>(string type, T payload, long generation = 1) =>
            VideoProtocolValidation.Event(sessionId, generation, type, payload);
        public async Task SendAsync<T>(string type, T payload, long generation = 1) =>
            await Host.WriteAsync(Envelope(type, payload, generation), lifetime.Token);
        public async Task<RendererEnvelope> ExpectAsync(string type)
        {
            RendererEnvelope message = await Host.ReadAsync<RendererEnvelope>(lifetime.Token);
            message.Type.Should().Be(type); return message;
        }
        public async Task InitializeAsync() { await SendAsync(RendererCommandTypes.Initialize, Initialize); await ExpectAsync(RendererEventTypes.Initialized); }
        public async Task InitializeAndAttachAsync()
        {
            await InitializeAsync();
            await SendAsync(RendererCommandTypes.AttachSurface, new AttachSurfacePayload(11, 320, 180, 1));
            await ExpectAsync(RendererEventTypes.SurfaceAttached);
        }
        public async Task LoadAsync()
        {
            await InitializeAndAttachAsync(); await SendAsync(RendererCommandTypes.LoadWallpaper, Content);
            await ExpectAsync(RendererEventTypes.ContentLoaded); await ExpectAsync(RendererEventTypes.FirstFramePresented);
        }
        public async Task StopAsync(long generation = 1)
        {
            await SendAsync(RendererCommandTypes.Shutdown, new ShutdownPayload("test-complete"), generation);
            await ExpectAsync(RendererEventTypes.ShutdownCompleted);
            Backend.DisposeCount.Should().Be(1);
            await Run.WaitAsync(lifetime.Token);
        }
        public async Task ExpectFailureAsync()
        {
            Func<Task> action = () => Run.WaitAsync(lifetime.Token);
            await action.Should().ThrowAsync<Exception>();
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync(); await client.DisposeAsync(); await server.DisposeAsync();
            try { await Run.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            await renderer.DisposeAsync(); await Host.DisposeAsync(); lifetime.Dispose();
            Directory.Delete(root, true);
        }
    }
}
