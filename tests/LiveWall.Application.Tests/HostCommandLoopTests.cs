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
                runtime.Desktop.DestroyedSurfaces.Contains(new SurfaceId("surface-1")),
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
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 500; attempt++)
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
        public TestRuntime(params bool[] autoPresentFirstFrame)
        {
            Provider = new FakeRendererProvider(autoPresentFirstFrame);
        }

        public FakeRendererProvider Provider { get; }

        public FakeDesktopHost Desktop { get; } = new();

        public HostCommandLoop CreateCommandLoop()
        {
            SessionCoordinator coordinator = new(
                new FakeWallpaperRepository(),
                new DefaultLayoutPlanner(),
                new RendererProviderSelector([Provider]),
                Desktop,
                new RuntimeEnvironment("win-x64", true, new HashSet<string>()));
            return new HostCommandLoop(coordinator, new DefaultPlaybackPolicyEvaluator());
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
        private readonly long generation;
        private readonly bool autoPresentFirstFrame;
        private int attachCount;

        public FakeRendererSession(
            SessionId id,
            long generation,
            bool autoPresentFirstFrame)
        {
            Id = id;
            this.generation = generation;
            this.autoPresentFirstFrame = autoPresentFirstFrame;
        }

        public SessionId Id { get; }

        public ConcurrentQueue<RendererCommand> Commands { get; } = new();

        public bool IsDisposed { get; private set; }

        public IAsyncEnumerable<RendererEvent> ReadEventsAsync(CancellationToken cancellationToken) =>
            events.Reader.ReadAllAsync(cancellationToken);

        public Task SendAsync(RendererCommand command, CancellationToken cancellationToken)
        {
            Commands.Enqueue(command);
            if (command is AttachRendererSurface)
            {
                attachCount++;
                events.Writer.TryWrite(new RendererSurfaceAttached(generation));
                if (autoPresentFirstFrame && attachCount > 1)
                {
                    PresentFirstFrame();
                }
            }

            if (autoPresentFirstFrame && command is LoadRendererContent)
            {
                PresentFirstFrame();
            }

            return Task.CompletedTask;
        }

        public void PresentFirstFrame() =>
            events.Writer.TryWrite(new RendererFirstFramePresented(generation));

        public void FailBeforeFirstFrame() =>
            events.Writer.TryWrite(new RendererFailed(
                generation,
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
    }
}
