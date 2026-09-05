using LiveWall.Domain.Displays;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Domain.Layouts;

public sealed record WallpaperAssignment(
    DisplayId DisplayId,
    WallpaperId WallpaperId,
    LayoutMode LayoutMode,
    FitMode FitMode,
    PropertyPresetId? PresetId);

public sealed record LayoutPlan(
    long TopologyRevision,
    IReadOnlyList<PlannedSurface> Surfaces);

public sealed record PlannedSurface(
    string PlanId,
    IReadOnlyList<DisplayId> DisplayIds,
    WallpaperId WallpaperId,
    DisplayBounds Bounds,
    LayoutMode LayoutMode,
    FitMode FitMode,
    PropertyPresetId? PresetId);

public enum LayoutMode
{
    PerDisplay,
    Duplicate,
    Span,
}

public enum FitMode
{
    Cover,
    Contain,
    Stretch,
    Center,
}
