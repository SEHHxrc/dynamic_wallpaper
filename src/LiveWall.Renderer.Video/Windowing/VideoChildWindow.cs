using System.ComponentModel;
using System.Runtime.InteropServices;
using Native = LiveWall.Renderer.Video.NativeMethods.VideoNativeMethods;

namespace LiveWall.Renderer.Video.Windowing;

internal sealed class VideoChildWindow : IDisposable
{
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly string name = $"LiveWall.Renderer.Video.{Guid.NewGuid():N}";
    private readonly Native.WindowProc procedure = WindowProcedure;
    private readonly nint instance = Native.GetModuleHandle(null);
    private readonly uint hostProcess;
    private readonly nint parent;
    private bool registered;
    public nint Handle { get; private set; }

    public VideoChildWindow(ulong host, uint hostProcess, int width, int height)
    {
        this.hostProcess = hostProcess;
        parent = unchecked((nint)(long)host);
        if (!ValidParent()) throw new InvalidDataException("Surface is not owned by the connected Host.");
        Native.WindowClass definition = new() { Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
            Procedure = procedure, Instance = instance, ClassName = name };
        try
        {
            if (Native.RegisterClassEx(ref definition) == 0) throw Error();
            registered = true;
            // CHILD | DISABLED | CLIPCHILDREN | CLIPSIBLINGS, never a top-level wallpaper.
            // NOACTIVATE | TOOLWINDOW | NOREDIRECTIONBITMAP for Renderer-owned DComp.
            Handle = Native.CreateWindowEx(0x08200080, name, "LiveWall Video", 0x4E000000,
                0, 0, width, height, parent, 0, instance, 0);
            if (Handle == 0 || !IsValid) throw Error();
            Resize(width, height);
        }
        catch { Dispose(); throw; }
    }

    public bool IsValid => ValidParent() && OwnedChild() && Native.GetParent(Handle) == parent;
    public bool MatchesSize(int width, int height) => IsValid && Native.GetClientRect(Handle, out Native.Rect bounds) &&
        bounds.Right - bounds.Left == width && bounds.Bottom - bounds.Top == height;
    public void Resize(int width, int height)
    {
        CheckThread();
        if (!IsValid || !Native.SetWindowPos(Handle, 0, 0, 0, width, height, 0x50)) throw Error();
    }
    public static void Pump()
    {
        while (Native.PeekMessage(out Native.Message message, 0, 0, 0, 1))
        {
            Native.TranslateMessage(ref message); Native.DispatchMessage(ref message);
        }
    }
    public void Dispose()
    {
        CheckThread();
        // Parent loss can already have destroyed the child. Never destroy a reused HWND.
        if (Handle != 0 && OwnedChild() && !Native.DestroyWindow(Handle)) throw Error();
        Handle = 0;
        if (registered && !Native.UnregisterClass(name, instance)) throw Error();
        registered = false;
    }
    private bool ValidParent()
    {
        if (!Native.IsWindow(parent)) return false;
        _ = Native.GetWindowThreadProcessId(parent, out uint pid);
        return pid != 0 && pid == hostProcess;
    }
    private bool OwnedChild()
    {
        if (!Native.IsWindow(Handle)) return false;
        _ = Native.GetWindowThreadProcessId(Handle, out uint pid);
        char[] value = new char[160];
        int length = Native.GetClassName(Handle, value, value.Length);
        return pid == Environment.ProcessId && length > 0 && new string(value, 0, length) == name;
    }
    private void CheckThread()
    {
        if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Video window thread mismatch.");
    }
    private static nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam) =>
        message == 0x84 ? -1 : message == 0x21 ? 3 : Native.DefWindowProc(hwnd, message, wParam, lParam);
    private static Win32Exception Error() => new(Marshal.GetLastWin32Error(), "Video child operation failed.");
}
