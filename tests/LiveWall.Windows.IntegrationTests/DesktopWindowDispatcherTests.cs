using FluentAssertions;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Windows.IntegrationTests;

public sealed class DesktopWindowDispatcherTests
{
    [Fact]
    public async Task WorkRunsOnOneStaThreadAndDispatcherStopsCleanly()
    {
        DesktopWindowDispatcher dispatcher = new();
        await dispatcher.Ready;

        (int ManagedThreadId, ApartmentState Apartment) first = await dispatcher.InvokeAsync(
            () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()),
            CancellationToken.None);
        (int ManagedThreadId, ApartmentState Apartment) second = await dispatcher.InvokeAsync(
            () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()),
            CancellationToken.None);

        first.ManagedThreadId.Should().Be(second.ManagedThreadId);
        first.Apartment.Should().Be(ApartmentState.STA);
        dispatcher.OwnerThreadId.Should().NotBe(0);
        dispatcher.SignalWindowHandle.Should().NotBe(0);

        await dispatcher.DisposeAsync();
        await dispatcher.Completion;
    }

    [Fact]
    public async Task CancellationOnlyCancelsWorkThatHasNotStarted()
    {
        await using DesktopWindowDispatcher dispatcher = new();
        await dispatcher.Ready;
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        Task blocking = dispatcher.InvokeAsync(
            () =>
            {
                started.Set();
                release.Wait();
            },
            CancellationToken.None);
        started.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();

        using CancellationTokenSource cancellation = new();
        Task<int> canceled = dispatcher.InvokeAsync(() => 42, cancellation.Token);
        cancellation.Cancel();
        release.Set();

        await blocking;
        Func<Task> waitCanceled = async () => await canceled;
        await waitCanceled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task HiddenTopLevelWindowPublishesTaskbarCreatedSignal()
    {
        await using DesktopWindowDispatcher dispatcher = new();
        await dispatcher.Ready;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        await using IAsyncEnumerator<DesktopWindowSignal> signals = dispatcher
            .ReadSignalsAsync(timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        bool posted = DesktopNativeMethods.PostMessage(
            unchecked((nint)(long)dispatcher.SignalWindowHandle),
            dispatcher.TaskbarCreatedMessage,
            0,
            0);

        posted.Should().BeTrue();
        (await signals.MoveNextAsync()).Should().BeTrue();
        signals.Current.Kind.Should().Be(DesktopWindowSignalKind.TaskbarCreated);
    }
}
