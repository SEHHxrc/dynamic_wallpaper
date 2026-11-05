using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace LiveWall.Renderer.Video.Mpv;

// Absolute, hash-checked dynamic loading keeps this backend private to the Renderer.
// No Platform.Windows/Host reference and no arbitrary mpv command API is exposed.
internal sealed class MpvClient : IDisposable
{
    private readonly nint library;
    private readonly MpvHandle handle;
    private readonly SetString option;
    private readonly SetString setProperty;
    private readonly GetString getProperty;
    private readonly Command command;
    private readonly WaitEvent waitEvent;
    private readonly Free free;
    private bool disposed;
    private bool fileLoaded;
    private bool restarted;
    private int restarts;
    private int renderContextActive;
    private bool usesRenderApi;
    private readonly List<string> diagnosticMessages = [];

    public MpvClient(string libraryPath, string expectedSha256)
    {
        ValidateLibrary(libraryPath, expectedSha256);
        library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        try
        {
            ulong api = Export<ApiVersion>("mpv_client_api_version")();
            if ((api >> 16) != 2) throw new NotSupportedException("Expected libmpv client API major 2.");
            ClientApiVersion = api;
            option = Export<SetString>("mpv_set_option_string");
            setProperty = Export<SetString>("mpv_set_property_string");
            getProperty = Export<GetString>("mpv_get_property_string");
            command = Export<Command>("mpv_command");
            waitEvent = Export<WaitEvent>("mpv_wait_event");
            free = Export<Free>("mpv_free");
            handle = new MpvHandle(Export<Create>("mpv_create")(), Export<Destroy>("mpv_terminate_destroy"));
            if (handle.IsInvalid) throw new InvalidOperationException("mpv_create failed.");
        }
        catch { NativeLibrary.Free(library); throw; }
    }

    public ulong ClientApiVersion { get; }
    public IReadOnlyList<string> DiagnosticMessages => diagnosticMessages.ToArray();
    public void EnableDiagnosticMessages() => Check(Export<RequestLog>("mpv_request_log_messages")(
        handle.DangerousGetHandle(), "warn"), "enable bounded diagnostic messages");

    public static void ValidateLibrary(string path, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute libmpv path.", nameof(path));
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A pinned SHA-256 is required.", nameof(expectedSha256));
        using FileStream file = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(file));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("libmpv SHA-256 mismatch; refusing to load native code.");
    }

    public void Initialize(ulong childWindow, bool headless = false, bool renderApi = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (headless && renderApi) throw new ArgumentException("Render API and null VO are distinct backends.");
        SetOption("config", "no");
        SetOption("load-scripts", "no");
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("osc", "no");
        SetOption("terminal", "no");
        SetOption("idle", "yes");
        SetOption("audio", renderApi ? "auto" : "no");
        if (renderApi) { SetOption("volume", "0"); SetOption("mute", "yes"); }
        SetOption("loop-file", "inf");
        SetOption("keep-open", "yes");
        // The C0 native fixture declares fit=cover. Preserve aspect and crop to fill;
        // do not enlarge the HWND or stretch pixels to compensate for letterboxing.
        SetOption("keepaspect", "yes");
        SetOption("keepaspect-window", "no");
        SetOption("video-unscaled", "no");
        SetOption("panscan", "1");
        SetOption("vo", renderApi ? "libmpv" : headless ? "null" : "gpu-next");
        if (!headless && !renderApi)
        {
            ArgumentOutOfRangeException.ThrowIfZero(childWindow);
            SetOption("wid", childWindow.ToString(CultureInfo.InvariantCulture));
            SetOption("gpu-api", "d3d11");
            SetOption("gpu-context", "d3d11");
            SetOption("hwdec", "auto-safe");
        }
        Check(Export<InitializeClient>("mpv_initialize")(handle.DangerousGetHandle()), "initialize");
        usesRenderApi = renderApi;
    }

    public MpvD3D11RenderContext CreateD3D11RenderContext(nint device)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!usesRenderApi || Interlocked.CompareExchange(ref renderContextActive, 1, 0) != 0)
            throw new InvalidOperationException("Initialize the render API and create only one context.");
        try { return new(library, handle.DangerousGetHandle(), device, () => Interlocked.Exchange(ref renderContextActive, 0)); }
        catch { Interlocked.Exchange(ref renderContextActive, 0); throw; }
    }

    public void LoadLocalVideo(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(fullPath))
            throw new ArgumentException("A local existing video is required.", nameof(path));
        string[] arguments = ["loadfile", fullPath, "replace"];
        nint[] strings = arguments.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        nint vector = Marshal.AllocHGlobal((strings.Length + 1) * nint.Size);
        try
        {
            for (int i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(vector, i * nint.Size, strings[i]);
            Marshal.WriteIntPtr(vector, strings.Length * nint.Size, 0);
            Check(command(handle.DangerousGetHandle(), vector), "loadfile");
        }
        finally
        {
            Marshal.FreeHGlobal(vector);
            foreach (nint value in strings) Marshal.FreeCoTaskMem(value);
        }
    }

    public void Pause(bool paused) => Check(setProperty(handle.DangerousGetHandle(), "pause", paused ? "yes" : "no"), "pause");

    public void SetFit(string fit)
    {
        if (fit is not ("Cover" or "Contain" or "Stretch" or "Center")) throw new ArgumentException("Unsupported Video fit.", nameof(fit));
        Check(setProperty(handle.DangerousGetHandle(), "keepaspect", fit == "Stretch" ? "no" : "yes"), "fit aspect");
        Check(setProperty(handle.DangerousGetHandle(), "video-unscaled", fit == "Center" ? "yes" : "no"), "fit scaling");
        Check(setProperty(handle.DangerousGetHandle(), "panscan", fit == "Cover" ? "1" : "0"), "fit crop");
    }
    public void SetVolume(double volume, bool muted)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(volume));
        Check(setProperty(handle.DangerousGetHandle(), "volume", (volume * 100).ToString(CultureInfo.InvariantCulture)), "volume");
        Check(setProperty(handle.DangerousGetHandle(), "mute", muted ? "yes" : "no"), "mute");
    }

    public string? ReadProperty(string name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        nint value = getProperty(handle.DangerousGetHandle(), name);
        if (value == 0) return null;
        try { return Marshal.PtrToStringUTF8(value); }
        finally { free(value); }
    }

    public MpvOutputEvidence Poll()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        for (int i = 0; i < 128; i++)
        {
            nint pointer = waitEvent(handle.DangerousGetHandle(), 0);
            if (pointer == 0) throw new InvalidOperationException("mpv_wait_event returned null.");
            NativeEvent evt = Marshal.PtrToStructure<NativeEvent>(pointer);
            if (evt.Id == 0) break;
            if (evt.Id == 8) fileLoaded = true;
            if (evt.Id == 2 && evt.Data != 0)
            {
                string message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(evt.Data, nint.Size * 2)) ?? "";
                if (diagnosticMessages.Count == 32) diagnosticMessages.RemoveAt(0);
                diagnosticMessages.Add(message.Length > 512 ? message[..512] : message.Trim());
            }
            if (evt.Id == 21) { restarted = true; restarts++; }
            if (evt.Id is 1 or 24 || evt.Error < 0) throw new InvalidOperationException($"mpv event failure: {evt.Id}/{evt.Error}.");
            if (evt.Id == 7 && evt.Data != 0)
            {
                int reason = Marshal.ReadInt32(evt.Data);
                if (reason == 4) throw new InvalidOperationException($"mpv media playback error: {Marshal.ReadInt32(evt.Data, 4)}.");
            }
        }
        double? position = double.TryParse(ReadProperty("time-pos"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double seconds) ? seconds : null;
        return new(fileLoaded, restarted, ReadProperty("vo-configured") == "yes", position,
            ReadProperty("current-vo"), ReadProperty("gpu-api"), restarts,
            new MpvViewportEvidence(ReadInteger("osd-dimensions/w"), ReadInteger("osd-dimensions/h"),
                ReadNumber("osd-dimensions/ml"), ReadNumber("osd-dimensions/mr"),
                ReadNumber("osd-dimensions/mt"), ReadNumber("osd-dimensions/mb"),
                ReadProperty("keepaspect"), ReadNumber("panscan")));
    }

    private int? ReadInteger(string name) => int.TryParse(ReadProperty(name), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out int value) ? value : null;
    private double? ReadNumber(string name) => double.TryParse(ReadProperty(name), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;

    public void Dispose()
    {
        if (disposed) return;
        if (Volatile.Read(ref renderContextActive) != 0)
            throw new InvalidOperationException("Free the mpv render context on its owner thread before disposing the client.");
        disposed = true;
        handle.Dispose(); // mpv must finish using the window before its owner destroys it.
        NativeLibrary.Free(library);
    }

    private void SetOption(string name, string value) => Check(option(handle.DangerousGetHandle(), name, value), name);
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    private static void Check(int code, string operation)
    {
        if (code < 0) throw new InvalidOperationException($"libmpv {operation} failed: {code}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeEvent { public readonly int Id; public readonly int Error; public readonly ulong UserData; public readonly nint Data; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong ApiVersion();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Create();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeClient(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetString(nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetString(nint context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Command(nint context, nint arguments);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint WaitEvent(nint context, double timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(nint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RequestLog(nint context, [MarshalAs(UnmanagedType.LPUTF8Str)] string level);

    private sealed class MpvHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly Destroy destroy;
        public MpvHandle(nint value, Destroy destroy) : base(true)
        {
            this.destroy = destroy;
            SetHandle(value);
        }
        public override bool IsInvalid => handle == 0;
        protected override bool ReleaseHandle() { destroy(handle); return true; }
    }
}
