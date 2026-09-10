using System.Runtime.InteropServices;

namespace LiveWall.Platform.Windows.NativeMethods;

internal static class DisplayNativeMethods
{
    internal const uint QueryOnlyActivePaths = 0x00000002;
    internal const uint QueryVirtualModeAware = 0x00000010;
    internal const int ErrorSuccess = 0;
    internal const int ErrorInsufficientBuffer = 122;
    internal const uint MonitorInfoPrimary = 0x00000001;
    internal const uint DisplayConfigGetSourceName = 1;
    internal const uint DisplayConfigGetTargetName = 2;
    internal const int EffectiveDpi = 0;

    internal delegate bool MonitorEnumProcedure(
        nint monitor,
        nint monitorDeviceContext,
        ref NativeRect monitorBounds,
        nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumProcedure callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint pathCount,
        out uint modeCount);

    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [In, Out] DisplayConfigPathInfo[] paths,
        ref uint modeCount,
        [In, Out] DisplayConfigModeInfo[] modes,
        nint currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int GetDisplayConfigSourceDeviceName(
        ref DisplayConfigSourceDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int GetDisplayConfigTargetDeviceName(
        ref DisplayConfigTargetDeviceName request);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MonitorInfoEx
{
    internal uint Size;
    internal NativeRect Monitor;
    internal NativeRect WorkArea;
    internal uint Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string DeviceName;

    internal static MonitorInfoEx Create() => new()
    {
        Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
        DeviceName = string.Empty,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLuid
{
    internal uint LowPart;
    internal int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigRational
{
    internal uint Numerator;
    internal uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathSourceInfo
{
    internal NativeLuid AdapterId;
    internal uint Id;
    internal uint ModeInfoIndex;
    internal uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathTargetInfo
{
    internal NativeLuid AdapterId;
    internal uint Id;
    internal uint ModeInfoIndex;
    internal uint OutputTechnology;
    internal uint Rotation;
    internal uint Scaling;
    internal DisplayConfigRational RefreshRate;
    internal uint ScanLineOrdering;

    [MarshalAs(UnmanagedType.Bool)]
    internal bool TargetAvailable;

    internal uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathInfo
{
    internal DisplayConfigPathSourceInfo SourceInfo;
    internal DisplayConfigPathTargetInfo TargetInfo;
    internal uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfig2DRegion
{
    internal uint Width;
    internal uint Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigVideoSignalInfo
{
    internal ulong PixelRate;
    internal DisplayConfigRational HorizontalSyncFrequency;
    internal DisplayConfigRational VerticalSyncFrequency;
    internal DisplayConfig2DRegion ActiveSize;
    internal DisplayConfig2DRegion TotalSize;
    internal uint VideoStandard;
    internal uint ScanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigTargetMode
{
    internal DisplayConfigVideoSignalInfo TargetVideoSignalInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    internal int X;
    internal int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigSourceMode
{
    internal uint Width;
    internal uint Height;
    internal uint PixelFormat;
    internal NativePoint Position;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigDesktopImageInfo
{
    internal NativePoint PathSourceSize;
    internal NativeRect DesktopImageRegion;
    internal NativeRect DesktopImageClip;
}

[StructLayout(LayoutKind.Explicit)]
internal struct DisplayConfigModeInfoUnion
{
    [FieldOffset(0)]
    internal DisplayConfigTargetMode TargetMode;

    [FieldOffset(0)]
    internal DisplayConfigSourceMode SourceMode;

    [FieldOffset(0)]
    internal DisplayConfigDesktopImageInfo DesktopImageInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigModeInfo
{
    internal uint InfoType;
    internal uint Id;
    internal NativeLuid AdapterId;
    internal DisplayConfigModeInfoUnion ModeInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigDeviceInfoHeader
{
    internal uint Type;
    internal uint Size;
    internal NativeLuid AdapterId;
    internal uint Id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayConfigSourceDeviceName
{
    internal DisplayConfigDeviceInfoHeader Header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string ViewGdiDeviceName;

    internal static DisplayConfigSourceDeviceName Create(
        NativeLuid adapterId,
        uint sourceId) => new()
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = DisplayNativeMethods.DisplayConfigGetSourceName,
                Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                AdapterId = adapterId,
                Id = sourceId,
            },
            ViewGdiDeviceName = string.Empty,
        };
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayConfigTargetDeviceName
{
    internal DisplayConfigDeviceInfoHeader Header;
    internal uint Flags;
    internal uint OutputTechnology;
    internal ushort EdidManufactureId;
    internal ushort EdidProductCodeId;
    internal uint ConnectorInstance;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    internal string MonitorFriendlyDeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    internal string MonitorDevicePath;

    internal static DisplayConfigTargetDeviceName Create(
        NativeLuid adapterId,
        uint targetId) => new()
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = DisplayNativeMethods.DisplayConfigGetTargetName,
                Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                AdapterId = adapterId,
                Id = targetId,
            },
            MonitorFriendlyDeviceName = string.Empty,
            MonitorDevicePath = string.Empty,
        };
}
