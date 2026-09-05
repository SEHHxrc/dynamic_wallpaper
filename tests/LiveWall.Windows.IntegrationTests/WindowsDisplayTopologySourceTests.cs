using FluentAssertions;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Platform.Windows.Displays;

namespace LiveWall.Windows.IntegrationTests;

public sealed class WindowsDisplayTopologySourceTests
{
    [Fact]
    public void DisplayIdentityIsCaseInsensitiveAndStable()
    {
        DisplayId first = DisplayIdentity.FromDevicePath("  \\?\\DISPLAY#ACME123#ONE  ");
        DisplayId second = DisplayIdentity.FromDevicePath("\\?\\display#acme123#one");
        DisplayId different = DisplayIdentity.FromDevicePath("\\?\\DISPLAY#ACME123#TWO");

        first.Should().Be(second);
        first.Should().NotBe(different);
        first.Value.Should().StartWith("win-display-");
        first.Value.Should().HaveLength("win-display-".Length + 64);
    }

    [Fact]
    public void RefreshPublishesImmutableTopologyOnlyWhenDisplayFactsChange()
    {
        DisplaySnapshot primary = Snapshot(
            "\\?\\DISPLAY#PRIMARY",
            new DisplayBounds(0, 0, 2560, 1440),
            1.5,
            144,
            isPrimary: true);
        DisplaySnapshot secondary = Snapshot(
            "\\?\\DISPLAY#SECONDARY",
            new DisplayBounds(-1920, 0, 1920, 1080),
            1,
            60,
            isPrimary: false);
        QueueDisplaySnapshotProvider provider = new(
            [secondary, primary],
            [primary, secondary],
            [primary]);
        using WindowsDisplayTopologySource source = new(
            provider,
            TimeSpan.Zero,
            initialize: true);
        List<DisplayTopologyChangedEventArgs> changes = [];
        source.Changed += (_, args) => changes.Add(args);

        DisplayTopology unchanged = source.Refresh();
        DisplayTopology changed = source.Refresh();

        unchanged.Revision.Should().Be(1);
        unchanged.Displays.Select(display => display.DevicePath).Should().Equal(
            primary.DevicePath,
            secondary.DevicePath);
        unchanged.Displays[1].Bounds.X.Should().Be(-1920);
        unchanged.Displays[0].ScaleFactor.Should().Be(1.5);
        unchanged.Displays[0].RefreshRateHz.Should().Be(144);
        changed.Revision.Should().Be(2);
        changed.Displays.Should().ContainSingle();
        changes.Should().ContainSingle();
        changes[0].Previous.Should().BeSameAs(unchanged);
        changes[0].Current.Should().BeSameAs(changed);
    }

    [Fact]
    public async Task RequestRefreshDebouncesBurstsAndPublishesLatestSnapshot()
    {
        DisplaySnapshot initial = Snapshot(
            "\\?\\DISPLAY#ONE",
            new DisplayBounds(0, 0, 1920, 1080),
            1,
            60,
            isPrimary: true);
        DisplaySnapshot updated = initial with { RefreshRateHz = 120 };
        QueueDisplaySnapshotProvider provider = new([initial], [updated]);
        using WindowsDisplayTopologySource source = new(
            provider,
            TimeSpan.FromMilliseconds(25),
            initialize: true);
        TaskCompletionSource<DisplayTopology> changed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += (_, args) => changed.TrySetResult(args.Current);

        source.RequestRefresh();
        source.RequestRefresh();
        source.RequestRefresh();

        DisplayTopology topology = await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        topology.Revision.Should().Be(2);
        topology.Displays.Should().ContainSingle().Which.RefreshRateHz.Should().Be(120);
        provider.ReadCount.Should().Be(2);
    }

    [Fact]
    public void RefreshRejectsDuplicateStableDevicePaths()
    {
        QueueDisplaySnapshotProvider provider = new([
            Snapshot("\\?\\DISPLAY#DUPLICATE", new DisplayBounds(0, 0, 100, 100), 1, 60, true),
            Snapshot("\\?\\display#duplicate", new DisplayBounds(100, 0, 100, 100), 1, 60, false),
        ]);
        using WindowsDisplayTopologySource source = new(
            provider,
            TimeSpan.Zero,
            initialize: false);

        Action action = () => source.Refresh();

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate identity*");
    }

    private static DisplaySnapshot Snapshot(
        string devicePath,
        DisplayBounds bounds,
        double scaleFactor,
        int refreshRateHz,
        bool isPrimary) =>
        new(devicePath, bounds, scaleFactor, refreshRateHz, isPrimary);

    private sealed class QueueDisplaySnapshotProvider(
        params IReadOnlyList<DisplaySnapshot>[] snapshots) : IDisplaySnapshotProvider
    {
        private readonly Queue<IReadOnlyList<DisplaySnapshot>> remaining = new(snapshots);
        private IReadOnlyList<DisplaySnapshot>? last;

        public int ReadCount { get; private set; }

        public IReadOnlyList<DisplaySnapshot> Read()
        {
            ReadCount++;
            if (remaining.Count > 0)
            {
                last = remaining.Dequeue();
            }

            return last ?? throw new InvalidOperationException("No display snapshot was configured.");
        }
    }
}
