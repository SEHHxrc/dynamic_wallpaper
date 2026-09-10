using System.Globalization;
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

    Task AbandonSurfaceAsync(SurfaceId surfaceId, CancellationToken cancellationToken);
}

public interface IDisplayTopologySource
{
    DisplayTopology Current { get; }

    event EventHandler<DisplayTopologyChangedEventArgs> Changed;
}

public readonly record struct SurfaceId(string Value);

public sealed record DesktopTopology(
    long Revision,
    IReadOnlyList<DesktopAttachmentCapability> Attachments);

public sealed record DesktopAttachmentCapability(
    string AdapterId,
    string PresentationKind,
    string RendererBinding);

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

public sealed record WindowsShellSnapshot(
    string Version,
    string Build,
    bool RaisedDesktopEnabled,
    int? UpdateBuildRevision = null)
{
    public bool HasCompleteBuildIdentity => UpdateBuildRevision is not null;

    public string FullBuild => UpdateBuildRevision is null
        ? Build
        : $"{Build}.{UpdateBuildRevision.Value.ToString(CultureInfo.InvariantCulture)}";
}

public sealed class DisplayTopologyChangedEventArgs(DisplayTopology previous, DisplayTopology current)
    : EventArgs
{
    public DisplayTopology Previous { get; } = previous;

    public DisplayTopology Current { get; } = current;
}
