using System.Runtime.InteropServices;

namespace LiveWall.Platform.Windows.NativeMethods;

internal static class DesktopNativeMethods
{
    internal const uint WorkerWMessage = 0x052C;
    internal const uint SendMessageTimeoutAbortIfHung = 0x0002;
    internal const uint WindowStyleChild = 0x40000000;
    internal const uint WindowStylePopup = 0x80000000;
    internal const uint WindowStyleCaption = 0x00C00000;
    internal const uint WindowStyleDisabled = 0x08000000;
    internal const uint WindowStyleVisible = 0x10000000;
    internal const uint WindowStyleClipChildren = 0x02000000;
    internal const uint WindowStyleClipSiblings = 0x04000000;
    internal const uint StaticStyleCenter = 0x00000001;
    internal const uint StaticStyleCenterImage = 0x00000200;
    internal const uint WindowExStyleNoActivate = 0x08000000;
    internal const uint WindowExStyleToolWindow = 0x00000080;
    internal const uint WindowExStyleLayered = 0x00080000;
    internal const uint WindowExStyleNoRedirectionBitmap = 0x00200000;
    internal const uint LayeredWindowAttributeAlpha = 0x00000002;
    internal const uint SetWindowPositionNoActivate = 0x0010;
    internal const uint SetWindowPositionShowWindow = 0x0040;
    internal const uint SetWindowPositionNoSize = 0x0001;
    internal const uint SetWindowPositionNoMove = 0x0002;
    internal const uint SetWindowPositionNoZOrder = 0x0004;
    internal const uint SetWindowPositionHideWindow = 0x0080;
    internal const uint WindowMessageDisplayChange = 0x007E;
    internal const uint WindowMessageDpiChanged = 0x02E0;
    internal const uint WindowMessageApplication = 0x8000;
    internal const int WindowLongStyle = -16;
    internal const int WindowLongExtendedStyle = -20;
    internal const uint GetWindowNext = 2;
    internal const uint GetWindowOwner = 4;
    internal const int UserObjectName = 2;
    internal const int ErrorClassAlreadyExists = 1410;
    internal static readonly nint WindowTop = 0;
    internal static readonly nint WindowBottom = 1;

    internal delegate bool EnumWindowsProcedure(nint window, nint data);

    internal delegate nint WindowProcedure(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowEx(
        nint parent,
        nint childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProcedure callback, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(
        nint parent,
        EnumWindowsProcedure callback,
        nint data);

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    internal static extern nint GetTopWindow(nint parent);

    [DllImport("user32.dll")]
    internal static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetThreadDesktop(uint threadId);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetUserObjectInformationW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformation(
        nint userObject,
        int index,
        [Out] char[] objectInformation,
        uint objectInformationLength,
        out uint lengthNeeded);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(
        nint window,
        [Out] char[] className,
        int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nuint wParam,
        nint lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint window, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetParent(nint child, nint newParent);

    [DllImport("user32.dll")]
    internal static extern nint GetParent(nint child);

    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(
        nint window,
        uint colorKey,
        byte alpha,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint BeginDeferWindowPos(int windowCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint DeferWindowPos(
        nint deferredWindowPosition,
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndDeferWindowPos(nint deferredWindowPosition);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint window, int index, nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("user32.dll")]
    internal static extern int FillRect(
        nint deviceContext,
        in NativeRect rectangle,
        nint brush);

    [DllImport("user32.dll")]
    internal static extern nint DefWindowProc(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessage(
        out NativeMessage message,
        nint window,
        uint messageFilterMinimum,
        uint messageFilterMaximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessage(in NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(
        uint processId,
        out uint sessionId);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern nint CreateSolidBrush(uint colorReference);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint graphicsObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GdiFlush();

    [DllImport("dwmapi.dll")]
    internal static extern int DwmFlush();
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMessage
{
    internal nint Window;
    internal uint Message;
    internal nuint WParam;
    internal nint LParam;
    internal uint Time;
    internal NativePoint Point;
    internal uint Private;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowClassEx
{
    internal uint Size;
    internal uint Style;
    internal DesktopNativeMethods.WindowProcedure WindowProcedure;
    internal int ClassExtraBytes;
    internal int WindowExtraBytes;
    internal nint Instance;
    internal nint Icon;
    internal nint Cursor;
    internal nint BackgroundBrush;
    internal string? MenuName;
    internal string ClassName;
    internal nint SmallIcon;
}
