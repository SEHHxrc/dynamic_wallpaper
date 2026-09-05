using FluentAssertions;
using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.Windows.IntegrationTests;

public sealed class ExplorerMonitorTests
{
    [Fact]
    public void CheckNowPublishesOnceWhenProgmanIsRecreated()
    {
        ulong? attachPoint = 20;
        FakeExplorerShellProbe probe = new(10, [20]);
        using ExplorerMonitor monitor = new(
            () => attachPoint,
            probe,
            TimeSpan.FromSeconds(1));
        List<ExplorerRestartedEventArgs> events = [];
        monitor.Restarted += (_, args) => events.Add(args);

        monitor.CheckNow();
        probe.ProgmanHandle = 11;
        monitor.CheckNow();
        monitor.CheckNow();

        events.Should().ContainSingle();
        events[0].Reason.Should().Be(ExplorerChangeReason.ProgmanRecreated);
        events[0].PreviousAttachPoint.Should().Be(20);
        events[0].CurrentProgmanHandle.Should().Be(11);
    }

    [Fact]
    public void CheckNowCanReportAnotherInvalidationAfterRecovery()
    {
        ulong? attachPoint = 20;
        FakeExplorerShellProbe probe = new(10, []);
        using ExplorerMonitor monitor = new(
            () => attachPoint,
            probe,
            TimeSpan.FromSeconds(1));
        List<ExplorerRestartedEventArgs> events = [];
        monitor.Restarted += (_, args) => events.Add(args);

        monitor.CheckNow();
        monitor.CheckNow();
        probe.ValidWindows.Add(20);
        monitor.CheckNow();
        probe.ValidWindows.Remove(20);
        monitor.CheckNow();

        events.Should().HaveCount(2);
        events.Should().OnlyContain(args =>
            args.Reason == ExplorerChangeReason.AttachPointInvalidated);
    }

    [Fact]
    public async Task RequestedValidationDebouncesBurstIntoOneInvalidation()
    {
        ulong? attachPoint = 20;
        FakeExplorerShellProbe probe = new(10, [20]);
        using ExplorerMonitor monitor = new(
            () => attachPoint,
            probe,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromHours(1));
        List<ExplorerRestartedEventArgs> events = [];
        monitor.Restarted += (_, args) => events.Add(args);
        probe.ProgmanHandle = 11;

        monitor.RequestValidation();
        monitor.RequestValidation();
        monitor.RequestValidation();
        await WaitUntilAsync(() => events.Count == 1);

        events.Should().ContainSingle();
        events[0].Reason.Should().Be(ExplorerChangeReason.ProgmanRecreated);
    }

    [Fact]
    public async Task RequestedValidationIgnoresTaskbarCreatedStyleDpiFalsePositive()
    {
        ulong? attachPoint = 20;
        FakeExplorerShellProbe probe = new(10, [20]);
        using ExplorerMonitor monitor = new(
            () => attachPoint,
            probe,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromHours(1));
        List<ExplorerRestartedEventArgs> events = [];
        monitor.Restarted += (_, args) => events.Add(args);

        monitor.RequestValidation();
        await Task.Delay(100);

        events.Should().BeEmpty();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Explorer monitor did not publish the expected event.");
    }

    private sealed class FakeExplorerShellProbe(
        ulong progmanHandle,
        IEnumerable<ulong> validWindows) : IExplorerShellProbe
    {
        public ulong ProgmanHandle { get; set; } = progmanHandle;

        public HashSet<ulong> ValidWindows { get; } = new(validWindows);

        public ulong GetProgmanHandle() => ProgmanHandle;

        public bool IsWindow(ulong handle) => ValidWindows.Contains(handle);
    }
}
