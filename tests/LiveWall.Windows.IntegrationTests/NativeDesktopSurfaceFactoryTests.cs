using FluentAssertions;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Windows.IntegrationTests;

public sealed class NativeDesktopSurfaceFactoryTests
{
    [Fact]
    public async Task SurfaceStaysHiddenUntilExplicitReplacementAndUsesDispatcherThread()
    {
        await using DesktopWindowDispatcher dispatcher = new();
        await dispatcher.Ready;
        await using NativeDesktopSurfaceFactory factory = new(
            dispatcher,
            shellGenerationValidator: static _ => true);
        SurfaceRequest request = new(
            [new DisplayId("test-display")],
            new DisplayBounds(0, 0, 32, 32),
            FitMode.Cover);

        ulong surface = await factory.CreateAsync(
            new SurfaceId("test-surface"),
            request,
            DesktopAttachmentLease.CreateLegacy(
                "test",
                dispatcher.SignalWindowHandle,
                shellWindowHandle: 1,
                shellProcessId: checked((uint)Environment.ProcessId)),
            CancellationToken.None);

        (await factory.IsVisibleAsync(surface, CancellationToken.None)).Should().BeFalse();
        (await factory.GetWindowThreadIdAsync(surface, CancellationToken.None))
            .Should().Be(dispatcher.OwnerThreadId);

        await factory.ReplaceAsync([surface], [], CancellationToken.None);
        (await factory.IsVisibleAsync(surface, CancellationToken.None)).Should().BeTrue();

        ulong replacement = await factory.CreateAsync(
            new SurfaceId("replacement-surface"),
            request,
            DesktopAttachmentLease.CreateLegacy(
                "test",
                dispatcher.SignalWindowHandle,
                shellWindowHandle: 1,
                shellProcessId: checked((uint)Environment.ProcessId)),
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

    [Fact]
    public async Task RaisedSurfaceUsesNoRedirectionAndStaysBetweenAnchorAndBackdrop()
    {
        await using DesktopWindowDispatcher dispatcher = new();
        await dispatcher.Ready;
        await using NativeDesktopSurfaceFactory factory = new(
            dispatcher,
            shellGenerationValidator: static _ => true);
        nint anchor = 0;
        nint backdrop = 0;
        nint competitor = 0;
        ulong surface = 0;

        try
        {
            (anchor, backdrop) = await dispatcher.InvokeAsync(
                () => CreateRaisedTestAnchors(ToHandle(dispatcher.SignalWindowHandle)),
                CancellationToken.None);
            SurfaceRequest request = new(
                [new DisplayId("raised-display")],
                new DisplayBounds(0, 0, 32, 32),
                FitMode.Cover);
            DesktopAttachmentLease lease = DesktopAttachmentLease.CreateRaised(
                "raised-test",
                dispatcher.SignalWindowHandle,
                ToPublicHandle(anchor),
                ToPublicHandle(backdrop),
                shellWindowHandle: 1,
                shellProcessId: checked((uint)Environment.ProcessId),
                structuralFingerprint: "test-fingerprint");

            surface = await factory.CreateAsync(
                new SurfaceId("raised-surface"),
                request,
                lease,
                CancellationToken.None);

            (await factory.IsVisibleAsync(surface, CancellationToken.None)).Should().BeFalse();
            competitor = await dispatcher.InvokeAsync(
                () => CreateCompetingSurface(
                    ToHandle(dispatcher.SignalWindowHandle),
                    anchor),
                CancellationToken.None);
            Func<Task> activateWithCompetitor = () =>
                factory.ReplaceAsync([surface], [], CancellationToken.None);
            await activateWithCompetitor.Should()
                .ThrowAsync<DesktopAttachPointUnavailableException>()
                .WithMessage("CompetingDesktopSurface:*");
            (await factory.IsVisibleAsync(surface, CancellationToken.None)).Should().BeFalse();
            await dispatcher.InvokeAsync(
                () =>
                {
                    _ = DesktopNativeMethods.DestroyWindow(competitor);
                    competitor = 0;
                },
                CancellationToken.None);
            await factory.ReplaceAsync([surface], [], CancellationToken.None);

            await dispatcher.InvokeAsync(
                () =>
                {
                    nint surfaceWindow = ToHandle(surface);
                    ulong extendedStyle = unchecked((ulong)DesktopNativeMethods.GetWindowLongPtr(
                        surfaceWindow,
                        DesktopNativeMethods.WindowLongExtendedStyle).ToInt64());
                    ulong style = unchecked((ulong)DesktopNativeMethods.GetWindowLongPtr(
                        surfaceWindow,
                        DesktopNativeMethods.WindowLongStyle).ToInt64());
                    (extendedStyle & DesktopNativeMethods.WindowExStyleNoRedirectionBitmap)
                        .Should().NotBe(0);
                    (style & DesktopNativeMethods.WindowStyleDisabled).Should().NotBe(0);
                    DesktopNativeMethods.GetParent(surfaceWindow).Should()
                        .Be(ToHandle(dispatcher.SignalWindowHandle));
                    SiblingOrder(ToHandle(dispatcher.SignalWindowHandle), anchor, surfaceWindow, backdrop)
                        .Should().Equal(anchor, surfaceWindow, backdrop);
                },
                CancellationToken.None);
        }
        finally
        {
            if (surface != 0)
            {
                await factory.DestroyAsync(surface, CancellationToken.None);
            }

            await dispatcher.InvokeAsync(
                () =>
                {
                    if (competitor != 0 && DesktopNativeMethods.IsWindow(competitor))
                    {
                        _ = DesktopNativeMethods.DestroyWindow(competitor);
                    }

                    if (anchor != 0 && DesktopNativeMethods.IsWindow(anchor))
                    {
                        _ = DesktopNativeMethods.DestroyWindow(anchor);
                    }

                    if (backdrop != 0 && DesktopNativeMethods.IsWindow(backdrop))
                    {
                        _ = DesktopNativeMethods.DestroyWindow(backdrop);
                    }
                },
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReplacementDoesNotTouchAnActiveSurfaceFromAnOlderShellGeneration()
    {
        await using DesktopWindowDispatcher dispatcher = new();
        await dispatcher.Ready;
        await using NativeDesktopSurfaceFactory factory = new(
            dispatcher,
            shellGenerationValidator: static _ => true);
        SurfaceRequest request = new(
            [new DisplayId("test-display")],
            new DisplayBounds(0, 0, 32, 32),
            FitMode.Cover);
        uint processId = checked((uint)Environment.ProcessId);
        ulong previous = await factory.CreateAsync(
            new SurfaceId("previous-generation"),
            request,
            DesktopAttachmentLease.CreateLegacy(
                "test",
                dispatcher.SignalWindowHandle,
                shellWindowHandle: 1,
                shellProcessId: processId),
            CancellationToken.None);
        await factory.ReplaceAsync([previous], [], CancellationToken.None);
        ulong provisional = await factory.CreateAsync(
            new SurfaceId("next-generation"),
            request,
            DesktopAttachmentLease.CreateLegacy(
                "test",
                dispatcher.SignalWindowHandle,
                shellWindowHandle: 2,
                shellProcessId: processId),
            CancellationToken.None);

        await factory.ReplaceAsync([provisional], [previous], CancellationToken.None);

        (await factory.IsVisibleAsync(provisional, CancellationToken.None)).Should().BeTrue();
        (await factory.IsVisibleAsync(previous, CancellationToken.None)).Should().BeTrue(
            "a stale-generation HWND must never be reused or mutated");
        await factory.DestroyAsync(previous, CancellationToken.None);
        await factory.DestroyAsync(provisional, CancellationToken.None);
    }

    [Fact]
    public async Task AbandoningStaleGenerationOnlyForgetsTrackingAndDoesNotTouchHwnd()
    {
        await using DesktopWindowDispatcher dispatcher = new();
        await dispatcher.Ready;
        bool generationIsCurrent = true;
        await using NativeDesktopSurfaceFactory factory = new(
            dispatcher,
            shellGenerationValidator: _ => generationIsCurrent);
        SurfaceRequest request = new(
            [new DisplayId("test-display")],
            new DisplayBounds(0, 0, 32, 32),
            FitMode.Cover);
        ulong surface = await factory.CreateAsync(
            new SurfaceId("stale-generation"),
            request,
            DesktopAttachmentLease.CreateLegacy(
                "test",
                dispatcher.SignalWindowHandle,
                shellWindowHandle: 1,
                shellProcessId: checked((uint)Environment.ProcessId)),
            CancellationToken.None);

        generationIsCurrent = false;
        await factory.AbandonAsync(surface, CancellationToken.None);

        await dispatcher.InvokeAsync(
            () =>
            {
                nint window = ToHandle(surface);
                DesktopNativeMethods.IsWindow(window).Should().BeTrue(
                    "abandon must not inspect or mutate a stale-generation HWND beyond bookkeeping");
                _ = DesktopNativeMethods.DestroyWindow(window);
            },
            CancellationToken.None);
    }

    private static (nint Anchor, nint Backdrop) CreateRaisedTestAnchors(nint parent)
    {
        nint anchor = DesktopNativeMethods.CreateWindowEx(
            0,
            "STATIC",
            "Raised test anchor",
            DesktopNativeMethods.WindowStyleChild,
            0,
            0,
            1,
            1,
            parent,
            0,
            0,
            0);
        nint backdrop = DesktopNativeMethods.CreateWindowEx(
            0,
            "STATIC",
            "Raised test backdrop",
            DesktopNativeMethods.WindowStyleChild,
            0,
            0,
            1,
            1,
            parent,
            0,
            0,
            0);
        if (anchor == 0 || backdrop == 0 ||
            !DesktopNativeMethods.SetWindowPos(
                backdrop,
                DesktopNativeMethods.WindowBottom,
                0,
                0,
                0,
                0,
                DesktopNativeMethods.SetWindowPositionNoActivate |
                    DesktopNativeMethods.SetWindowPositionNoMove |
                    DesktopNativeMethods.SetWindowPositionNoSize) ||
            !DesktopNativeMethods.SetWindowPos(
                anchor,
                DesktopNativeMethods.WindowTop,
                0,
                0,
                0,
                0,
                DesktopNativeMethods.SetWindowPositionNoActivate |
                    DesktopNativeMethods.SetWindowPositionNoMove |
                    DesktopNativeMethods.SetWindowPositionNoSize))
        {
            if (anchor != 0)
            {
                _ = DesktopNativeMethods.DestroyWindow(anchor);
            }

            if (backdrop != 0)
            {
                _ = DesktopNativeMethods.DestroyWindow(backdrop);
            }

            throw new InvalidOperationException("Could not create the Raised Surface test anchors.");
        }

        return (anchor, backdrop);
    }

    private static nint CreateCompetingSurface(nint parent, nint anchor)
    {
        nint competitor = DesktopNativeMethods.CreateWindowEx(
            0,
            "STATIC",
            "Competing desktop Surface",
            DesktopNativeMethods.WindowStyleChild | DesktopNativeMethods.WindowStyleVisible,
            0,
            0,
            16,
            16,
            parent,
            0,
            0,
            0);
        if (competitor == 0 ||
            !DesktopNativeMethods.SetWindowPos(
                competitor,
                anchor,
                0,
                0,
                0,
                0,
                DesktopNativeMethods.SetWindowPositionNoActivate |
                    DesktopNativeMethods.SetWindowPositionNoMove |
                    DesktopNativeMethods.SetWindowPositionNoSize))
        {
            if (competitor != 0)
            {
                _ = DesktopNativeMethods.DestroyWindow(competitor);
            }

            throw new InvalidOperationException("Could not create a competing desktop Surface.");
        }

        return competitor;
    }

    private static List<nint> SiblingOrder(
        nint parent,
        params nint[] expectedWindows)
    {
        HashSet<nint> expected = [.. expectedWindows];
        List<nint> result = [];
        for (nint current = DesktopNativeMethods.GetTopWindow(parent);
            current != 0;
            current = DesktopNativeMethods.GetWindow(current, DesktopNativeMethods.GetWindowNext))
        {
            if (expected.Contains(current))
            {
                result.Add(current);
            }
        }

        return result;
    }

    private static nint ToHandle(ulong handle) => unchecked((nint)(long)handle);

    private static ulong ToPublicHandle(nint handle) => unchecked((ulong)handle.ToInt64());
}
