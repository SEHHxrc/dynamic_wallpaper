using FluentAssertions;
using LiveWall.Application.Layouts;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Wallpapers;

namespace LiveWall.Application.Tests;

public sealed class LayoutPlannerTests
{
    private readonly DefaultLayoutPlanner planner = new();

    [Fact]
    public void SpanCombinesAllTargetDisplaysIntoOneSurface()
    {
        DisplayTopology topology = CreateTopology();
        WallpaperId wallpaperId = new("sample.wallpaper");
        WallpaperAssignment[] assignments =
        [
            new(new DisplayId("left"), wallpaperId, LayoutMode.Span, FitMode.Cover, null),
            new(new DisplayId("primary"), wallpaperId, LayoutMode.Span, FitMode.Cover, null),
        ];

        LayoutPlan plan = planner.CreatePlan(topology, assignments);

        plan.TopologyRevision.Should().Be(7);
        PlannedSurface surface = plan.Surfaces.Should().ContainSingle().Subject;
        surface.DisplayIds.Should().Equal(new DisplayId("left"), new DisplayId("primary"));
        surface.Bounds.Should().Be(new DisplayBounds(-1920, 0, 4480, 1440));
        surface.LayoutMode.Should().Be(LayoutMode.Span);
    }

    [Fact]
    public void PerDisplayCreatesOneSurfaceForEachAssignment()
    {
        DisplayTopology topology = CreateTopology();
        WallpaperAssignment[] assignments =
        [
            new(new DisplayId("left"), new WallpaperId("sample.left"), LayoutMode.PerDisplay, FitMode.Contain, null),
            new(new DisplayId("primary"), new WallpaperId("sample.primary"), LayoutMode.PerDisplay, FitMode.Cover, null),
        ];

        LayoutPlan plan = planner.CreatePlan(topology, assignments);

        plan.Surfaces.Should().HaveCount(2);
        plan.Surfaces.Should().OnlyContain(surface => surface.DisplayIds.Count == 1);
    }

    [Fact]
    public void DuplicateAssignmentsForOneDisplayAreRejected()
    {
        DisplayTopology topology = CreateTopology();
        WallpaperAssignment[] assignments =
        [
            new(new DisplayId("left"), new WallpaperId("sample.one"), LayoutMode.PerDisplay, FitMode.Cover, null),
            new(new DisplayId("left"), new WallpaperId("sample.two"), LayoutMode.PerDisplay, FitMode.Cover, null),
        ];

        Action create = () => planner.CreatePlan(topology, assignments);

        create.Should().Throw<ArgumentException>().WithParameterName("assignments");
    }

    private static DisplayTopology CreateTopology() =>
        new(
            7,
            [
                new DisplayDescriptor(
                    new DisplayId("left"),
                    "display/left",
                    new DisplayBounds(-1920, 0, 1920, 1080),
                    1,
                    60,
                    false),
                new DisplayDescriptor(
                    new DisplayId("primary"),
                    "display/primary",
                    new DisplayBounds(0, 0, 2560, 1440),
                    1.25,
                    144,
                    true),
            ]);
}
