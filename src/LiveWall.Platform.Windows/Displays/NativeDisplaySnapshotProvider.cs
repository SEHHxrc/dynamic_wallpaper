using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Domain.Displays;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Displays;

internal sealed class NativeDisplaySnapshotProvider : IDisplaySnapshotProvider
{
    private const int DefaultRefreshRateHz = 60;
    private const double DefaultDpi = 96;
    private const int MaximumDisplayConfigAttempts = 5;

    public IReadOnlyList<DisplaySnapshot> Read()
    {
        Dictionary<string, DisplayPathDetails> paths = ReadActiveDisplayPaths();
        List<DisplaySnapshot> snapshots = [];
        DisplayNativeMethods.MonitorEnumProcedure callback = (
            nint monitor,
            nint monitorDeviceContext,
            ref NativeRect monitorBounds,
            nint data) =>
        {
            _ = monitorDeviceContext;
            _ = monitorBounds;
            _ = data;
            MonitorInfoEx monitorInfo = MonitorInfoEx.Create();
            if (!DisplayNativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMonitorInfo failed.");
            }

            string gdiName = monitorInfo.DeviceName.TrimEnd('\0');
            if (!paths.TryGetValue(gdiName, out DisplayPathDetails? path))
            {
                throw new InvalidOperationException(
                    $"DisplayConfig did not provide a stable target path for '{gdiName}'.");
            }

            int width = checked(monitorInfo.Monitor.Right - monitorInfo.Monitor.Left);
            int height = checked(monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top);
            snapshots.Add(new DisplaySnapshot(
                path.DevicePath,
                new DisplayBounds(
                    monitorInfo.Monitor.Left,
                    monitorInfo.Monitor.Top,
                    width,
                    height),
                ReadScaleFactor(monitor),
                path.RefreshRateHz,
                (monitorInfo.Flags & DisplayNativeMethods.MonitorInfoPrimary) != 0));
            return true;
        };

        if (!DisplayNativeMethods.EnumDisplayMonitors(0, 0, callback, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumDisplayMonitors failed.");
        }

        return snapshots;
    }

    private static Dictionary<string, DisplayPathDetails> ReadActiveDisplayPaths()
    {
        uint flags = DisplayNativeMethods.QueryOnlyActivePaths |
            DisplayNativeMethods.QueryVirtualModeAware;

        for (int attempt = 0; attempt < MaximumDisplayConfigAttempts; attempt++)
        {
            int status = DisplayNativeMethods.GetDisplayConfigBufferSizes(
                flags,
                out uint pathCount,
                out uint modeCount);
            ThrowIfDisplayConfigFailed(status, "GetDisplayConfigBufferSizes");

            DisplayConfigPathInfo[] paths = new DisplayConfigPathInfo[pathCount];
            DisplayConfigModeInfo[] modes = new DisplayConfigModeInfo[modeCount];
            status = DisplayNativeMethods.QueryDisplayConfig(
                flags,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                0);
            if (status == DisplayNativeMethods.ErrorInsufficientBuffer)
            {
                continue;
            }

            ThrowIfDisplayConfigFailed(status, "QueryDisplayConfig");
            Dictionary<string, DisplayPathDetails> result = new(StringComparer.OrdinalIgnoreCase);
            foreach (DisplayConfigPathInfo path in paths.Take(checked((int)pathCount)))
            {
                DisplayConfigSourceDeviceName sourceName = DisplayConfigSourceDeviceName.Create(
                    path.SourceInfo.AdapterId,
                    path.SourceInfo.Id);
                status = DisplayNativeMethods.GetDisplayConfigSourceDeviceName(ref sourceName);
                ThrowIfDisplayConfigFailed(status, "DisplayConfigGetDeviceInfo(source)");

                DisplayConfigTargetDeviceName targetName = DisplayConfigTargetDeviceName.Create(
                    path.TargetInfo.AdapterId,
                    path.TargetInfo.Id);
                status = DisplayNativeMethods.GetDisplayConfigTargetDeviceName(ref targetName);
                ThrowIfDisplayConfigFailed(status, "DisplayConfigGetDeviceInfo(target)");

                string gdiName = sourceName.ViewGdiDeviceName.TrimEnd('\0');
                string devicePath = targetName.MonitorDevicePath.TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(gdiName) || string.IsNullOrWhiteSpace(devicePath))
                {
                    continue;
                }

                result.TryAdd(
                    gdiName,
                    new DisplayPathDetails(devicePath, ToRefreshRate(path.TargetInfo.RefreshRate)));
            }

            return result;
        }

        throw new Win32Exception(
            DisplayNativeMethods.ErrorInsufficientBuffer,
            "Display configuration changed repeatedly while it was being read.");
    }

    private static double ReadScaleFactor(nint monitor)
    {
        int status = DisplayNativeMethods.GetDpiForMonitor(
            monitor,
            DisplayNativeMethods.EffectiveDpi,
            out uint dpiX,
            out _);
        return status == DisplayNativeMethods.ErrorSuccess && dpiX > 0
            ? dpiX / DefaultDpi
            : 1;
    }

    private static int ToRefreshRate(DisplayConfigRational refreshRate)
    {
        if (refreshRate.Numerator == 0 || refreshRate.Denominator == 0)
        {
            return DefaultRefreshRateHz;
        }

        double value = (double)refreshRate.Numerator / refreshRate.Denominator;
        return Math.Max(1, checked((int)Math.Round(value, MidpointRounding.AwayFromZero)));
    }

    private static void ThrowIfDisplayConfigFailed(int status, string operation)
    {
        if (status != DisplayNativeMethods.ErrorSuccess)
        {
            throw new Win32Exception(status, $"{operation} failed.");
        }
    }

    private sealed record DisplayPathDetails(string DevicePath, int RefreshRateHz);
}
