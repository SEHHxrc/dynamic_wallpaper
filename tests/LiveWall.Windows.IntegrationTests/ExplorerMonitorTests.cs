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

        for (int index = 0; index < 10; index++)
        {
            monitor.RequestValidation();
        }
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

    [Fact]
    public void SuccessfulValidationPublishesShellAvailabilityAfterRestartNotification()
    {
        FakeExplorerShellProbe probe = new(10, [20]);
        using ExplorerMonitor monitor = new(
            () => 20,
            probe,
            TimeSpan.FromSeconds(1));
        List<string> order = [];
        ExplorerValidationCompletedEventArgs? completed = null;
        monitor.Restarted += (_, _) => order.Add("restarted");
        monitor.ValidationCompleted += (_, args) =>
        {
            order.Add("completed");
            completed = args;
        };
        probe.ProgmanHandle = 11;

        monitor.CheckNow();

        order.Should().Equal("restarted", "completed");
        completed.Should().NotBeNull();
        completed!.ShellAvailable.Should().BeTrue();
        completed.ShellChanged.Should().BeTrue();
        completed.CurrentProgmanHandle.Should().Be(11);
    }

    [Fact]
    public async Task ValidationFailureIsStronglyObservableAndDoesNotPublishRestart()
    {
        FakeExplorerShellProbe probe = new(10, [20]);
        using ExplorerMonitor monitor = new(
            () => 20,
            probe,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromHours(1));
        List<ExplorerRestartedEventArgs> restarted = [];
        List<ExplorerValidationFailedEventArgs> failures = [];
        monitor.Restarted += (_, args) => restarted.Add(args);
        monitor.ValidationFailed += (_, args) => failures.Add(args);
        probe.Failure = new IOException("Simulated Explorer probe failure.");

        monitor.RequestValidation();
        await WaitUntilAsync(() => failures.Count == 1);

        restarted.Should().BeEmpty();
        failures.Should().ContainSingle();
        failures[0].Exception.Should().BeSameAs(probe.Failure);
    }

    [Fact]
    public async Task FallbackPollingReportsOneOutageUntilShellBecomesHealthyAgain()
    {
        FakeExplorerShellProbe probe = new(10, []);
        using ExplorerMonitor monitor = new(
            () => 20,
            probe,
            TimeSpan.FromHours(1),
            TimeSpan.FromMilliseconds(20));
        List<ExplorerRestartedEventArgs> events = [];
        monitor.Restarted += (_, args) => events.Add(args);

        monitor.Start();
        await WaitUntilAsync(() => events.Count == 1);
        await Task.Delay(100);
        events.Should().ContainSingle();

        probe.ValidWindows.Add(20);
        await Task.Delay(50);
        probe.ValidWindows.Remove(20);
        await WaitUntilAsync(() => events.Count == 2);
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

        public Exception? Failure { get; set; }

        public ulong GetProgmanHandle() => Failure is null
            ? ProgmanHandle
            : throw Failure;

        public bool IsWindow(ulong handle) => ValidWindows.Contains(handle);
    }
}
