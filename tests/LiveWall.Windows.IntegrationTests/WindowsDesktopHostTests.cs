using FluentAssertions;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.Windows.IntegrationTests;

public sealed class WindowsDesktopHostTests
{
    [Fact]
    public void LegacyWorkerWIsDisabledForAnUnvalidatedBuild()
    {
        LegacyWorkerWAdapter adapter = new();

        adapter.IsSupported(ShellSnapshot()).Should().BeFalse();
    }

    [Fact]
    public void LegacyWorkerWCanBeExplicitlyEnabledForDiagnostics()
    {
        LegacyWorkerWAdapter adapter = new(allowUnvalidatedBuild: true);

        adapter.IsSupported(ShellSnapshot()).Should().BeTrue();
    }

    [Fact]
    public void ShellSnapshotUsesTheCompleteBuildAsCompatibilityKey()
    {
        WindowsShellSnapshot snapshot = ShellSnapshot();

        snapshot.HasCompleteBuildIdentity.Should().BeTrue();
        snapshot.FullBuild.Should().Be("26100.1234");
    }

    [Fact]
    public async Task SelectorFallsBackWhenPreferredAttachPointIsUnavailable()
    {
        FakeDesktopAdapter preferred = new(
            supported: true,
            new DesktopAttachPointUnavailableException("raised unavailable"));
        FakeDesktopAdapter fallback = new(
            supported: true,
            Lease(42));
        DesktopHostAdapterSelector selector = new([preferred, fallback]);

        SelectedDesktopAdapter selected = await selector.DiscoverAsync(
            ShellSnapshot(),
            CancellationToken.None);

        selected.Adapter.Should().BeSameAs(fallback);
        selected.AttachmentLease.Capability.AdapterId.Should().Be("legacy");
        preferred.DiscoverCount.Should().Be(1);
        fallback.DiscoverCount.Should().Be(1);
    }

    [Fact]
    public async Task RaisedDesktopFallsBackUntilAttachPointDiscoveryIsValidated()
    {
        FakeDesktopAdapter fallback = new(
            supported: true,
            Lease(42));
        DesktopHostAdapterSelector selector = new([
            new RaisedDesktopAdapter(),
            fallback,
        ]);

        SelectedDesktopAdapter selected = await selector.DiscoverAsync(
            ShellSnapshot(),
            CancellationToken.None);

        selected.Adapter.Should().BeSameAs(fallback);
        selected.AttachmentLease.Capability.AdapterId.Should().Be("legacy");
    }

    [Fact]
    public void RaisedDesktopProductionAdapterIsDisabledWhileAllowlistIsEmpty()
    {
        RaisedDesktopAdapter adapter = new();

        adapter.IsSupported(ShellSnapshot()).Should().BeFalse();
    }

    [Fact]
    public async Task RaisedDesktopDiagnosticOverrideReturnsAnInternalLease()
    {
        DesktopAttachmentLease lease = DesktopAttachmentLease.CreateRaised(
            RaisedDesktopAdapter.AdapterId,
            parentWindowHandle: 10,
            zOrderAnchorWindowHandle: 11,
            backdropWindowHandle: 12,
            shellWindowHandle: 10,
            shellProcessId: 20,
            structuralFingerprint: "fingerprint");
        RaisedDesktopAdapter adapter = new(
            allowUnvalidatedBuild: true,
            _ => Task.FromResult(lease));

        adapter.IsSupported(ShellSnapshot()).Should().BeTrue();
        DesktopAttachmentLease discovered = await adapter.DiscoverAsync(CancellationToken.None);

        discovered.Should().BeSameAs(lease);
        discovered.Capability.Should().Be(new DesktopAttachmentCapability(
            RaisedDesktopAdapter.AdapterId,
            "raised-progman-no-redirection",
            "hwnd-child-v1"));
        discovered.StructuralFingerprint.Should().Be("fingerprint");
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
            Lease(100),
            Lease(200));
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
        initial.Attachments.Should().ContainSingle()
            .Which.AdapterId.Should().Be("legacy");
        recovered.Revision.Should().Be(2);
        recovered.Attachments.Should().ContainSingle()
            .Which.AdapterId.Should().Be("legacy");
        surface.WindowHandle.Should().Be(1000);
        surfaceFactory.Created.Should().ContainSingle();
        surfaceFactory.Replacements.Should().ContainSingle();
        surfaceFactory.Replacements[0].Provisional.Should().Equal(1000UL);
        surfaceFactory.Replacements[0].Replaced.Should().BeEmpty();
        surfaceFactory.Destroyed.Should().Equal(1000UL);
        surfaceFactory.Created[0].AttachmentLease.ParentWindowHandle.Should().Be(100);
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
            Lease(100));
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
            Lease(100));
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
        new(
            "Windows 11",
            "26100",
            RaisedDesktopEnabled: true,
            UpdateBuildRevision: 1234);

    private static DesktopAttachmentLease Lease(ulong parentWindowHandle) =>
        DesktopAttachmentLease.CreateLegacy(
            "legacy",
            parentWindowHandle,
            shellWindowHandle: 1,
            shellProcessId: 1);

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

        public bool IsSupported(WindowsShellSnapshot snapshot) => supported;

        public Task<DesktopAttachmentLease> DiscoverAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscoverCount++;
            object result = discoveries.Dequeue();
            return result switch
            {
                DesktopAttachmentLease lease => Task.FromResult(lease),
                Exception exception => Task.FromException<DesktopAttachmentLease>(exception),
                _ => throw new InvalidOperationException("Unexpected fake discovery result."),
            };
        }
    }

    private sealed class FakeDesktopSurfaceFactory : IDesktopSurfaceFactory
    {
        private ulong nextHandle = 1000;

        public List<(SurfaceId Id, SurfaceRequest Request, DesktopAttachmentLease AttachmentLease)> Created { get; } = [];

        public List<(IReadOnlyList<ulong> Provisional, IReadOnlyList<ulong> Replaced)> Replacements { get; } = [];

        public List<ulong> Destroyed { get; } = [];

        public List<ulong> Abandoned { get; } = [];

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
            DesktopAttachmentLease attachmentLease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Created.Add((id, request, attachmentLease));
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

        public Task AbandonAsync(ulong surfaceHandle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Abandoned.Add(surfaceHandle);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
