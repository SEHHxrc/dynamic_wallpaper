using System.Runtime.InteropServices;

namespace LiveWall.Renderer.Video.Mpv;

// ABI from the pinned mpv render.h/render_d3d11.h. Device/texture pointers never
// cross a process boundary. All methods and Dispose belong to one render thread.
internal sealed class MpvD3D11RenderContext : IDisposable
{
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private readonly Action released;
    private readonly Update update;
    private readonly GetInfo getInfo;
    private readonly Render render;
    private readonly Release release;
    private readonly ReportSwap reportSwap;
    private readonly SetUpdateCallback setUpdateCallback;
    private UpdateCallback? callback;
    private nint context;

    public MpvD3D11RenderContext(nint library, nint client, nint device, Action released)
    {
        ArgumentOutOfRangeException.ThrowIfZero(device);
        this.released = released;
        update = Export<Update>(library, "mpv_render_context_update");
        getInfo = Export<GetInfo>(library, "mpv_render_context_get_info");
        render = Export<Render>(library, "mpv_render_context_render");
        release = Export<Release>(library, "mpv_render_context_free");
        reportSwap = Export<ReportSwap>(library, "mpv_render_context_report_swap");
        setUpdateCallback = Export<SetUpdateCallback>(library, "mpv_render_context_set_update_callback");
        nint api = Marshal.StringToCoTaskMemUTF8("d3d11");
        nint init = Marshal.AllocHGlobal(nint.Size);
        try
        {
            Marshal.WriteIntPtr(init, device);
            Check(Export<Create>(library, "mpv_render_context_create")(out context, client,
                [new(1, api), new(21, init), default]), "create D3D11 render context");
            if (context == 0) throw new InvalidOperationException("mpv returned an empty render context.");
        }
        catch
        {
            if (context != 0) release(context);
            context = 0;
            throw;
        }
        finally { Marshal.FreeCoTaskMem(api); Marshal.FreeHGlobal(init); }
    }

    public bool TryGetNextFrame(out bool isNewVideo)
    {
        CheckThread();
        isNewVideo = false;
        if ((update(context) & 1) == 0) return false;
        nint info = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(info, 0); Marshal.WriteInt64(info, 8, 0);
            Check(getInfo(context, new(11, info)), "read next-frame info");
            ulong flags = unchecked((ulong)Marshal.ReadInt64(info));
            isNewVideo = (flags & 1) != 0 && (flags & 6) == 0;
            return (flags & 1) != 0; // Redraws must still be serviced, but cannot prove new content.
        }
        finally { Marshal.FreeHGlobal(info); }
    }
    public int RenderToTexture(nint texture)
    {
        CheckThread();
        ArgumentOutOfRangeException.ThrowIfZero(texture);
        nint fbo = Marshal.AllocHGlobal(nint.Size);
        try
        {
            Marshal.WriteIntPtr(fbo, texture);
            int result = render(context, [new(22, fbo), default]);
            Check(result, "render D3D11 texture");
            return result;
        }
        finally { Marshal.FreeHGlobal(fbo); }
    }
    public void ReportPresented()
    {
        CheckThread(); reportSwap(context);
    }
    public void SetWakeup(Action wake)
    {
        CheckThread();
        callback = _ => wake(); // Callback only signals; it never invokes mpv or waits on control.
        setUpdateCallback(context, callback, 0);
    }
    public void Dispose()
    {
        if (context == 0) return;
        CheckThread();
        setUpdateCallback(context, null, 0);
        release(context); context = 0; callback = null; released();
    }
    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(context == 0, this);
        if (Environment.CurrentManagedThreadId != threadId) throw new InvalidOperationException("mpv render-thread ownership mismatch.");
    }
    private static T Export<T>(nint library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    private static void Check(int result, string operation)
    {
        if (result < 0) throw new InvalidOperationException($"libmpv {operation} failed: {result}. No fallback presentation proof is permitted.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Parameter(int type, nint data) { public readonly int Type = type; public readonly nint Data = data; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Create(out nint context, nint client, [In] Parameter[] parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Update(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetInfo(nint context, Parameter parameter);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Render(nint context, [In] Parameter[] parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Release(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReportSwap(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void UpdateCallback(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetUpdateCallback(nint context, UpdateCallback? callback, nint callbackContext);
}
