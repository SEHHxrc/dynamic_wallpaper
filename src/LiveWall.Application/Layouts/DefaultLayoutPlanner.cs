using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;

namespace LiveWall.Application.Layouts;

public sealed class LayoutPlanningException(string errorCode, string message)
    : InvalidOperationException(message)
{
    public string ErrorCode { get; } = !string.IsNullOrWhiteSpace(errorCode)
        ? errorCode
        : throw new ArgumentException("A layout planning error code is required.", nameof(errorCode));
}

public sealed class DefaultLayoutPlanner : ILayoutPlanner
{
    public LayoutPlan CreatePlan(
        DisplayTopology topology,
        IReadOnlyList<WallpaperAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(assignments);

        Dictionary<DisplayId, DisplayDescriptor> displays = topology.Displays
            .ToDictionary(display => display.Id);

        DisplayId[] duplicateAssignments = assignments
            .GroupBy(assignment => assignment.DisplayId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateAssignments.Length > 0)
        {
            throw new ArgumentException(
                $"A display may only have one assignment: {string.Join(", ", duplicateAssignments)}.",
                nameof(assignments));
        }

        foreach (WallpaperAssignment assignment in assignments)
        {
            if (!displays.ContainsKey(assignment.DisplayId))
            {
                throw new ArgumentException(
                    $"Assignment references unknown display '{assignment.DisplayId}'.",
                    nameof(assignments));
            }
        }

        List<PlannedSurface> surfaces = [];
        var groups = assignments.GroupBy(assignment => new
        {
            assignment.WallpaperId,
            assignment.LayoutMode,
            assignment.FitMode,
            assignment.PresetId,
        });

        foreach (var group in groups)
        {
            WallpaperAssignment[] orderedAssignments = group
                .OrderBy(assignment => assignment.DisplayId.Value, StringComparer.Ordinal)
                .ToArray();

            if (group.Key.LayoutMode == LayoutMode.Span)
            {
                DisplayId[] displayIds = orderedAssignments.Select(item => item.DisplayId).ToArray();
                DisplayBounds bounds = DisplayBounds.Union(displayIds.Select(id => displays[id].Bounds));
                EnsureSpanDoesNotIntersectNonMembers(bounds, displayIds, displays.Values);
                surfaces.Add(new PlannedSurface(
                    CreatePlanId("span", group.Key.WallpaperId, displayIds),
                    displayIds,
                    group.Key.WallpaperId,
                    bounds,
                    LayoutMode.Span,
                    group.Key.FitMode,
                    group.Key.PresetId));
                continue;
            }

            foreach (WallpaperAssignment assignment in orderedAssignments)
            {
                DisplayId[] displayIds = [assignment.DisplayId];
                surfaces.Add(new PlannedSurface(
                    CreatePlanId(group.Key.LayoutMode.ToString(), group.Key.WallpaperId, displayIds),
                    displayIds,
                    group.Key.WallpaperId,
                    displays[assignment.DisplayId].Bounds,
                    group.Key.LayoutMode,
                    group.Key.FitMode,
                    group.Key.PresetId));
            }
        }

        return new LayoutPlan(
            topology.Revision,
            surfaces.OrderBy(surface => surface.PlanId, StringComparer.Ordinal).ToArray());
    }

    private static void EnsureSpanDoesNotIntersectNonMembers(
        DisplayBounds spanBounds,
        IReadOnlyCollection<DisplayId> spanDisplayIds,
        IEnumerable<DisplayDescriptor> connectedDisplays)
    {
        HashSet<DisplayId> members = spanDisplayIds.ToHashSet();
        DisplayDescriptor? conflictingDisplay = connectedDisplays.FirstOrDefault(display =>
            !members.Contains(display.Id) && IntersectsWithPositiveArea(spanBounds, display.Bounds));

        if (conflictingDisplay is not null)
        {
            throw new LayoutPlanningException(
                "layout.span.intersects_non_member_display",
                $"SpanLayoutIntersectsNonMemberDisplay: span '{string.Join("+", spanDisplayIds)}' " +
                $"intersects connected display '{conflictingDisplay.Id}'.");
        }
    }

    private static bool IntersectsWithPositiveArea(DisplayBounds left, DisplayBounds right)
    {
        long leftRight = (long)left.X + left.Width;
        long leftBottom = (long)left.Y + left.Height;
        long rightRight = (long)right.X + right.Width;
        long rightBottom = (long)right.Y + right.Height;

        return Math.Max((long)left.X, right.X) < Math.Min(leftRight, rightRight) &&
            Math.Max((long)left.Y, right.Y) < Math.Min(leftBottom, rightBottom);
    }

    private static string CreatePlanId(
        string layout,
        Domain.Wallpapers.WallpaperId wallpaperId,
        IReadOnlyList<DisplayId> displayIds) =>
        $"{layout.ToLowerInvariant()}:{wallpaperId.Value}:{string.Join("+", displayIds.Select(id => id.Value))}";
}
