using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using FluentAssertions;
using LiveWall.Application.Library;
using LiveWall.Application.Sessions;
using LiveWall.DesktopHostDiagnostics;
using LiveWall.Diagnostics;
using LiveWall.Domain.Compatibility;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.Orchestration;
using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.Application.Tests;

public sealed class VideoProductCandidateTests
{
    [Fact]
    public void ControlAcceptanceRequiresConfirmationAndHasFourFixedStages()
    {
        var options = VideoCandidateOptions.Parse([.. Options, "--control-recovery"]);
        options.ControlRecovery.Should().BeTrue();
        options.DurationSeconds.Should().Be(20);
        Action missing = () => VideoCandidateOptions.Parse([.. Options.Where(value => value != "--confirm-desktop-presentation"), "--control-recovery"]);
        missing.Should().Throw<ArgumentException>();
        VideoCandidateOptions.Parse([.. Options.Where(value => value != "--confirm-desktop-presentation"), "--control-recovery", "--preflight-only"]).PreflightOnly.Should().BeTrue();
    }

    [Theory]
    [InlineData("10")]
    [InlineData("30")]
    public void ControlAcceptanceCannotSilentlyChangeItsObservationSchedule(string seconds)
    {
        Action parse = () => VideoCandidateOptions.Parse([.. Options, "--control-recovery", "--duration-seconds", seconds]);
        parse.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, true, false, true, true)]
    [InlineData(-1, true, false, true, false)]
    [InlineData(null, true, false, false, false)]
    [InlineData(0, false, false, true, false)]
    [InlineData(0, true, true, false, false)]
    public void CleanupDoesNotConfuseProcessExitWithGracefulRetirement(
        int? exitCode, bool shutdownCompleted, bool windowsRemain, bool cleaned, bool graceful)
    {
        var assessment = VideoCandidateCleanupAssessment.Evaluate(true, shutdownCompleted, windowsRemain, 1, [exitCode]);
        assessment.OwnedResourcesCleaned.Should().Be(cleaned);
        assessment.GracefulRetirement.Should().Be(graceful);
    }

    [Fact]
    public void CleanupCannotPassWithMissingRendererExitEvidenceOrUndisposedResources()
    {
        VideoCandidateCleanupAssessment.Evaluate(true, true, false, 2, [0]).OwnedResourcesCleaned.Should().BeFalse();
        VideoCandidateCleanupAssessment.Evaluate(false, true, false, 1, [0]).OwnedResourcesCleaned.Should().BeFalse();
        VideoCandidateCleanupAssessment.Evaluate(true, true, false, 0, []).GracefulRetirement.Should().BeFalse();
    }

    private static string[] Options => [VideoCandidateOptions.Mode, "--confirm-desktop-presentation",
        "--renderer-executable", @"E:\repo\LiveWall.Renderer.Video.exe", "--content-root", @"E:\repo\fixture",
        "--libmpv", @"E:\repo\libmpv-2.dll", "--result-json", @"E:\repo\fresh.json"];

    [Fact]
    public void ExplicitConfirmationAndReadOnlyPreflightHaveSeparateGates()
    {
        Action missing = () => VideoCandidateOptions.Parse(Options.Where(value => value != "--confirm-desktop-presentation").ToArray());
        missing.Should().Throw<ArgumentException>().WithMessage("*requires --confirm-desktop-presentation*");
        var preflight = VideoCandidateOptions.Parse([.. Options.Where(value => value != "--confirm-desktop-presentation"), "--preflight-only"]);
        preflight.PreflightOnly.Should().BeTrue();
        VideoCandidateOptions.Parse(Options).DurationSeconds.Should().Be(10);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("31")]
    [InlineData("not-a-number")]
    public void DurationNeverWidensTheExistingThirtySecondLimit(string duration)
    {
        Action parse = () => VideoCandidateOptions.Parse([.. Options, "--duration-seconds", duration]);
        parse.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--target-display")]
    [InlineData("--confirm-desktop-presentation")]
    [InlineData("--renderer-executable")]
    public void UnknownExpandedOrDuplicateFlagsAreRejected(string flag)
    {
        Action parse = () => VideoCandidateOptions.Parse([.. Options, flag, "all"]);
        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NativeFixtureMapsToVideoWithNoExtraCapabilities()
    {
        WallpaperDefinition definition = VideoProductCandidateAssets.ReadFixtureDefinition(Manifest());
        definition.Kind.Should().Be(WallpaperKind.Video);
        definition.EntryPoint.Should().Be("content/motion.avi");
        definition.Capabilities.RequiresNetwork.Should().BeFalse();
        definition.Capabilities.RequiresAudioCapture.Should().BeFalse();
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("type")]
    [InlineData("id")]
    [InlineData("fit")]
    [InlineData("muted")]
    [InlineData("network")]
    public void FixtureRejectsUnapprovedContentAndPermissions(string field)
    {
        JsonNode node = JsonNode.Parse(Manifest().GetRawText())!;
        if (field == "fit") node["defaults"]!["fit"] = "contain";
        else if (field == "muted") node["defaults"]!["muted"] = false;
        else if (field == "network") node["permissions"]!["network"] = true;
        else node[field] = field == "entry" ? "../escape.avi" : "other";
        Action read = () => VideoProductCandidateAssets.ReadFixtureDefinition(JsonSerializer.SerializeToElement(node));
        read.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("new", ShellMutationRecovery.CleanupIncomplete)]
    [InlineData("existing", ShellMutationRecovery.NotRequired)]
    [InlineData("recovered", ShellMutationRecovery.Recovered)]
    [InlineData("reused", ShellMutationRecovery.Inconclusive)]
    [InlineData("generation", ShellMutationRecovery.Inconclusive)]
    [InlineData("unavailable", ShellMutationRecovery.Inconclusive)]
    public void MutationRecoveryNeverEquatesOwnedCleanupWithShellRestoration(string scenario, object expected)
    {
        ShellTopologySnapshot before = Snapshot(scenario is "existing" or "reused");
        ShellTopologySnapshot requested = Snapshot(true);
        ShellTopologySnapshot after = Snapshot(scenario is "new" or "existing" or "reused");
        if (scenario == "reused") after = after with { ShellDescendants = [after.ShellDescendants[0] with { ProcessId = 99 }] };
        if (scenario == "generation") after = after with { ShellWindowHandle = 999 };
        if (scenario == "unavailable") after = after with { Availability = ShellTopologyAvailability.ShellUnavailable };
        ShellMutationAssessment.Evaluate(before, requested, after, true).Recovery.Should().Be((ShellMutationRecovery)expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Attach")]
    [InlineData("Content")]
    [InlineData("FirstFrame")]
    public async Task ProductCoreCommitsOnlyAfterEveryRequiredEventAndCleansAllResources(string? suppressed)
    {
        Desktop desktop = new();
        Provider provider = new(desktop, suppressed);
        bool committed = false;
        RendererProductCandidateRequest request = Request() with { FirstFrameCommitted = _ =>
            { committed = true; desktop.ReplacementCount.Should().Be(1); } };
        if (suppressed is null)
        {
            RendererProductCandidateResult result = await RendererProductCandidateDiagnostics.RunWithDesktopAsync(
                request, Entry(), provider, desktop, CancellationToken.None);
            result.FirstFrameCommitted.Should().BeTrue();
            result.Timeline.Count(item => item.Phase == ApplyGenerationPhase.SurfacesReplaced && item.Outcome == ApplyStageOutcome.Success).Should().Be(1);
            committed.Should().BeTrue();
        }
        else
        {
            Func<Task> run = () => RendererProductCandidateDiagnostics.RunWithDesktopAsync(request, Entry(), provider, desktop, CancellationToken.None);
            await run.Should().ThrowAsync<RendererProductCandidateFailureException>();
            desktop.ReplacementCount.Should().Be(0);
            committed.Should().BeFalse();
        }
        desktop.Destroyed.Should().BeTrue(); provider.Renderer!.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task CancellationAfterCommitStillRetiresTheActiveRendererAndSurface()
    {
        using CancellationTokenSource cancel = new();
        Desktop desktop = new(); Provider provider = new(desktop, null);
        RendererProductCandidateRequest request = Request() with { FirstFrameCommitted = _ => cancel.Cancel() };
        Func<Task> run = () => RendererProductCandidateDiagnostics.RunWithDesktopAsync(request, Entry(), provider, desktop, cancel.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        desktop.ReplacementCount.Should().Be(1); desktop.Destroyed.Should().BeTrue(); provider.Renderer!.Disposed.Should().BeTrue();
    }

    private static JsonElement Manifest() => JsonSerializer.SerializeToElement(new { schemaVersion = 1,
        id = "livewall.diagnostic.video-c0", version = "1.0.0", type = "video", entry = "content/motion.avi",
        defaults = new { fit = "cover", muted = true }, permissions = new { network = false, audioCapture = false, pointerInput = false, systemMetrics = false } });
    private static WallpaperLibraryEntry Entry() => new(VideoProductCandidateAssets.ReadFixtureDefinition(Manifest()),
        Path.GetTempPath(), new CompatibilityReport(CompatibilityGrade.Native, []));
    private static RendererProductCandidateRequest Request() => new("unused-test-renderer", new("test", "0", false),
        new(1, [new(new DisplayId("primary"), "test", new(0, 0, 320, 180), 1, 60, true)]),
        TimeSpan.FromSeconds(1), RendererStageTimeout: TimeSpan.FromMilliseconds(80));
    private static ShellTopologySnapshot Snapshot(bool worker) => new(new("test", "26200", 9168, "25H2", null),
        new(1, "WinSta0", "Default"), ShellTopologyAvailability.Available, 1, [Window(1, "Progman", 0)],
        worker ? [Window(2, "WorkerW", 1)] : []);
    private static ShellWindowFingerprint Window(ulong id, string name, ulong parent) => new(id,
        parent == 0 ? ShellWindowScope.TopLevel : ShellWindowScope.ShellDescendant, name, 10, "explorer", 1,
        parent == 0 ? 0 : 1, 0, true, new(0, 0, 320, 180), parent, 0, 0, 0, 0, 0, 0, 0);

    private sealed class Desktop : IDesktopHost
    {
        public int ReplacementCount { get; private set; }
        public bool Destroyed { get; private set; }
        public Task<DesktopTopology> EnsureTopologyAsync(DisplayTopology topology, CancellationToken token) => Task.FromResult(new DesktopTopology(1, []));
        public Task<DesktopSurface> CreateSurfaceAsync(SurfaceRequest request, CancellationToken token) => Task.FromResult(new DesktopSurface(new("test"), request.DisplayIds, 1));
        public Task ResizeSurfaceAsync(SurfaceId id, SurfaceRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task ReplaceSurfacesAsync(SurfaceReplacement replacement, CancellationToken token) { ReplacementCount++; return Task.CompletedTask; }
        public Task DestroySurfaceAsync(SurfaceId id, CancellationToken token) { Destroyed = true; return Task.CompletedTask; }
        public Task AbandonSurfaceAsync(SurfaceId id, CancellationToken token) => DestroySurfaceAsync(id, token);
    }
    private sealed class Provider(Desktop desktop, string? suppressed) : IRendererProvider
    {
        public RendererDescriptor Descriptor { get; } = new(new("video"), "Test Video", new HashSet<WallpaperKind> { WallpaperKind.Video }, new HashSet<string>());
        public Renderer? Renderer { get; private set; }
        public RendererMatch Match(WallpaperDefinition definition, RuntimeEnvironment environment) => new(definition.Kind == WallpaperKind.Video, 100, "test");
        public Task<IRendererSession> CreateAsync(RendererLaunchContext context, CancellationToken token)
        { Renderer = new(context.SessionId, desktop, suppressed); return Task.FromResult<IRendererSession>(Renderer); }
    }
    private sealed class Renderer(SessionId id, Desktop desktop, string? suppressed) : IRendererSession
    {
        private readonly Channel<RendererEvent> events = Channel.CreateUnbounded<RendererEvent>();
        public SessionId Id => id;
        public bool Disposed { get; private set; }
        public IAsyncEnumerable<RendererEvent> ReadEventsAsync(CancellationToken token) => events.Reader.ReadAllAsync(token);
        public Task SendAsync(RendererCommand command, CancellationToken token)
        {
            if (command is AttachRendererSurface && suppressed != "Attach") events.Writer.TryWrite(new RendererSurfaceAttached(command.Generation));
            if (command is LoadRendererContent load)
            {
                desktop.ReplacementCount.Should().Be(0);
                load.Wallpaper.Kind.Should().Be(WallpaperKind.Video);
                if (suppressed != "Content") events.Writer.TryWrite(new RendererContentLoaded(command.Generation));
                if (suppressed is not ("Content" or "FirstFrame")) events.Writer.TryWrite(new RendererFirstFramePresented(command.Generation));
            }
            if (command is ShutdownRenderer) events.Writer.TryWrite(new RendererShutdownCompleted(command.Generation));
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
