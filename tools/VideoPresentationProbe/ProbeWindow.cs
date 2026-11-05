using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LiveWall.VideoPresentationProbe;

// Diagnostic-only child HWND, created/pumped/resized/destroyed on this STA thread.
// These Win32 declarations are not linked into a product Renderer.
internal sealed class ProbeWindow : IDisposable
{
    private readonly uint thread = Native.GetCurrentThreadId();
    private readonly string name = $"LiveWall.VideoProbe.{Guid.NewGuid():N}";
    private readonly Native.WindowProc procedure = Native.DefWindowProc;
    private readonly nint instance = Native.GetModuleHandle(null);
    private bool registered;
    private bool disposed;
    public nint Handle { get; private set; }

    public static ProbeWindow CreateHiddenHost(int width, int height) => new(0, width, height, hiddenHost: true);
    public ProbeWindow(ulong parent, int width, int height) : this(parent, width, height, hiddenHost: false) { }
    private ProbeWindow(ulong parent, int width, int height, bool hiddenHost)
    {
        if (!hiddenHost && !Native.IsWindow(unchecked((nint)(long)parent))) throw new ArgumentException("Host HWND is invalid.", nameof(parent));
        Native.WindowClass windowClass = new() { Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
            Procedure = procedure, Instance = instance, ClassName = name };
        try
        {
            if (Native.RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            registered = true;
            Handle = Native.CreateWindowEx(0x08000080, name, "LiveWall libmpv probe", hiddenHost ? 0x80000000 : 0x4E000000,
                0, 0, width, height, unchecked((nint)(long)parent), 0, instance, 0);
            if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!hiddenHost) Resize(width, height);
            if (!hiddenHost && Native.GetParent(Handle) != unchecked((nint)(long)parent)) throw new InvalidOperationException("Child parent mismatch.");
        }
        catch { Dispose(); throw; }
    }

    public void Resize(int width, int height)
    {
        CheckThread();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!Native.SetWindowPos(Handle, 0, 0, 0, width, height, 0x50)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Pump()
    {
        CheckThread();
        while (Native.PeekMessage(out Native.Message message, 0, 0, 0, 1))
        {
            Native.TranslateMessage(ref message); Native.DispatchMessage(ref message);
        }
    }
    public ProbeWindowGeometry ReadGeometry()
    {
        CheckThread();
        nint parent = Native.GetParent(Handle);
        if (!Native.GetClientRect(parent, out Native.Rect host) || !Native.GetClientRect(Handle, out Native.Rect child))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "C0 client geometry sampling failed.");
        return new(host.Right - host.Left, host.Bottom - host.Top, child.Right - child.Left, child.Bottom - child.Top);
    }
    public static bool WindowExists(ulong window) => Native.IsWindow(unchecked((nint)(long)window));
    public static ulong FirstChild(ulong parent) => unchecked((ulong)Native.GetWindow(unchecked((nint)(long)parent), 5).ToInt64());
    public static bool ChildMatches(ulong window, ulong parent, int rendererPid, int width, int height)
    {
        nint child = unchecked((nint)(long)window);
        _ = Native.GetWindowThreadProcessId(child, out uint pid);
        return Native.IsWindow(child) && pid == rendererPid &&
            Native.GetParent(child) == unchecked((nint)(long)parent) && Native.GetClientRect(child, out Native.Rect bounds) &&
            bounds.Right - bounds.Left == width && bounds.Bottom - bounds.Top == height;
    }
    public void Dispose()
    {
        CheckThread();
        if (disposed) return;
        disposed = true;
        if (Handle != 0 && Native.IsWindow(Handle) && !Native.DestroyWindow(Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Handle = 0;
        if (registered && !Native.UnregisterClass(name, instance)) throw new Win32Exception(Marshal.GetLastWin32Error());
        registered = false;
    }
    private void CheckThread()
    {
        if (Native.GetCurrentThreadId() != thread) throw new InvalidOperationException("Window ownership thread mismatch.");
    }

    private static class Native
    {
        [DllImport("user32.dll")] public static extern nint GetWindow(nint hwnd, uint command);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WindowClass
        {
            public uint Size; public uint Style; public WindowProc Procedure; public int ClassExtra; public int WindowExtra;
            public nint Instance; public nint Icon; public nint Cursor; public nint Background; public string? Menu; public string ClassName; public nint SmallIcon;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct Message { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X; public int Y; public uint Private; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WindowClass windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool UnregisterClass(string name, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] public static extern nint GetParent(nint hwnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetClientRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PeekMessage(out Message message, nint hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint DispatchMessage(ref Message message);
    }
}

internal sealed record ProbeWindowGeometry(int HostWidth, int HostHeight, int ChildWidth, int ChildHeight)
{
    public bool ChildFillsHost => HostWidth > 0 && HostHeight > 0 && HostWidth == ChildWidth && HostHeight == ChildHeight;
}
