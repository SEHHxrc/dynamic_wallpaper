using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;

namespace LiveWall.Application.Layouts;

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

    private static string CreatePlanId(
        string layout,
        Domain.Wallpapers.WallpaperId wallpaperId,
        IReadOnlyList<DisplayId> displayIds) =>
        $"{layout.ToLowerInvariant()}:{wallpaperId.Value}:{string.Join("+", displayIds.Select(id => id.Value))}";
}
