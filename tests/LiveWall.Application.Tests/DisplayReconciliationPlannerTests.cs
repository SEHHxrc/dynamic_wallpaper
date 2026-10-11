using FluentAssertions;
using LiveWall.Application.Layouts;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Domain.Sessions;
using LiveWall.Domain.Wallpapers;
using LiveWall.Host.Orchestration;

namespace LiveWall.Application.Tests;

public sealed class DisplayReconciliationPlannerTests
{
    private static readonly DisplayId Primary = new("primary");
    private static readonly DisplayId Secondary = new("secondary");
    private static readonly WallpaperId Wallpaper = new("wallpaper");
    private readonly DisplayReconciliationPlanner planner =
        new(new DefaultLayoutPlanner());

    [Fact]
    public void AddedDisplayWithoutAssignmentDoesNotDisturbActiveSession()
    {
        DisplayTopology previous = Topology(1, Display(Primary, 0, 0, primary: true));
        DisplayTopology current = Topology(2,
            Display(Primary, 0, 0, primary: true),
            Display(Secondary, 1920, 0));

        DisplayReconciliationPlan plan = planner.CreatePlan(
            previous,
            current,
            [Assignment(Primary)],
            [Active("session-primary", Primary, new DisplayBounds(0, 0, 1920, 1080))]);

        plan.Kind.Should().Be(DisplayReconciliationKind.NoChange);
        plan.Reasons.Should().ContainSingle().Which.Should()
            .Be(DisplayTopologyChangeReason.DisplayAdded);
        plan.PreservedSessionIds.Should().Equal(new SessionId("session-primary"));
        plan.SurfacesToPrepare.Should().BeEmpty();
    }

    [Fact]
    public void ReconnectedDisplayWithOfflineAssignmentPreparesOnlyThatDisplay()
    {
        DisplayTopology previous = Topology(1, Display(Primary, 0, 0, primary: true));
        DisplayTopology current = Topology(2,
            Display(Primary, 0, 0, primary: true),
            Display(Secondary, 1920, 0));

        DisplayReconciliationPlan plan = planner.CreatePlan(
            previous,
            current,
            [Assignment(Primary), Assignment(Secondary)],
            [Active("session-primary", Primary, new DisplayBounds(0, 0, 1920, 1080))]);

        plan.Kind.Should().Be(DisplayReconciliationKind.Reconfigure);
        plan.PreservedSessionIds.Should().Equal(new SessionId("session-primary"));
        plan.SurfacesToPrepare.Should().ContainSingle()
            .Which.DisplayIds.Should().Equal(Secondary);
        plan.OfflineAssignments.Should().BeEmpty();
    }

    [Fact]
    public void RemovedDisplayRetiresItsSessionButPreservesOfflineAssignment()
    {
        DisplayTopology previous = Topology(1,
            Display(Primary, 0, 0, primary: true),
            Display(Secondary, -1920, 0));
        DisplayTopology current = Topology(2, Display(Primary, 0, 0, primary: true));

        DisplayReconciliationPlan plan = planner.CreatePlan(
            previous,
            current,
            [Assignment(Primary), Assignment(Secondary)],
            [
                Active("session-primary", Primary, new DisplayBounds(0, 0, 1920, 1080)),
                Active("session-secondary", Secondary, new DisplayBounds(-1920, 0, 1920, 1080)),
            ]);

        plan.Reasons.Should().Contain(
            DisplayTopologyChangeReason.VirtualDesktopOriginChanged);
        plan.PreservedSessionIds.Should().BeEmpty();
        plan.RetiredSessionIds.Should().BeEquivalentTo(
            [new SessionId("session-primary"), new SessionId("session-secondary")]);
        plan.OfflineAssignments.Should().ContainSingle()
            .Which.DisplayId.Should().Be(Secondary);
        plan.SurfacesToPrepare.Should().ContainSingle()
            .Which.DisplayIds.Should().Equal(Primary);
    }

    [Fact]
    public void ReconnectingNegativeCoordinateDisplayRebuildsAllForNewParentOrigin()
    {
        DisplayTopology previous = Topology(1, Display(Primary, 0, 0, primary: true));
        DisplayTopology current = Topology(2,
            Display(Primary, 0, 0, primary: true),
            Display(Secondary, -1920, 0));

        DisplayReconciliationPlan plan = planner.CreatePlan(
            previous,
            current,
            [Assignment(Primary), Assignment(Secondary)],
            [Active("session-primary", Primary, new DisplayBounds(0, 0, 1920, 1080))]);

        plan.Reasons.Should().Contain(
            DisplayTopologyChangeReason.VirtualDesktopOriginChanged);
        plan.PreservedSessionIds.Should().BeEmpty();
        plan.RetiredSessionIds.Should().Equal(new SessionId("session-primary"));
        plan.SurfacesToPrepare.Should().HaveCount(2);
        plan.AffectedDisplayIds.Should().BeEquivalentTo([Primary, Secondary]);
    }

    [Fact]
    public void ScaleChangeRebuildsAffectedSurfaceEvenWhenBoundsAreStable()
    {
        DisplayTopology previous = Topology(1, Display(Primary, 0, 0, primary: true));
        DisplayTopology current = Topology(2,
            Display(Primary, 0, 0, primary: true) with { ScaleFactor = 1.5 });

        DisplayReconciliationPlan plan = planner.CreatePlan(
            previous,
            current,
            [Assignment(Primary)],
            [Active("session-primary", Primary, new DisplayBounds(0, 0, 1920, 1080))]);

        plan.Reasons.Should().Contain(DisplayTopologyChangeReason.ScaleChanged);
        plan.RetiredSessionIds.Should().Equal(new SessionId("session-primary"));
        plan.SurfacesToPrepare.Should().ContainSingle();
        plan.ActiveSurfaceResizes.Should().ContainSingle()
            .Which.SessionId.Should().Be(new SessionId("session-primary"));
        plan.RequiresAtomicReplace.Should().BeTrue();
    }

    [Fact]
    public void PrimaryFlagOrRefreshRateOnlyDoesNotRebuildIdBoundAssignments()
    {
        DisplayTopology previous = Topology(1,
            Display(Primary, 0, 0, primary: true),
            Display(Secondary, -1920, 0));
        DisplayTopology current = Topology(2,
            Display(Primary, 0, 0, refreshRate: 60),
            Display(Secondary, -1920, 0, primary: true, refreshRate: 120));

        DisplayReconciliationPlan plan = planner.CreatePlan(
            previous,
            current,
            [Assignment(Primary), Assignment(Secondary)],
            [
                Active("session-primary", Primary, new DisplayBounds(0, 0, 1920, 1080)),
                Active("session-secondary", Secondary, new DisplayBounds(-1920, 0, 1920, 1080)),
            ]);

        plan.Kind.Should().Be(DisplayReconciliationKind.NoChange);
        plan.Reasons.Should().Contain(DisplayTopologyChangeReason.PrimaryChanged);
        plan.Reasons.Should().Contain(DisplayTopologyChangeReason.RefreshRateChanged);
        plan.PreservedSessionIds.Should().HaveCount(2);
    }

    [Fact]
    public void RemovingOneSpanDisplayRebuildsTheSpanAndKeepsOfflineIntent()
    {
        DisplayTopology previous = Topology(1,
            Display(Primary, 0, 0, primary: true),
            Display(Secondary, -1920, 0));
        DisplayTopology current = Topology(2, Display(Primary, 0, 0, primary: true));
        WallpaperAssignment[] assignments =
        [
            Assignment(Primary, LayoutMode.Span),
            Assignment(Secondary, LayoutMode.Span),
        ];

        DisplayReconciliationPlan plan = planner.CreatePlan(
            previous,
            current,
            assignments,
            [Active(
                "session-span",
                [Secondary, Primary],
                new DisplayBounds(-1920, 0, 3840, 1080))]);

        plan.RetiredSessionIds.Should().Equal(new SessionId("session-span"));
        plan.SurfacesToPrepare.Should().ContainSingle()
            .Which.DisplayIds.Should().Equal(Primary);
        plan.OfflineAssignments.Should().ContainSingle()
            .Which.DisplayId.Should().Be(Secondary);
    }

    [Fact]
    public void SpanBoundsChangeRebuildsTheWholeSpanWithOneFastResize()
    {
        DisplayTopology previous = Topology(1,
            Display(Primary, 0, 0, primary: true),
            Display(Secondary, 1920, 0));
        DisplayTopology current = Topology(2,
            Display(Primary, 0, 0, primary: true),
            Display(Secondary, 1920, 100));
        WallpaperAssignment[] assignments =
        [
            Assignment(Primary, LayoutMode.Span),
            Assignment(Secondary, LayoutMode.Span),
        ];

        DisplayReconciliationPlan plan = planner.CreatePlan(
            previous,
            current,
            assignments,
            [Active(
                "session-span",
                [Primary, Secondary],
                new DisplayBounds(0, 0, 3840, 1080))]);

        plan.Kind.Should().Be(DisplayReconciliationKind.Reconfigure);
        plan.Reasons.Should().Contain(DisplayTopologyChangeReason.BoundsChanged);
        plan.RetiredSessionIds.Should().Equal(new SessionId("session-span"));
        plan.SurfacesToPrepare.Should().ContainSingle()
            .Which.DisplayIds.Should().BeEquivalentTo([Primary, Secondary]);
        plan.SurfacesToPrepare.Single().Bounds.Should()
            .Be(new DisplayBounds(0, 0, 3840, 1180));
        plan.ActiveSurfaceResizes.Should().ContainSingle()
            .Which.SessionId.Should().Be(new SessionId("session-span"));
    }

    [Fact]
    public void SpanMemberScaleChangeRebuildsTheWholePhysicalPixelSurface()
    {
        DisplayTopology previous = Topology(1,
            Display(Primary, 0, 0, primary: true),
            Display(Secondary, 1920, 0));
        DisplayTopology current = Topology(2,
            Display(Primary, 0, 0, primary: true),
            Display(Secondary, 1920, 0) with { ScaleFactor = 1.75 });
        WallpaperAssignment[] assignments =
        [
            Assignment(Primary, LayoutMode.Span),
            Assignment(Secondary, LayoutMode.Span),
        ];

        DisplayReconciliationPlan plan = planner.CreatePlan(
            previous,
            current,
            assignments,
            [Active(
                "session-span",
                [Primary, Secondary],
                new DisplayBounds(0, 0, 3840, 1080))]);

        plan.Reasons.Should().Contain(DisplayTopologyChangeReason.ScaleChanged);
        plan.PreservedSessionIds.Should().BeEmpty();
        plan.RetiredSessionIds.Should().Equal(new SessionId("session-span"));
        plan.SurfacesToPrepare.Should().ContainSingle()
            .Which.DisplayIds.Should().BeEquivalentTo([Primary, Secondary]);
    }

    [Fact]
    public void ReconnectingSpanMemberRebuildsOneSurfaceForAllMembers()
    {
        DisplayTopology previous = Topology(1, Display(Primary, 0, 0, primary: true));
        DisplayTopology current = Topology(2,
            Display(Primary, 0, 0, primary: true),
            Display(Secondary, 1920, 0));
        WallpaperAssignment[] assignments =
        [
            Assignment(Primary, LayoutMode.Span),
            Assignment(Secondary, LayoutMode.Span),
        ];

        DisplayReconciliationPlan plan = planner.CreatePlan(
            previous,
            current,
            assignments,
            [Active(
                "session-span-single",
                Primary,
                new DisplayBounds(0, 0, 1920, 1080))]);

        plan.RetiredSessionIds.Should().Equal(new SessionId("session-span-single"));
        PlannedSurface span = plan.SurfacesToPrepare.Should().ContainSingle().Subject;
        span.DisplayIds.Should().BeEquivalentTo([Primary, Secondary]);
        span.Bounds.Should().Be(new DisplayBounds(0, 0, 3840, 1080));
        plan.RequiresAtomicReplace.Should().BeTrue();
    }

    [Fact]
    public void ZeroDisplaysSuspendsAllSessionsWithoutDeletingAssignments()
    {
        DisplayTopology previous = Topology(1, Display(Primary, 0, 0, primary: true));
        DisplayTopology current = Topology(2);

        DisplayReconciliationPlan plan = planner.CreatePlan(
            previous,
            current,
            [Assignment(Primary)],
            [Active("session-primary", Primary, new DisplayBounds(0, 0, 1920, 1080))]);

        plan.Kind.Should().Be(DisplayReconciliationKind.Suspend);
        plan.RetiredSessionIds.Should().Equal(new SessionId("session-primary"));
        plan.OfflineAssignments.Should().ContainSingle();
        plan.SurfacesToPrepare.Should().BeEmpty();
    }

    [Fact]
    public void StaleOrDuplicateTopologyRevisionIsRejected()
    {
        Action action = () => planner.CreatePlan(
            Topology(2, Display(Primary, 0, 0, primary: true)),
            Topology(2, Display(Primary, 0, 0, primary: true)),
            [],
            []);

        action.Should().Throw<ArgumentException>();
    }

    private static WallpaperAssignment Assignment(
        DisplayId displayId,
        LayoutMode mode = LayoutMode.PerDisplay) =>
        new(displayId, Wallpaper, mode, FitMode.Cover, null);

    private static ActiveSurfaceState Active(
        string sessionId,
        DisplayId displayId,
        DisplayBounds bounds) => Active(sessionId, [displayId], bounds);

    private static ActiveSurfaceState Active(
        string sessionId,
        IReadOnlyList<DisplayId> displayIds,
        DisplayBounds bounds) =>
        new(new SessionId(sessionId), Wallpaper, displayIds, bounds, FitMode.Cover);

    private static DisplayDescriptor Display(
        DisplayId id,
        int x,
        int y,
        bool primary = false,
        int refreshRate = 60) =>
        new(id, $"device-{id.Value}", new DisplayBounds(x, y, 1920, 1080), 1, refreshRate, primary);

    private static DisplayTopology Topology(
        long revision,
        params DisplayDescriptor[] displays) => new(revision, displays);
}
