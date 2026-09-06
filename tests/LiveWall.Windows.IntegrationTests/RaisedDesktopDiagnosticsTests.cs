using FluentAssertions;
using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.Windows.IntegrationTests;

public sealed class RaisedDesktopDiagnosticsTests
{
    private static readonly ShellWindowBounds VirtualDesktop = new(0, 0, 2880, 1800);

    [Fact]
    public void PreflightRejectsAProcessOutsideTheInteractiveDesktop()
    {
        ShellTopologySnapshot snapshot = CreateSnapshot(desktopName: "CodexSandboxDesktop-test");

        RaisedDesktopAnalysis analysis = RaisedDesktopDiagnostics.AnalyzePreflight(
            snapshot,
            snapshot,
            VirtualDesktop);

        analysis.Context.Should().BeNull();
        analysis.Rejections.Should().ContainSingle(rejection =>
            rejection.Code == RaisedDesktopRejectionCode.WrongInteractiveDesktop);
    }

    [Theory]
    [InlineData(false, true, RaisedDesktopRejectionCode.ProgmanNotRaised)]
    [InlineData(true, false, RaisedDesktopRejectionCode.DefViewNotLayered)]
    public void PreflightRequiresRaisedProgmanAndLayeredDefView(
        bool progmanRaised,
        bool defViewLayered,
        RaisedDesktopRejectionCode expected)
    {
        ShellTopologySnapshot snapshot = CreateSnapshot(
            progmanRaised: progmanRaised,
            defViewLayered: defViewLayered);

        RaisedDesktopAnalysis analysis = RaisedDesktopDiagnostics.AnalyzePreflight(
            snapshot,
            snapshot,
            VirtualDesktop);

        analysis.Context.Should().BeNull();
        analysis.Rejections.Should().Contain(rejection => rejection.Code == expected);
    }

    [Fact]
    public void RequestedTopologyAcceptsOnlyTheRaisedDesktopWorkerShape()
    {
        ShellTopologySnapshot before = CreateSnapshot();
        ShellTopologySnapshot after = CreateSnapshot(includeWorker: true);

        RaisedDesktopAnalysis analysis = RaisedDesktopDiagnostics.AnalyzeRequestedTopology(
            before,
            after,
            VirtualDesktop);

        analysis.Rejections.Should().BeEmpty();
        analysis.Context.Should().NotBeNull();
        analysis.Context!.WorkerWindowHandle.Should().Be(104);
        analysis.Context.WorkerZOrderIndex.Should().Be(1);
    }

    [Fact]
    public void RequestedTopologyExplainsInvalidWorkerProperties()
    {
        ShellTopologySnapshot before = CreateSnapshot();
        ShellTopologySnapshot after = CreateSnapshot(
            includeWorker: true,
            workerOwner: 900,
            workerBounds: new ShellWindowBounds(0, 0, 100, 100),
            workerZOrder: 0);

        RaisedDesktopAnalysis analysis = RaisedDesktopDiagnostics.AnalyzeRequestedTopology(
            before,
            after,
            VirtualDesktop);

        analysis.Context.Should().BeNull();
        analysis.Rejections.Select(rejection => rejection.Code).Should().Contain([
            RaisedDesktopRejectionCode.WorkerHasOwner,
            RaisedDesktopRejectionCode.WorkerBoundsMismatch,
            RaisedDesktopRejectionCode.WorkerZOrderInvalid,
        ]);
    }

    [Fact]
    public void RequestedTopologyRejectsAWorkerThatIsNotADirectProgmanChild()
    {
        ShellTopologySnapshot before = CreateSnapshot();
        ShellTopologySnapshot after = CreateSnapshot(
            includeWorker: true,
            workerParent: 101);

        RaisedDesktopAnalysis analysis = RaisedDesktopDiagnostics.AnalyzeRequestedTopology(
            before,
            after,
            VirtualDesktop);

        analysis.Context.Should().BeNull();
        analysis.Rejections.Should().ContainSingle(rejection =>
            rejection.Code == RaisedDesktopRejectionCode.WorkerNotDirectChild);
    }

    [Fact]
    public void RejectedRequestWithAnIntroducedWorkerReportsCleanupIncomplete()
    {
        ShellTopologySnapshot before = CreateSnapshot();
        ShellTopologySnapshot after = CreateSnapshot(
            includeWorker: true,
            workerOwner: 900);

        RaisedDesktopDiagnostics.ClassifyRejectedRequest(before, after)
            .Should().Be(RaisedDesktopDiagnosticStatus.CleanupIncomplete);
    }

    [Theory]
    [InlineData(RaisedDesktopPresentationKind.LayeredGdi, true, true)]
    [InlineData(RaisedDesktopPresentationKind.NonLayeredGdi, false, true)]
    [InlineData(RaisedDesktopPresentationKind.NonLayeredGdi, true, false)]
    [InlineData(RaisedDesktopPresentationKind.DirectCompositionSwapChain, false, true)]
    [InlineData(RaisedDesktopPresentationKind.DirectCompositionSwapChain, true, false)]
    public void PresentationSurfaceMustMatchTheDeclaredPresentationKind(
        RaisedDesktopPresentationKind presentationKind,
        bool surfaceLayered,
        bool expected)
    {
        ShellTopologySnapshot snapshot = CreateSnapshot(
            includeWorker: true,
            workerZOrder: 2,
            includeSurface: true,
            surfaceLayered: surfaceLayered);
        RaisedDesktopAnalysis analysis = RaisedDesktopDiagnostics.AnalyzeRequestedTopology(
            CreateSnapshot(),
            CreateSnapshot(includeWorker: true),
            VirtualDesktop);

        bool result = RaisedDesktopDiagnostics.ValidatePresentationSurface(
            snapshot,
            analysis.Context!,
            VirtualDesktop,
            presentationKind);

        result.Should().Be(expected);
    }

    [Fact]
    public void PresentationSurfaceRejectsATopLevelNonClientFrame()
    {
        ShellTopologySnapshot snapshot = CreateSnapshot(
            includeWorker: true,
            workerZOrder: 2,
            includeSurface: true,
            surfaceLayered: false,
            surfaceCaption: true);
        RaisedDesktopAnalysis analysis = RaisedDesktopDiagnostics.AnalyzeRequestedTopology(
            CreateSnapshot(),
            CreateSnapshot(includeWorker: true),
            VirtualDesktop);

        RaisedDesktopDiagnostics.ValidatePresentationSurface(
                snapshot,
                analysis.Context!,
                VirtualDesktop,
                RaisedDesktopPresentationKind.DirectCompositionSwapChain)
            .Should().BeFalse();
    }

    [Fact]
    public void PresentationSurfaceRejectsAnInteractiveApplicationWindow()
    {
        ShellTopologySnapshot snapshot = CreateSnapshot(
            includeWorker: true,
            workerZOrder: 2,
            includeSurface: true,
            surfaceLayered: false,
            surfaceInteractive: true);
        RaisedDesktopAnalysis analysis = RaisedDesktopDiagnostics.AnalyzeRequestedTopology(
            CreateSnapshot(),
            CreateSnapshot(includeWorker: true),
            VirtualDesktop);

        RaisedDesktopDiagnostics.ValidatePresentationSurface(
                snapshot,
                analysis.Context!,
                VirtualDesktop,
                RaisedDesktopPresentationKind.DirectCompositionSwapChain)
            .Should().BeFalse();
    }

    [Fact]
    public void FingerprintExcludesTransientHandlesAndProcessIds()
    {
        ShellTopologySnapshot first = CreateSnapshot(handleOffset: 0, processId: 42);
        ShellTopologySnapshot second = CreateSnapshot(handleOffset: 1000, processId: 99);

        RaisedDesktopStructureFingerprint firstFingerprint =
            RaisedDesktopDiagnostics.CreateFingerprint(first);
        RaisedDesktopStructureFingerprint secondFingerprint =
            RaisedDesktopDiagnostics.CreateFingerprint(second);

        firstFingerprint.Value.Should().Be(secondFingerprint.Value);
        firstFingerprint.Summary.ToLowerInvariant().Should().NotContain("pid")
            .And.NotContain("hwnd");
    }

    [Fact]
    public void PreflightRejectsAnUnstableStructure()
    {
        ShellTopologySnapshot before = CreateSnapshot();
        ShellTopologySnapshot changed = CreateSnapshot(includeWorker: true);

        RaisedDesktopAnalysis analysis = RaisedDesktopDiagnostics.AnalyzePreflight(
            before,
            changed,
            VirtualDesktop);

        analysis.Context.Should().BeNull();
        analysis.Rejections.Should().Contain(rejection =>
            rejection.Code == RaisedDesktopRejectionCode.UnexpectedTopologyMutation);
    }

    [Fact]
    public void PreflightIgnoresUnrelatedTopLevelZOrderChanges()
    {
        ShellTopologySnapshot before = CreateSnapshot();
        ShellTopologySnapshot changed = before with
        {
            TopLevelWindows = before.TopLevelWindows
                .Select(window => window with
                {
                    SiblingZOrderIndex = window.SiblingZOrderIndex + 7,
                })
                .ToArray(),
        };

        RaisedDesktopAnalysis analysis = RaisedDesktopDiagnostics.AnalyzePreflight(
            before,
            changed,
            VirtualDesktop);

        analysis.Context.Should().NotBeNull();
        analysis.Rejections.Should().BeEmpty();
        analysis.BeforeFingerprint.Value.Should().NotBe(analysis.AfterFingerprint.Value);
        RaisedDesktopDiagnostics.CreateCandidateFingerprint(before).Value.Should().Be(
            RaisedDesktopDiagnostics.CreateCandidateFingerprint(changed).Value);
    }

    private static ShellTopologySnapshot CreateSnapshot(
        string desktopName = "Default",
        bool progmanRaised = true,
        bool defViewLayered = true,
        bool includeWorker = false,
        ulong workerOwner = 0,
        ShellWindowBounds? workerBounds = null,
        int workerZOrder = 1,
        ulong? workerParent = null,
        bool includeSurface = false,
        bool surfaceLayered = true,
        bool surfaceCaption = false,
        bool surfaceInteractive = false,
        ulong handleOffset = 0,
        uint processId = 42)
    {
        ulong progman = 100 + handleOffset;
        ulong defView = 101 + handleOffset;
        ulong listView = 102 + handleOffset;
        ulong header = 103 + handleOffset;
        ulong worker = 104 + handleOffset;
        ShellWindowFingerprint progmanWindow = Window(
            progman,
            ShellWindowScope.TopLevel,
            "Progman",
            processId,
            depth: 0,
            zOrder: 10,
            bounds: VirtualDesktop,
            parent: 0,
            extendedStyle: progmanRaised ? 0x00200080UL : 0x80UL,
            defView: defView);
        List<ShellWindowFingerprint> descendants =
        [
            Window(
                defView,
                ShellWindowScope.ShellDescendant,
                "SHELLDLL_DefView",
                processId,
                depth: 1,
                zOrder: 0,
                bounds: VirtualDesktop,
                parent: progman,
                extendedStyle: defViewLayered ? 0x00080000UL : 0UL,
                listView: listView,
                nextSibling: includeSurface ? 105 + handleOffset : includeWorker ? worker : 0),
            Window(
                listView,
                ShellWindowScope.ShellDescendant,
                "SysListView32",
                processId,
                depth: 2,
                zOrder: 0,
                bounds: VirtualDesktop,
                parent: defView),
            Window(
                header,
                ShellWindowScope.ShellDescendant,
                "SysHeader32",
                processId,
                depth: 3,
                zOrder: 0,
                bounds: new ShellWindowBounds(0, 0, 0, 0),
                parent: listView),
        ];
        if (includeWorker)
        {
            ulong surface = 105 + handleOffset;
            if (includeSurface)
            {
                descendants.Add(Window(
                    surface,
                    ShellWindowScope.ShellDescendant,
                    "LiveWall.RaisedDesktopColor.test",
                    77,
                    depth: 1,
                    zOrder: 1,
                    bounds: VirtualDesktop,
                    parent: progman,
                    style: (surfaceInteractive ? 0x50000000UL : 0x58000000UL) |
                        (surfaceCaption ? 0x00C00000UL : 0UL),
                    extendedStyle: (surfaceInteractive ? 0UL : 0x08000080UL) |
                        (surfaceLayered ? 0x00080000UL : 0UL),
                    nextSibling: worker));
            }
            descendants.Add(Window(
                worker,
                ShellWindowScope.ShellDescendant,
                "WorkerW",
                processId,
                depth: 1,
                zOrder: workerZOrder,
                bounds: workerBounds ?? VirtualDesktop,
                parent: workerParent ?? progman,
                owner: workerOwner));
        }

        return new(
            new WindowsBuildIdentity("Windows", "26200", 9168, "25H2", "lab"),
            new ShellExecutionContext(2, "WinSta0", desktopName),
            ShellTopologyAvailability.Available,
            progman,
            [progmanWindow],
            descendants);
    }

    private static ShellWindowFingerprint Window(
        ulong handle,
        ShellWindowScope scope,
        string className,
        uint processId,
        int depth,
        int zOrder,
        ShellWindowBounds bounds,
        ulong parent,
        ulong owner = 0,
        ulong style = 0x50000000,
        ulong extendedStyle = 0,
        ulong defView = 0,
        ulong listView = 0,
        ulong nextSibling = 0) =>
        new(
            handle,
            scope,
            className,
            processId,
            "explorer",
            24,
            depth,
            zOrder,
            true,
            bounds,
            parent,
            owner,
            style,
            extendedStyle,
            nextSibling,
            defView,
            listView,
            0);
}
