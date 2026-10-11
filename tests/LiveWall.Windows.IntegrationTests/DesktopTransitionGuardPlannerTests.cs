using FluentAssertions;
using LiveWall.Domain.Displays;
using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.Windows.IntegrationTests;

public sealed class DesktopTransitionGuardPlannerTests
{
    [Fact]
    public void LandscapeToPortraitProducesOnlyTheFiniteBottomDelta()
    {
        DisplayDescriptor target = Display(
            "main",
            new DisplayBounds(0, 0, 2880, 1800));

        DesktopTransitionGuardPlan plan = DesktopTransitionGuardPlanner.Create(
            target,
            [target]);

        plan.Availability.Should().Be(DesktopTransitionGuardAvailability.Available);
        plan.CandidateBounds.Should().Be(new DisplayBounds(0, 0, 1800, 2880));
        plan.GuardRegions.Should().Equal(new DisplayBounds(0, 1800, 1800, 1080));
        plan.TotalPixelArea.Should().Be(1_944_000);
        plan.AdjacentDisplayIntersection.Should().Be(0);
    }

    [Fact]
    public void PortraitToLandscapeProducesOnlyTheFiniteRightDelta()
    {
        DisplayDescriptor target = Display(
            "main",
            new DisplayBounds(0, 0, 1800, 2880));

        DesktopTransitionGuardPlan plan = DesktopTransitionGuardPlanner.Create(
            target,
            [target]);

        plan.Availability.Should().Be(DesktopTransitionGuardAvailability.Available);
        plan.CandidateBounds.Should().Be(new DisplayBounds(0, 0, 2880, 1800));
        plan.GuardRegions.Should().Equal(new DisplayBounds(1800, 0, 1080, 1800));
        plan.TotalPixelArea.Should().Be(1_944_000);
    }

    [Fact]
    public void AdjacentDisplaysAreSubtractedWithoutCreatingAnOverlap()
    {
        DisplayDescriptor target = Display(
            "main",
            new DisplayBounds(0, 0, 1800, 2880));
        DisplayDescriptor adjacent = Display(
            "adjacent",
            new DisplayBounds(1800, 600, 1080, 600));

        DesktopTransitionGuardPlan plan = DesktopTransitionGuardPlanner.Create(
            target,
            [target, adjacent]);

        plan.Availability.Should().Be(DesktopTransitionGuardAvailability.Available);
        plan.GuardRegions.Should().Equal(
            new DisplayBounds(1800, 0, 1080, 600),
            new DisplayBounds(1800, 1200, 1080, 600));
        plan.TotalPixelArea.Should().Be(1_296_000);
        plan.AdjacentDisplayIntersection.Should().Be(0);
    }

    [Fact]
    public void SquareDisplayHasNoRotationDeltaAndFailsClosed()
    {
        DisplayDescriptor target = Display(
            "square",
            new DisplayBounds(0, 0, 2048, 2048));

        DesktopTransitionGuardPlan plan = DesktopTransitionGuardPlanner.Create(
            target,
            [target]);

        plan.Availability.Should().Be(DesktopTransitionGuardAvailability.GuardUnavailable);
        plan.Reason.Should().Be("GuardRegionEmpty");
        plan.GuardRegions.Should().BeEmpty();
    }

    private static DisplayDescriptor Display(string id, DisplayBounds bounds) =>
        new(new DisplayId(id), $"device-{id}", bounds, 1, 60, id == "main");
}
