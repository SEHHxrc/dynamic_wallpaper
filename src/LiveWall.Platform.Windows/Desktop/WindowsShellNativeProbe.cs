using System.ComponentModel;
using System.Diagnostics;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

internal interface IWindowsShellNativeProbe
{
    ShellExecutionContext CaptureExecutionContext();

    nint GetShellWindow();

    IReadOnlyList<nint> EnumerateTopLevelWindows();

    IReadOnlyList<nint> EnumerateDescendantWindows(nint parent);

    bool IsWindow(nint window);

    string? GetClassName(nint window);

    uint GetWindowThreadProcessId(nint window, out uint processId);

    string? GetProcessName(uint processId);

    ShellWindowBounds? GetBounds(nint window);

    bool IsWindowVisible(nint window);

    nint GetParent(nint window);

    nint GetOwner(nint window);

    ulong GetStyle(nint window);

    ulong GetExtendedStyle(nint window);

    nint GetTopWindow(nint parent);

    nint GetNextWindow(nint window);

    nint FindDirectChild(nint parent, string className);

    nint FindNextTopLevelWindow(nint window, string className);
}

internal sealed class WindowsShellNativeProbe : IWindowsShellNativeProbe
{
    private const int UserObjectNameCapacity = 256;

    public ShellExecutionContext CaptureExecutionContext()
    {
        uint? sessionId = DesktopNativeMethods.ProcessIdToSessionId(
            checked((uint)Environment.ProcessId),
            out uint nativeSessionId)
            ? nativeSessionId
            : null;
        nint windowStation = DesktopNativeMethods.GetProcessWindowStation();
        nint desktop = DesktopNativeMethods.GetThreadDesktop(
            DesktopNativeMethods.GetCurrentThreadId());
        return new ShellExecutionContext(
            sessionId,
            ReadUserObjectName(windowStation),
            ReadUserObjectName(desktop));
    }

    public nint GetShellWindow() => DesktopNativeMethods.GetShellWindow();

    public IReadOnlyList<nint> EnumerateTopLevelWindows()
    {
        List<nint> windows = [];
        DesktopNativeMethods.EnumWindowsProcedure callback = (window, callbackData) =>
        {
            _ = callbackData;
            windows.Add(window);
            return true;
        };
        if (!DesktopNativeMethods.EnumWindows(callback, 0))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(
                    error,
                    "EnumWindows failed while capturing Shell topology.");
            }
        }

        return windows;
    }

    public IReadOnlyList<nint> EnumerateDescendantWindows(nint parent)
    {
        List<nint> windows = [];
        DesktopNativeMethods.EnumWindowsProcedure callback = (window, callbackData) =>
        {
            _ = callbackData;
            windows.Add(window);
            return true;
        };
        _ = DesktopNativeMethods.EnumChildWindows(parent, callback, 0);
        return windows;
    }

    public bool IsWindow(nint window) => DesktopNativeMethods.IsWindow(window);

    public string? GetClassName(nint window)
    {
        char[] className = new char[256];
        int length = DesktopNativeMethods.GetClassName(
            window,
            className,
            className.Length);
        return length == 0 ? null : new string(className, 0, length);
    }

    public uint GetWindowThreadProcessId(nint window, out uint processId) =>
        DesktopNativeMethods.GetWindowThreadProcessId(window, out processId);

    public string? GetProcessName(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public ShellWindowBounds? GetBounds(nint window)
    {
        return DesktopNativeMethods.GetWindowRect(window, out NativeRect rectangle)
            ? new ShellWindowBounds(
                rectangle.Left,
                rectangle.Top,
                rectangle.Right - rectangle.Left,
                rectangle.Bottom - rectangle.Top)
            : null;
    }

    public bool IsWindowVisible(nint window) =>
        DesktopNativeMethods.IsWindowVisible(window);

    public nint GetParent(nint window) => DesktopNativeMethods.GetParent(window);

    public nint GetOwner(nint window) => DesktopNativeMethods.GetWindow(
        window,
        DesktopNativeMethods.GetWindowOwner);

    public ulong GetStyle(nint window) => unchecked(
        (ulong)DesktopNativeMethods.GetWindowLongPtr(
            window,
            DesktopNativeMethods.WindowLongStyle).ToInt64());

    public ulong GetExtendedStyle(nint window) => unchecked(
        (ulong)DesktopNativeMethods.GetWindowLongPtr(
            window,
            DesktopNativeMethods.WindowLongExtendedStyle).ToInt64());

    public nint GetTopWindow(nint parent) => DesktopNativeMethods.GetTopWindow(parent);

    public nint GetNextWindow(nint window) => DesktopNativeMethods.GetWindow(
        window,
        DesktopNativeMethods.GetWindowNext);

    public nint FindDirectChild(nint parent, string className) =>
        DesktopNativeMethods.FindWindowEx(parent, 0, className, null);

    public nint FindNextTopLevelWindow(nint window, string className) =>
        DesktopNativeMethods.FindWindowEx(0, window, className, null);

    private static string? ReadUserObjectName(nint userObject)
    {
        if (userObject == 0)
        {
            return null;
        }

        char[] name = new char[UserObjectNameCapacity];
        if (!DesktopNativeMethods.GetUserObjectInformation(
                userObject,
                DesktopNativeMethods.UserObjectName,
                name,
                checked((uint)(name.Length * sizeof(char))),
                out uint lengthNeeded))
        {
            return null;
        }

        int characterCount = Math.Min(
            name.Length,
            checked((int)(lengthNeeded / sizeof(char))));
        if (characterCount > 0 && name[characterCount - 1] == '\0')
        {
            characterCount--;
        }

        return new string(name, 0, characterCount);
    }
}
