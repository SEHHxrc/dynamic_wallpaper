using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;

namespace LiveWall.Application.Sessions;

public interface IDesktopHost
{
    Task<DesktopTopology> EnsureTopologyAsync(
        DisplayTopology displays,
        CancellationToken cancellationToken);

    Task<DesktopSurface> CreateSurfaceAsync(
        SurfaceRequest request,
        CancellationToken cancellationToken);

    Task ReplaceSurfacesAsync(
        SurfaceReplacement replacement,
        CancellationToken cancellationToken);

    Task DestroySurfaceAsync(SurfaceId surfaceId, CancellationToken cancellationToken);
}

public interface IDesktopHostAdapter
{
    bool IsSupported(WindowsShellSnapshot snapshot);

    Task<DesktopAttachPoint> DiscoverAsync(CancellationToken cancellationToken);

    Task RecoverAsync(CancellationToken cancellationToken);
}

public interface IDisplayTopologySource
{
    DisplayTopology Current { get; }

    event EventHandler<DisplayTopologyChangedEventArgs> Changed;
}

public readonly record struct SurfaceId(string Value);

public sealed record DesktopTopology(long Revision, IReadOnlyList<DesktopAttachPoint> AttachPoints);

public sealed record DesktopAttachPoint(ulong WindowHandle, string AdapterId);

public sealed record SurfaceRequest(
    IReadOnlyList<DisplayId> DisplayIds,
    DisplayBounds Bounds,
    FitMode FitMode);

public sealed record SurfaceReplacement(
    IReadOnlyList<SurfaceId> ProvisionalSurfaceIds,
    IReadOnlyList<SurfaceId> ReplacedSurfaceIds,
    long DesktopTopologyRevision);

public sealed record DesktopSurface(
    SurfaceId Id,
    IReadOnlyList<DisplayId> DisplayIds,
    ulong WindowHandle);

public sealed record WindowsShellSnapshot(string Version, string Build, bool RaisedDesktopEnabled);

public sealed class DisplayTopologyChangedEventArgs(DisplayTopology previous, DisplayTopology current)
    : EventArgs
{
    public DisplayTopology Previous { get; } = previous;

    public DisplayTopology Current { get; } = current;
}
