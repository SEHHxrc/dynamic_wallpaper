using System.Runtime.InteropServices;

namespace LiveWall.Platform.Windows.NativeMethods;

internal static class SystemStateNativeMethods
{
    internal const uint WindowMessagePowerBroadcast = 0x0218;
    internal const uint WindowMessageSessionChange = 0x02B1;
    internal const nuint PowerBroadcastSuspend = 0x0004;
    internal const nuint PowerBroadcastResumeSuspend = 0x0007;
    internal const nuint PowerBroadcastResumeAutomatic = 0x0012;
    internal const nuint PowerBroadcastSettingChange = 0x8013;
    internal const nuint SessionConsoleConnect = 0x1;
    internal const nuint SessionConsoleDisconnect = 0x2;
    internal const nuint SessionRemoteConnect = 0x3;
    internal const nuint SessionRemoteDisconnect = 0x4;
    internal const nuint SessionLock = 0x7;
    internal const nuint SessionUnlock = 0x8;
    internal const uint NotifyForThisSession = 0;
    internal const uint DeviceNotifyWindowHandle = 0;
    internal const int SystemMetricRemoteSession = 0x1000;
    internal const int WtsCurrentSession = -1;
    internal const int WtsConnectState = 8;
    internal const uint DesktopReadObjects = 0x0001;

    internal static readonly Guid ConsoleDisplayState =
        new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    internal static readonly Guid AcDcPowerSource =
        new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSRegisterSessionNotification(nint window, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSUnRegisterSessionNotification(nint window);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQuerySessionInformation(
        nint server,
        int sessionId,
        int informationClass,
        out nint buffer,
        out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint RegisterPowerSettingNotification(
        nint recipient,
        ref Guid powerSettingGuid,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterPowerSettingNotification(nint handle);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint OpenInputDesktop(
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(nint desktop);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out NativeSystemPowerStatus status);

    internal static bool TryReadPowerSetting(
        nint parameter,
        out Guid setting,
        out uint value)
    {
        setting = default;
        value = 0;
        if (parameter == 0)
        {
            return false;
        }

        setting = Marshal.PtrToStructure<Guid>(parameter);
        uint dataLength = unchecked((uint)Marshal.ReadInt32(parameter, 16));
        if (dataLength < sizeof(uint))
        {
            return false;
        }

        value = unchecked((uint)Marshal.ReadInt32(parameter, 20));
        return true;
    }

    internal static bool IsDefaultInputDesktop()
    {
        nint desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == 0)
        {
            return false;
        }

        try
        {
            char[] name = new char[256];
            if (!DesktopNativeMethods.GetUserObjectInformation(
                    desktop,
                    DesktopNativeMethods.UserObjectName,
                    name,
                    checked((uint)(name.Length * sizeof(char))),
                    out _))
            {
                return false;
            }

            string value = new(name, 0, Array.IndexOf(name, '\0') is int end and >= 0
                ? end
                : name.Length);
            return string.Equals(value, "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _ = CloseDesktop(desktop);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSystemPowerStatus
{
    internal byte AcLineStatus;
    internal byte BatteryFlag;
    internal byte BatteryLifePercent;
    internal byte SystemStatusFlag;
    internal uint BatteryLifeTime;
    internal uint BatteryFullLifeTime;
}
