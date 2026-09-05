using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

public static class WindowsShellTopologyDiagnostics
{
    public static IReadOnlyList<ShellWindowFingerprint> Capture()
    {
        List<ShellWindowFingerprint> windows = [];
        DesktopNativeMethods.EnumWindowsProcedure callback = (window, _) =>
        {
            string className = ReadClassName(window);
            if (className is not ("Progman" or "WorkerW"))
            {
                return true;
            }

            _ = DesktopNativeMethods.GetWindowThreadProcessId(window, out uint processId);
            nint defView = DesktopNativeMethods.FindWindowEx(
                window,
                0,
                "SHELLDLL_DefView",
                null);
            nint nextWorker = DesktopNativeMethods.FindWindowEx(
                0,
                window,
                "WorkerW",
                null);
            windows.Add(new ShellWindowFingerprint(
                unchecked((ulong)window.ToInt64()),
                className,
                processId,
                defView != 0,
                unchecked((ulong)nextWorker.ToInt64())));
            return true;
        };

        if (!DesktopNativeMethods.EnumWindows(callback, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(error, "EnumWindows failed while capturing Shell topology.");
            }
        }

        return windows;
    }

    private static string ReadClassName(nint window)
    {
        StringBuilder className = new(256);
        int length = DesktopNativeMethods.GetClassName(window, className, className.Capacity);
        if (length == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(error, "GetClassName failed while capturing Shell topology.");
            }
        }

        return className.ToString();
    }
}

public sealed record ShellWindowFingerprint(
    ulong WindowHandle,
    string ClassName,
    uint ProcessId,
    bool HostsShellDefView,
    ulong NextWorkerWindowHandle);
