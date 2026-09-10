using FluentAssertions;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.Orchestration;

namespace LiveWall.Application.Tests;

public sealed class RendererProtocolStateMachineTests
{
    private static readonly DisplayBounds Bounds = new(0, 0, 1920, 1080);

    [Fact]
    public void InitialLoadAndRecoveryRequireTheFormalEventOrder()
    {
        RendererProtocolStateMachine machine = new(1);

        machine.ValidateAndApply(new AttachRendererSurface(1, 42, Bounds, 1));
        machine.ValidateAndApply(new RendererSurfaceAttached(1));
        machine.ValidateAndApply(new LoadRendererContent(1, CreateWallpaper(), "C:\\content"));
        machine.ValidateAndApply(new RendererContentLoaded(1));
        machine.ValidateAndApply(new RendererFirstFramePresented(1));
        machine.ValidateAndApply(new SetRendererBounds(2, Bounds, 1));
        machine.ValidateAndApply(new AttachRendererSurface(2, 43, Bounds, 1));
        machine.ValidateAndApply(new RendererSurfaceAttached(2));
        machine.ValidateAndApply(new RendererFirstFramePresented(2));

        machine.Generation.Should().Be(2);
    }

    [Fact]
    public void LoadBeforeSurfaceAttachedIsRejected()
    {
        RendererProtocolStateMachine machine = new(1);

        Action act = () => machine.ValidateAndApply(
            new LoadRendererContent(1, CreateWallpaper(), "C:\\content"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*LoadRendererContent*Initialized*");
    }

    [Fact]
    public void FirstFrameBeforeContentLoadedIsRejected()
    {
        RendererProtocolStateMachine machine = new(1);
        machine.ValidateAndApply(new AttachRendererSurface(1, 42, Bounds, 1));
        machine.ValidateAndApply(new RendererSurfaceAttached(1));

        Action act = () => machine.ValidateAndApply(new RendererFirstFramePresented(1));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*FirstFramePresented*SurfaceReady*");
    }

    [Fact]
    public void DuplicateAndBackwardRecoveryGenerationsAreRejected()
    {
        RendererProtocolStateMachine machine = CreateRunningMachine();

        Action duplicate = () => machine.ValidateAndApply(
            new AttachRendererSurface(1, 43, Bounds, 1));

        duplicate.Should().Throw<InvalidOperationException>();
        machine.ValidateAndApply(new AttachRendererSurface(2, 44, Bounds, 1));
        machine.ValidateAndApply(new RendererSurfaceAttached(2));
        machine.ValidateAndApply(new RendererFirstFramePresented(2));
        Action backward = () => machine.ValidateAndApply(
            new AttachRendererSurface(1, 45, Bounds, 1));
        backward.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WrongGenerationEventIsRejectedInsteadOfIgnored()
    {
        RendererProtocolStateMachine machine = new(3);
        machine.ValidateAndApply(new AttachRendererSurface(3, 42, Bounds, 1));

        Action act = () => machine.ValidateAndApply(new RendererSurfaceAttached(2));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*generation 2*generation 3*");
    }

    private static RendererProtocolStateMachine CreateRunningMachine()
    {
        RendererProtocolStateMachine machine = new(1);
        machine.ValidateAndApply(new AttachRendererSurface(1, 42, Bounds, 1));
        machine.ValidateAndApply(new RendererSurfaceAttached(1));
        machine.ValidateAndApply(new LoadRendererContent(1, CreateWallpaper(), "C:\\content"));
        machine.ValidateAndApply(new RendererContentLoaded(1));
        machine.ValidateAndApply(new RendererFirstFramePresented(1));
        return machine;
    }

    private static WallpaperDefinition CreateWallpaper() => new(
        new WallpaperId("test.wallpaper"),
        "1.0.0",
        WallpaperKind.Video,
        "video.mp4",
        new WallpaperMetadata("Test", null, null, null, new HashSet<string>()),
        new WallpaperCapabilities(false, false, false, false, 30),
        WallpaperOrigin.Native);
}
