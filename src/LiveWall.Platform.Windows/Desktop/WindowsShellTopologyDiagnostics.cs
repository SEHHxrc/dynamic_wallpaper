using System.Globalization;
using Microsoft.Win32;

namespace LiveWall.Platform.Windows.Desktop;

public static class WindowsShellTopologyDiagnostics
{
    private const int MaximumParentDepth = 64;
    private const int MaximumSiblingWindows = 4096;
    private const string WindowsVersionRegistryPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static IReadOnlyList<ShellWindowFingerprint> Capture() =>
        CaptureSnapshot().TopLevelWindows;

    public static ShellTopologySnapshot CaptureSnapshot() =>
        CaptureSnapshot(
            new WindowsShellNativeProbe(),
            CaptureBuildIdentity());

    internal static ShellTopologySnapshot CaptureSnapshot(
        IWindowsShellNativeProbe probe,
        WindowsBuildIdentity buildIdentity)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(buildIdentity);
        Dictionary<uint, string?> processNames = [];
        List<ShellWindowFingerprint> topLevelWindows = [];
        IReadOnlyList<nint> allTopLevelWindows = probe.EnumerateTopLevelWindows();
        for (int zOrderIndex = 0; zOrderIndex < allTopLevelWindows.Count; zOrderIndex++)
        {
            nint window = allTopLevelWindows[zOrderIndex];
            string? className = probe.GetClassName(window);
            if (className is not ("Progman" or "WorkerW"))
            {
                continue;
            }

            ShellWindowFingerprint fingerprint = CaptureWindow(
                probe,
                window,
                className,
                ShellWindowScope.TopLevel,
                depth: 0,
                siblingZOrderIndex: zOrderIndex,
                processNames);
            topLevelWindows.Add(fingerprint);
        }

        ShellExecutionContext executionContext = probe.CaptureExecutionContext();
        nint shellWindow = probe.GetShellWindow();
        List<ShellWindowFingerprint> descendants = [];
        ShellTopologyAvailability availability = shellWindow != 0 && probe.IsWindow(shellWindow)
            ? ShellTopologyAvailability.Available
            : ShellTopologyAvailability.ShellUnavailable;
        if (availability == ShellTopologyAvailability.Available)
        {
            foreach (nint window in probe.EnumerateDescendantWindows(shellWindow))
            {
                string? className = probe.GetClassName(window);
                if (className is null)
                {
                    continue;
                }

                nint parent = probe.GetParent(window);
                ShellWindowFingerprint fingerprint = CaptureWindow(
                    probe,
                    window,
                    className,
                    ShellWindowScope.ShellDescendant,
                    GetDepth(probe, window, shellWindow),
                    GetSiblingZOrderIndex(probe, window, parent),
                    processNames);
                descendants.Add(fingerprint);
            }
        }

        return new ShellTopologySnapshot(
            buildIdentity,
            executionContext,
            availability,
            ToPublicHandle(shellWindow),
            topLevelWindows,
            descendants);
    }

    private static ShellWindowFingerprint CaptureWindow(
        IWindowsShellNativeProbe probe,
        nint window,
        string className,
        ShellWindowScope scope,
        int depth,
        int siblingZOrderIndex,
        Dictionary<uint, string?> processNames)
    {
        uint threadId = probe.GetWindowThreadProcessId(
            window,
            out uint processId);
        if (!processNames.TryGetValue(processId, out string? processName))
        {
            processName = probe.GetProcessName(processId);
            processNames.Add(processId, processName);
        }

        ShellWindowBounds? bounds = probe.GetBounds(window);
        nint directDefView = probe.FindDirectChild(window, "SHELLDLL_DefView");
        nint directSysListView = probe.FindDirectChild(window, "SysListView32");
        nint nextWorker = scope == ShellWindowScope.TopLevel
            ? probe.FindNextTopLevelWindow(window, "WorkerW")
            : 0;

        return new ShellWindowFingerprint(
            ToPublicHandle(window),
            scope,
            className,
            processId,
            processName,
            threadId,
            depth,
            siblingZOrderIndex,
            probe.IsWindowVisible(window),
            bounds,
            ToPublicHandle(probe.GetParent(window)),
            ToPublicHandle(probe.GetOwner(window)),
            probe.GetStyle(window),
            probe.GetExtendedStyle(window),
            ToPublicHandle(probe.GetNextWindow(window)),
            ToPublicHandle(directDefView),
            ToPublicHandle(directSysListView),
            ToPublicHandle(nextWorker));
    }

    private static int GetDepth(
        IWindowsShellNativeProbe probe,
        nint window,
        nint shellWindow)
    {
        int depth = 0;
        HashSet<nint> visited = [];
        nint current = window;
        while (current != 0 && current != shellWindow && depth < MaximumParentDepth)
        {
            if (!visited.Add(current))
            {
                return -1;
            }

            current = probe.GetParent(current);
            depth++;
        }

        return current == shellWindow ? depth : -1;
    }

    private static int GetSiblingZOrderIndex(
        IWindowsShellNativeProbe probe,
        nint window,
        nint parent)
    {
        nint current = probe.GetTopWindow(parent);
        HashSet<nint> visited = [];
        for (int index = 0;
             current != 0 && index < MaximumSiblingWindows;
             index++)
        {
            if (!visited.Add(current))
            {
                return -1;
            }

            if (current == window)
            {
                return index;
            }

            current = probe.GetNextWindow(current);
        }

        return -1;
    }

    public static WindowsBuildIdentity CaptureBuildIdentity()
    {
        string fallbackBuild = Environment.OSVersion.Version.Build.ToString(
            CultureInfo.InvariantCulture);
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                WindowsVersionRegistryPath,
                writable: false);
            string build = Convert.ToString(
                    key?.GetValue("CurrentBuildNumber"),
                    CultureInfo.InvariantCulture) ?? fallbackBuild;
            int? updateBuildRevision = key?.GetValue("UBR") is int revision
                ? revision
                : null;
            return new WindowsBuildIdentity(
                Environment.OSVersion.VersionString,
                build,
                updateBuildRevision,
                Convert.ToString(key?.GetValue("DisplayVersion"), CultureInfo.InvariantCulture),
                Convert.ToString(key?.GetValue("BuildLabEx"), CultureInfo.InvariantCulture));
        }
        catch (System.Security.SecurityException)
        {
            return new WindowsBuildIdentity(
                Environment.OSVersion.VersionString,
                fallbackBuild,
                null,
                null,
                null);
        }
        catch (UnauthorizedAccessException)
        {
            return new WindowsBuildIdentity(
                Environment.OSVersion.VersionString,
                fallbackBuild,
                null,
                null,
                null);
        }
    }

    private static ulong ToPublicHandle(nint handle) =>
        unchecked((ulong)handle.ToInt64());
}

public sealed record ShellTopologySnapshot(
    WindowsBuildIdentity BuildIdentity,
    ShellExecutionContext ExecutionContext,
    ShellTopologyAvailability Availability,
    ulong ShellWindowHandle,
    IReadOnlyList<ShellWindowFingerprint> TopLevelWindows,
    IReadOnlyList<ShellWindowFingerprint> ShellDescendants);

public sealed record ShellExecutionContext(
    uint? SessionId,
    string? WindowStationName,
    string? DesktopName);

public enum ShellTopologyAvailability
{
    Available,
    ShellUnavailable,
}

public sealed record WindowsBuildIdentity(
    string Version,
    string Build,
    int? UpdateBuildRevision,
    string? DisplayVersion,
    string? BuildLabIdentity)
{
    public string FullBuild => UpdateBuildRevision is null
        ? Build
        : $"{Build}.{UpdateBuildRevision.Value.ToString(CultureInfo.InvariantCulture)}";
}

public enum ShellWindowScope
{
    TopLevel,
    ShellDescendant,
}

public sealed record ShellWindowFingerprint(
    ulong WindowHandle,
    ShellWindowScope Scope,
    string ClassName,
    uint ProcessId,
    string? ProcessName,
    uint ThreadId,
    int Depth,
    int SiblingZOrderIndex,
    bool IsVisible,
    ShellWindowBounds? Bounds,
    ulong ParentWindowHandle,
    ulong OwnerWindowHandle,
    ulong Style,
    ulong ExtendedStyle,
    ulong NextSiblingWindowHandle,
    ulong ShellDefViewWindowHandle,
    ulong SysListViewWindowHandle,
    ulong NextWorkerWindowHandle);

public sealed record ShellWindowBounds(int X, int Y, int Width, int Height);
