using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Playback;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Application.Sessions;

public interface IRendererProvider
{
    RendererDescriptor Descriptor { get; }

    RendererMatch Match(WallpaperDefinition wallpaper, RuntimeEnvironment environment);

    Task<IRendererSession> CreateAsync(
        RendererLaunchContext context,
        CancellationToken cancellationToken);
}

public interface IRendererSession : IAsyncDisposable
{
    SessionId Id { get; }

    IAsyncEnumerable<RendererEvent> ReadEventsAsync(CancellationToken cancellationToken);

    Task SendAsync(RendererCommand command, CancellationToken cancellationToken);
}

public sealed record RendererDescriptor(
    RendererId Id,
    string Name,
    IReadOnlySet<WallpaperKind> SupportedKinds,
    IReadOnlySet<string> Capabilities);

public sealed record RendererMatch(bool IsMatch, int Priority, string Reason);

public sealed record RuntimeEnvironment(
    string RuntimeIdentifier,
    bool HardwareAccelerationAvailable,
    IReadOnlySet<string> AvailableFeatures);

public sealed record RendererLaunchContext(
    SessionId SessionId,
    long Generation,
    WallpaperDefinition Wallpaper,
    string CanonicalContentRoot);

public abstract record RendererCommand(long Generation);

public sealed record AttachRendererSurface(
    long Generation,
    ulong SurfaceWindowHandle,
    DisplayBounds Bounds,
    double ScaleFactor) : RendererCommand(Generation);

public sealed record LoadRendererContent(
    long Generation,
    WallpaperDefinition Wallpaper,
    string CanonicalContentRoot)
    : RendererCommand(Generation);

public sealed record ChangeRendererState(long Generation, PlaybackState TargetState) : RendererCommand(Generation);

public sealed record SetRendererBounds(
    long Generation,
    DisplayBounds Bounds,
    double ScaleFactor) : RendererCommand(Generation);

public sealed record SetRendererFit(long Generation, FitMode FitMode) : RendererCommand(Generation);

public sealed record SetRendererVolume(long Generation, double Volume, bool Muted) : RendererCommand(Generation);

public sealed record ThrottleRenderer(
    long Generation,
    int FramesPerSecond,
    PlaybackQuality Quality) : RendererCommand(Generation);

public sealed record ShutdownRenderer(long Generation, string Reason) : RendererCommand(Generation);

public abstract record RendererEvent(long Generation);

public sealed record RendererInitialized(long Generation) : RendererEvent(Generation);

public sealed record RendererSurfaceAttached(long Generation) : RendererEvent(Generation);

public sealed record RendererContentLoaded(long Generation) : RendererEvent(Generation);

public sealed record RendererFirstFramePresented(long Generation) : RendererEvent(Generation);

public sealed record RendererPlaybackStateChanged(long Generation, PlaybackState State)
    : RendererEvent(Generation);

public sealed record RendererFailed(long Generation, string ErrorCode, string Message, bool Recoverable)
    : RendererEvent(Generation);

public sealed record RendererShutdownCompleted(long Generation) : RendererEvent(Generation);
