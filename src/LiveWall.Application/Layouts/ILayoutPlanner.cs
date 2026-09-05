using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;

namespace LiveWall.Application.Layouts;

public interface ILayoutPlanner
{
    LayoutPlan CreatePlan(
        DisplayTopology topology,
        IReadOnlyList<WallpaperAssignment> assignments);
}

