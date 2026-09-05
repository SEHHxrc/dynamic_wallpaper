using FluentAssertions;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.Windows.IntegrationTests;

public sealed class WindowsDesktopHostTests
{
    [Fact]
    public async Task SelectorFallsBackWhenPreferredAttachPointIsUnavailable()
    {
        FakeDesktopAdapter preferred = new(
            supported: true,
            new DesktopAttachPointUnavailableException("raised unavailable"));
        FakeDesktopAdapter fallback = new(
            supported: true,
            new DesktopAttachPoint(42, "legacy"));
        DesktopHostAdapterSelector selector = new([preferred, fallback]);

        SelectedDesktopAdapter selected = await selector.DiscoverAsync(
            ShellSnapshot(),
            CancellationToken.None);

        selected.Adapter.Should().BeSameAs(fallback);
        selected.AttachPoint.AdapterId.Should().Be("legacy");
        preferred.DiscoverCount.Should().Be(1);
        fallback.DiscoverCount.Should().Be(1);
    }

    [Fact]
    public async Task RaisedDesktopFallsBackUntilAttachPointDiscoveryIsValidated()
    {
        FakeDesktopAdapter fallback = new(
            supported: true,
            new DesktopAttachPoint(42, "legacy"));
        DesktopHostAdapterSelector selector = new([
            new RaisedDesktopAdapter(),
            fallback,
        ]);

        SelectedDesktopAdapter selected = await selector.DiscoverAsync(
            ShellSnapshot(),
            CancellationToken.None);

        selected.Adapter.Should().BeSameAs(fallback);
        selected.AttachPoint.AdapterId.Should().Be("legacy");
    }

    [Fact]
    public async Task HostCreatesReplacesRediscoversAndDestroysSurfaces()
    {
        DisplayId displayId = new("display-one");
        DisplayTopology displays = new(7, [
            new DisplayDescriptor(
                displayId,
                "device-one",
                new DisplayBounds(-100, 20, 800, 600),
                1.25,
                60,
                true),
        ]);
        FakeDesktopAdapter adapter = new(
            supported: true,
            new DesktopAttachPoint(100, "legacy"),
            new DesktopAttachPoint(200, "legacy"));
        FakeDesktopSurfaceFactory surfaceFactory = new();
        await using WindowsDesktopHost host = new(
            ShellSnapshot(),
            new DesktopHostAdapterSelector([adapter]),
            surfaceFactory);
        SurfaceRequest request = new(
            [displayId],
            displays.Displays[0].Bounds,
            FitMode.Cover);

        DesktopTopology initial = await host.EnsureTopologyAsync(
            displays,
            CancellationToken.None);
        DesktopSurface surface = await host.CreateSurfaceAsync(
            request,
            CancellationToken.None);
        await host.ReplaceSurfacesAsync(
            new SurfaceReplacement([surface.Id], [], initial.Revision),
            CancellationToken.None);
        DesktopTopology recovered = await host.RecoverAsync(
            CancellationToken.None);
        await host.DestroySurfaceAsync(surface.Id, CancellationToken.None);
        await host.DestroySurfaceAsync(surface.Id, CancellationToken.None);

        initial.Revision.Should().Be(1);
        initial.AttachPoints.Should().ContainSingle().Which.WindowHandle.Should().Be(100);
        recovered.Revision.Should().Be(2);
        recovered.AttachPoints.Should().ContainSingle().Which.WindowHandle.Should().Be(200);
        surface.WindowHandle.Should().Be(1000);
        surfaceFactory.Created.Should().ContainSingle();
        surfaceFactory.Replacements.Should().ContainSingle();
        surfaceFactory.Replacements[0].Provisional.Should().Equal(1000UL);
        surfaceFactory.Replacements[0].Replaced.Should().BeEmpty();
        surfaceFactory.Destroyed.Should().Equal(1000UL);
        adapter.RecoverCount.Should().Be(1);
    }

    [Fact]
    public async Task HostRejectsSurfaceForDisconnectedDisplay()
    {
        DisplayId connected = new("connected");
        DisplayTopology displays = new(1, [
            new DisplayDescriptor(
                connected,
                "device",
                new DisplayBounds(0, 0, 100, 100),
                1,
                60,
                true),
        ]);
        FakeDesktopAdapter adapter = new(
            supported: true,
            new DesktopAttachPoint(100, "legacy"));
        await using WindowsDesktopHost host = new(
            ShellSnapshot(),
            new DesktopHostAdapterSelector([adapter]),
            new FakeDesktopSurfaceFactory());
        await host.EnsureTopologyAsync(displays, CancellationToken.None);

        Func<Task> action = () => host.CreateSurfaceAsync(
            new SurfaceRequest(
                [new DisplayId("missing")],
                new DisplayBounds(0, 0, 100, 100),
                FitMode.Cover),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not connected*");
    }

    [Fact]
    public async Task HostRejectsReplacementForStaleDesktopTopology()
    {
        DisplayId displayId = new("connected");
        DisplayTopology displays = new(1, [
            new DisplayDescriptor(
                displayId,
                "device",
                new DisplayBounds(0, 0, 100, 100),
                1,
                60,
                true),
        ]);
        FakeDesktopAdapter adapter = new(
            supported: true,
            new DesktopAttachPoint(100, "legacy"));
        FakeDesktopSurfaceFactory factory = new();
        await using WindowsDesktopHost host = new(
            ShellSnapshot(),
            new DesktopHostAdapterSelector([adapter]),
            factory);
        DesktopTopology desktopTopology = await host.EnsureTopologyAsync(
            displays,
            CancellationToken.None);
        DesktopSurface surface = await host.CreateSurfaceAsync(
            new SurfaceRequest([displayId], displays.Displays[0].Bounds, FitMode.Cover),
            CancellationToken.None);

        Func<Task> replace = () => host.ReplaceSurfacesAsync(
            new SurfaceReplacement([surface.Id], [], desktopTopology.Revision - 1),
            CancellationToken.None);

        await replace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*stale*");
        factory.Replacements.Should().BeEmpty();
    }

    private static WindowsShellSnapshot ShellSnapshot() =>
        new("Windows 11", "26100", RaisedDesktopEnabled: true);

    private sealed class FakeDesktopAdapter : IDesktopHostAdapter
    {
        private readonly bool supported;
        private readonly Queue<object> discoveries;

        public FakeDesktopAdapter(bool supported, params object[] discoveries)
        {
            this.supported = supported;
            this.discoveries = new Queue<object>(discoveries);
        }

        public int DiscoverCount { get; private set; }

        public int RecoverCount { get; private set; }

        public bool IsSupported(WindowsShellSnapshot snapshot) => supported;

        public Task<DesktopAttachPoint> DiscoverAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscoverCount++;
            object result = discoveries.Dequeue();
            return result switch
            {
                DesktopAttachPoint point => Task.FromResult(point),
                Exception exception => Task.FromException<DesktopAttachPoint>(exception),
                _ => throw new InvalidOperationException("Unexpected fake discovery result."),
            };
        }

        public Task RecoverAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecoverCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDesktopSurfaceFactory : IDesktopSurfaceFactory
    {
        private ulong nextHandle = 1000;

        public List<(SurfaceId Id, SurfaceRequest Request, DesktopAttachPoint AttachPoint)> Created { get; } = [];

        public List<(IReadOnlyList<ulong> Provisional, IReadOnlyList<ulong> Replaced)> Replacements { get; } = [];

        public List<ulong> Destroyed { get; } = [];

        public async IAsyncEnumerable<DesktopWindowSignal> ReadSignalsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public Task<ulong> CreateAsync(
            SurfaceId id,
            SurfaceRequest request,
            DesktopAttachPoint attachPoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Created.Add((id, request, attachPoint));
            return Task.FromResult(nextHandle++);
        }

        public Task ReplaceAsync(
            IReadOnlyList<ulong> provisionalSurfaceHandles,
            IReadOnlyList<ulong> replacedSurfaceHandles,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Replacements.Add((
                provisionalSurfaceHandles.ToArray(),
                replacedSurfaceHandles.ToArray()));
            return Task.CompletedTask;
        }

        public Task DestroyAsync(ulong surfaceHandle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Destroyed.Add(surfaceHandle);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
