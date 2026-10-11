using LiveWall.Application.Layouts;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Host.Orchestration;

public enum DisplayReconciliationKind
{
    NoChange,
    Reconfigure,
    Suspend,
}

internal enum DisplayTopologyChangeReason
{
    DisplayAdded,
    DisplayRemoved,
    BoundsChanged,
    ScaleChanged,
    PrimaryChanged,
    RefreshRateChanged,
    VirtualDesktopOriginChanged,
}

internal sealed record ActiveSurfaceState(
    SessionId SessionId,
    WallpaperId WallpaperId,
    IReadOnlyList<DisplayId> DisplayIds,
    DisplayBounds Bounds,
    FitMode FitMode);

internal sealed record ActiveSurfaceResize(
    SessionId SessionId,
    PlannedSurface Target);

internal sealed record DisplayReconciliationPlan(
    long PreviousTopologyRevision,
    long CurrentTopologyRevision,
    DisplayReconciliationKind Kind,
    IReadOnlyList<DisplayTopologyChangeReason> Reasons,
    IReadOnlyList<WallpaperAssignment> ConnectedAssignments,
    IReadOnlyList<WallpaperAssignment> OfflineAssignments,
    LayoutPlan DesiredLayout,
    IReadOnlyList<SessionId> PreservedSessionIds,
    IReadOnlyList<SessionId> RetiredSessionIds,
    IReadOnlyList<PlannedSurface> SurfacesToPrepare,
    IReadOnlyList<ActiveSurfaceResize> ActiveSurfaceResizes,
    IReadOnlyList<DisplayId> AffectedDisplayIds)
{
    public bool RequiresAtomicReplace => SurfacesToPrepare.Count > 0;
}

internal sealed class DisplayReconciliationPlanner(ILayoutPlanner layoutPlanner)
{
    private readonly ILayoutPlanner layoutPlanner = layoutPlanner ??
        throw new ArgumentNullException(nameof(layoutPlanner));

    public DisplayReconciliationPlan CreatePlan(
        DisplayTopology previous,
        DisplayTopology current,
        IReadOnlyList<WallpaperAssignment> desiredAssignments,
        IReadOnlyList<ActiveSurfaceState> activeSessions)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desiredAssignments);
        ArgumentNullException.ThrowIfNull(activeSessions);
        if (current.Revision <= previous.Revision)
        {
            throw new ArgumentException(
                "Display reconciliation requires a strictly newer topology revision.",
                nameof(current));
        }

        Dictionary<DisplayId, DisplayDescriptor> previousDisplays = previous.Displays
            .ToDictionary(display => display.Id);
        Dictionary<DisplayId, DisplayDescriptor> currentDisplays = current.Displays
            .ToDictionary(display => display.Id);
        DisplayTopologyChangeReason[] reasons = GetReasons(previousDisplays, currentDisplays);
        bool virtualDesktopOriginChanged =
            previous.Displays.Count > 0 && current.Displays.Count > 0 &&
            GetVirtualDesktopOrigin(previous.Displays) !=
                GetVirtualDesktopOrigin(current.Displays);
        WallpaperAssignment[] connectedAssignments = desiredAssignments
            .Where(assignment => currentDisplays.ContainsKey(assignment.DisplayId))
            .OrderBy(assignment => assignment.DisplayId.Value, StringComparer.Ordinal)
            .ToArray();
        WallpaperAssignment[] offlineAssignments = desiredAssignments
            .Where(assignment => !currentDisplays.ContainsKey(assignment.DisplayId))
            .OrderBy(assignment => assignment.DisplayId.Value, StringComparer.Ordinal)
            .ToArray();
        LayoutPlan desiredLayout = layoutPlanner.CreatePlan(current, connectedAssignments);
        HashSet<DisplayId> scaleChanged = currentDisplays
            .Where(item => previousDisplays.TryGetValue(item.Key, out DisplayDescriptor? old) &&
                old.ScaleFactor != item.Value.ScaleFactor)
            .Select(item => item.Key)
            .ToHashSet();

        List<SessionId> preserved = [];
        List<PlannedSurface> prepare = [];
        HashSet<SessionId> matchedSessions = [];
        foreach (PlannedSurface surface in desiredLayout.Surfaces)
        {
            ActiveSurfaceState? match = activeSessions.FirstOrDefault(active =>
                !matchedSessions.Contains(active.SessionId) &&
                active.WallpaperId == surface.WallpaperId &&
                active.FitMode == surface.FitMode &&
                active.Bounds == surface.Bounds &&
                SameDisplays(active.DisplayIds, surface.DisplayIds) &&
                !virtualDesktopOriginChanged &&
                !surface.DisplayIds.Any(scaleChanged.Contains));
            if (match is null)
            {
                prepare.Add(surface);
                continue;
            }

            matchedSessions.Add(match.SessionId);
            preserved.Add(match.SessionId);
        }

        SessionId[] retired = activeSessions
            .Where(active => !matchedSessions.Contains(active.SessionId))
            .Select(active => active.SessionId)
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .ToArray();
        HashSet<SessionId> resizeMatches = [];
        ActiveSurfaceResize[] activeSurfaceResizes = prepare
            .Select(surface =>
            {
                ActiveSurfaceState? candidate = activeSessions.FirstOrDefault(active =>
                    !matchedSessions.Contains(active.SessionId) &&
                    !resizeMatches.Contains(active.SessionId) &&
                    active.WallpaperId == surface.WallpaperId &&
                    active.FitMode == surface.FitMode &&
                    SameDisplays(active.DisplayIds, surface.DisplayIds));
                if (candidate is null)
                {
                    return null;
                }
                resizeMatches.Add(candidate.SessionId);
                return new ActiveSurfaceResize(candidate.SessionId, surface);
            })
            .Where(resize => resize is not null)
            .Cast<ActiveSurfaceResize>()
            .ToArray();
        DisplayId[] affectedDisplays = prepare
            .SelectMany(surface => surface.DisplayIds)
            .Concat(activeSessions
                .Where(active => retired.Contains(active.SessionId))
                .SelectMany(active => active.DisplayIds))
            .Distinct()
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .ToArray();
        DisplayReconciliationKind kind = prepare.Count == 0 && retired.Length == 0
            ? DisplayReconciliationKind.NoChange
            : current.Displays.Count == 0
                ? DisplayReconciliationKind.Suspend
                : DisplayReconciliationKind.Reconfigure;

        return new DisplayReconciliationPlan(
            previous.Revision,
            current.Revision,
            kind,
            Array.AsReadOnly(reasons),
            Array.AsReadOnly(connectedAssignments),
            Array.AsReadOnly(offlineAssignments),
            desiredLayout,
            Array.AsReadOnly(preserved.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(retired),
            Array.AsReadOnly(prepare.ToArray()),
            Array.AsReadOnly(activeSurfaceResizes),
            Array.AsReadOnly(affectedDisplays));
    }

    private static DisplayTopologyChangeReason[] GetReasons(
        IReadOnlyDictionary<DisplayId, DisplayDescriptor> previous,
        IReadOnlyDictionary<DisplayId, DisplayDescriptor> current)
    {
        HashSet<DisplayTopologyChangeReason> reasons = [];
        if (current.Keys.Except(previous.Keys).Any())
        {
            reasons.Add(DisplayTopologyChangeReason.DisplayAdded);
        }
        if (previous.Keys.Except(current.Keys).Any())
        {
            reasons.Add(DisplayTopologyChangeReason.DisplayRemoved);
        }
        foreach (DisplayId id in previous.Keys.Intersect(current.Keys))
        {
            DisplayDescriptor old = previous[id];
            DisplayDescriptor next = current[id];
            if (old.Bounds != next.Bounds)
            {
                reasons.Add(DisplayTopologyChangeReason.BoundsChanged);
            }
            if (old.ScaleFactor != next.ScaleFactor)
            {
                reasons.Add(DisplayTopologyChangeReason.ScaleChanged);
            }
            if (old.IsPrimary != next.IsPrimary)
            {
                reasons.Add(DisplayTopologyChangeReason.PrimaryChanged);
            }
            if (old.RefreshRateHz != next.RefreshRateHz)
            {
                reasons.Add(DisplayTopologyChangeReason.RefreshRateChanged);
            }
        }
        if (previous.Count > 0 && current.Count > 0 &&
            GetVirtualDesktopOrigin(previous.Values) !=
                GetVirtualDesktopOrigin(current.Values))
        {
            reasons.Add(DisplayTopologyChangeReason.VirtualDesktopOriginChanged);
        }
        return reasons.OrderBy(reason => reason).ToArray();
    }

    private static (int X, int Y) GetVirtualDesktopOrigin(
        IEnumerable<DisplayDescriptor> displays) =>
        (displays.Min(display => display.Bounds.X),
            displays.Min(display => display.Bounds.Y));

    private static bool SameDisplays(
        IReadOnlyList<DisplayId> left,
        IReadOnlyList<DisplayId> right) =>
        left.Count == right.Count && left.ToHashSet().SetEquals(right);
}
