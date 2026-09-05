using FluentAssertions;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.Windows.IntegrationTests;

public sealed class NativeDesktopSurfaceFactoryTests
{
    [Fact]
    public async Task SurfaceStaysHiddenUntilExplicitReplacementAndUsesDispatcherThread()
    {
        await using DesktopWindowDispatcher dispatcher = new();
        await dispatcher.Ready;
        await using NativeDesktopSurfaceFactory factory = new(dispatcher);
        SurfaceRequest request = new(
            [new DisplayId("test-display")],
            new DisplayBounds(0, 0, 32, 32),
            FitMode.Cover);

        ulong surface = await factory.CreateAsync(
            new SurfaceId("test-surface"),
            request,
            new DesktopAttachPoint(dispatcher.SignalWindowHandle, "test"),
            CancellationToken.None);

        (await factory.IsVisibleAsync(surface, CancellationToken.None)).Should().BeFalse();
        (await factory.GetWindowThreadIdAsync(surface, CancellationToken.None))
            .Should().Be(dispatcher.OwnerThreadId);

        await factory.ReplaceAsync([surface], [], CancellationToken.None);
        (await factory.IsVisibleAsync(surface, CancellationToken.None)).Should().BeTrue();

        ulong replacement = await factory.CreateAsync(
            new SurfaceId("replacement-surface"),
            request,
            new DesktopAttachPoint(dispatcher.SignalWindowHandle, "test"),
            CancellationToken.None);
        (await factory.IsVisibleAsync(replacement, CancellationToken.None)).Should().BeFalse();
        await factory.ReplaceAsync([replacement], [surface], CancellationToken.None);
        (await factory.IsVisibleAsync(surface, CancellationToken.None)).Should().BeFalse();
        (await factory.IsVisibleAsync(replacement, CancellationToken.None)).Should().BeTrue();

        Func<Task> invalidReplacement = () => factory.ReplaceAsync(
            [ulong.MaxValue],
            [replacement],
            CancellationToken.None);
        await invalidReplacement.Should().ThrowAsync<InvalidOperationException>();
        (await factory.IsVisibleAsync(replacement, CancellationToken.None)).Should().BeTrue();

        await factory.DestroyAsync(surface, CancellationToken.None);
        await factory.DestroyAsync(replacement, CancellationToken.None);
        factory.LastCreateThreadId.Should().Be(dispatcher.OwnerThreadId);
        factory.LastReplaceThreadId.Should().Be(dispatcher.OwnerThreadId);
        factory.LastDestroyThreadId.Should().Be(dispatcher.OwnerThreadId);
    }
}
