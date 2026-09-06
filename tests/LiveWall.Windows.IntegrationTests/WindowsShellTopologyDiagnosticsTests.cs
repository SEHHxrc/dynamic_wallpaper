using FluentAssertions;
using LiveWall.Platform.Windows.Desktop;

namespace LiveWall.Windows.IntegrationTests;

public sealed class WindowsShellTopologyDiagnosticsTests
{
    private static readonly WindowsBuildIdentity BuildIdentity = new(
        "Windows",
        "26200",
        9168,
        "25H2",
        "build-lab");

    [Fact]
    public void BuildIdentityUsesUpdateBuildRevisionWhenAvailable()
    {
        BuildIdentity.FullBuild.Should().Be("26200.9168");
    }

    [Fact]
    public void BuildIdentityFallsBackToBaseBuildWithoutUpdateBuildRevision()
    {
        WindowsBuildIdentity identity = new(
            "Windows",
            "26200",
            null,
            null,
            null);

        identity.FullBuild.Should().Be("26200");
    }

    [Fact]
    public void SnapshotMarksAnUnavailableShellAsInconclusiveContext()
    {
        FakeWindowsShellNativeProbe probe = new(shellWindow: 0);

        ShellTopologySnapshot snapshot =
            WindowsShellTopologyDiagnostics.CaptureSnapshot(probe, BuildIdentity);

        snapshot.Availability.Should().Be(ShellTopologyAvailability.ShellUnavailable);
        snapshot.ShellWindowHandle.Should().Be(0);
        snapshot.ShellDescendants.Should().BeEmpty();
        snapshot.ExecutionContext.Should().Be(
            new ShellExecutionContext(7, "Service-0x0-3e7$", "Default"));
    }

    [Fact]
    public void SyntheticTreeCapturesDepthRelationshipsAndSiblingZOrder()
    {
        FakeWindowsShellNativeProbe probe = FakeWindowsShellNativeProbe.CreateShellTree();

        ShellTopologySnapshot snapshot =
            WindowsShellTopologyDiagnostics.CaptureSnapshot(probe, BuildIdentity);

        snapshot.Availability.Should().Be(ShellTopologyAvailability.Available);
        snapshot.ShellWindowHandle.Should().Be(100);
        snapshot.TopLevelWindows.Should().HaveCount(2);
        snapshot.ShellDescendants.Should().HaveCount(4);

        ShellWindowFingerprint progman = snapshot.TopLevelWindows
            .Single(window => window.ClassName == "Progman");
        progman.SiblingZOrderIndex.Should().Be(2);
        progman.ShellDefViewWindowHandle.Should().Be(101);

        ShellWindowFingerprint defView = snapshot.ShellDescendants
            .Single(window => window.WindowHandle == 101);
        defView.Depth.Should().Be(1);
        defView.ParentWindowHandle.Should().Be(100);
        defView.SiblingZOrderIndex.Should().Be(0);
        defView.SysListViewWindowHandle.Should().Be(102);
        defView.NextSiblingWindowHandle.Should().Be(104);

        ShellWindowFingerprint listView = snapshot.ShellDescendants
            .Single(window => window.WindowHandle == 102);
        listView.Depth.Should().Be(2);
        listView.ParentWindowHandle.Should().Be(101);
        listView.SiblingZOrderIndex.Should().Be(0);

        ShellWindowFingerprint header = snapshot.ShellDescendants
            .Single(window => window.WindowHandle == 103);
        header.Depth.Should().Be(3);
        header.ParentWindowHandle.Should().Be(102);

        ShellWindowFingerprint childWorker = snapshot.ShellDescendants
            .Single(window => window.WindowHandle == 104);
        childWorker.Depth.Should().Be(1);
        childWorker.SiblingZOrderIndex.Should().Be(1);
        childWorker.OwnerWindowHandle.Should().Be(900);
        childWorker.Style.Should().Be(0x40000000);
        childWorker.ExtendedStyle.Should().Be(0x80);
    }

    [Fact]
    public void NativeSnapshotIsSafeWithOrWithoutAnInteractiveShell()
    {
        ShellTopologySnapshot snapshot =
            WindowsShellTopologyDiagnostics.CaptureSnapshot();

        snapshot.BuildIdentity.Build.Should().NotBeNullOrWhiteSpace();
        if (snapshot.Availability == ShellTopologyAvailability.ShellUnavailable)
        {
            snapshot.ShellWindowHandle.Should().Be(0);
            snapshot.ShellDescendants.Should().BeEmpty();
        }
        else
        {
            snapshot.ShellWindowHandle.Should().NotBe(0);
        }
    }

    private sealed class FakeWindowsShellNativeProbe : IWindowsShellNativeProbe
    {
        private readonly Dictionary<nint, string> classes = [];
        private readonly Dictionary<nint, nint> parents = [];
        private readonly Dictionary<nint, nint> owners = [];
        private readonly Dictionary<nint, nint> nextWindows = [];
        private readonly Dictionary<nint, nint> topWindows = [];
        private readonly List<nint> topLevelWindows = [];
        private readonly List<nint> descendants = [];
        private readonly nint shellWindow;

        public FakeWindowsShellNativeProbe(nint shellWindow)
        {
            this.shellWindow = shellWindow;
        }

        public static FakeWindowsShellNativeProbe CreateShellTree()
        {
            FakeWindowsShellNativeProbe probe = new(shellWindow: 100);
            probe.topLevelWindows.AddRange([200, 300, 100]);
            probe.descendants.AddRange([101, 102, 103, 104]);
            probe.classes.Add(200, "NormalWindow");
            probe.classes.Add(300, "WorkerW");
            probe.classes.Add(100, "Progman");
            probe.classes.Add(101, "SHELLDLL_DefView");
            probe.classes.Add(102, "SysListView32");
            probe.classes.Add(103, "SysHeader32");
            probe.classes.Add(104, "WorkerW");
            probe.parents.Add(101, 100);
            probe.parents.Add(102, 101);
            probe.parents.Add(103, 102);
            probe.parents.Add(104, 100);
            probe.owners.Add(104, 900);
            probe.topWindows.Add(100, 101);
            probe.topWindows.Add(101, 102);
            probe.topWindows.Add(102, 103);
            probe.nextWindows.Add(101, 104);
            return probe;
        }

        public ShellExecutionContext CaptureExecutionContext() =>
            new(7, "Service-0x0-3e7$", "Default");

        public nint GetShellWindow() => shellWindow;

        public IReadOnlyList<nint> EnumerateTopLevelWindows() => topLevelWindows;

        public IReadOnlyList<nint> EnumerateDescendantWindows(nint parent) =>
            parent == shellWindow ? descendants : [];

        public bool IsWindow(nint window) => window != 0;

        public string? GetClassName(nint window) =>
            classes.GetValueOrDefault(window);

        public uint GetWindowThreadProcessId(nint window, out uint processId)
        {
            processId = 42;
            return 24;
        }

        public string? GetProcessName(uint processId) => "explorer";

        public ShellWindowBounds? GetBounds(nint window) =>
            new(0, 0, 1920, 1080);

        public bool IsWindowVisible(nint window) => true;

        public nint GetParent(nint window) => parents.GetValueOrDefault(window);

        public nint GetOwner(nint window) => owners.GetValueOrDefault(window);

        public ulong GetStyle(nint window) =>
            window == 104 ? 0x40000000UL : 0x10000000UL;

        public ulong GetExtendedStyle(nint window) => window == 104 ? 0x80UL : 0;

        public nint GetTopWindow(nint parent) => topWindows.GetValueOrDefault(parent);

        public nint GetNextWindow(nint window) => nextWindows.GetValueOrDefault(window);

        public nint FindDirectChild(nint parent, string className) =>
            descendants.FirstOrDefault(window =>
                GetParent(window) == parent && GetClassName(window) == className);

        public nint FindNextTopLevelWindow(nint window, string className)
        {
            int index = topLevelWindows.IndexOf(window);
            return topLevelWindows
                .Skip(index + 1)
                .FirstOrDefault(candidate => GetClassName(candidate) == className);
        }
    }
}
