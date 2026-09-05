using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

internal interface IDesktopSurfaceFactory : IAsyncDisposable
{
    IAsyncEnumerable<DesktopWindowSignal> ReadSignalsAsync(
        CancellationToken cancellationToken);

    Task<ulong> CreateAsync(
        SurfaceId id,
        SurfaceRequest request,
        DesktopAttachPoint attachPoint,
        CancellationToken cancellationToken);

    Task ReplaceAsync(
        IReadOnlyList<ulong> provisionalSurfaceHandles,
        IReadOnlyList<ulong> replacedSurfaceHandles,
        CancellationToken cancellationToken);

    Task DestroyAsync(ulong surfaceHandle, CancellationToken cancellationToken);
}

internal sealed class NativeDesktopSurfaceFactory : IDesktopSurfaceFactory
{
    private readonly string className = $"LiveWall.DesktopSurface.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly DesktopNativeMethods.WindowProcedure windowProcedure;
    private readonly IDesktopWindowDispatcher dispatcher;
    private readonly bool ownsDispatcher;
    private readonly Dictionary<nint, DesktopSurfaceWindowState> surfaces = [];
    private nint instance;
    private bool registered;
    private bool disposed;

    public NativeDesktopSurfaceFactory()
        : this(new DesktopWindowDispatcher(), ownsDispatcher: true)
    {
    }

    internal NativeDesktopSurfaceFactory(
        IDesktopWindowDispatcher dispatcher,
        bool ownsDispatcher = false)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.ownsDispatcher = ownsDispatcher;
        windowProcedure = WindowProcedure;
    }

    public IAsyncEnumerable<DesktopWindowSignal> ReadSignalsAsync(
        CancellationToken cancellationToken) =>
        dispatcher.ReadSignalsAsync(cancellationToken);

    public Task<ulong> CreateAsync(
        SurfaceId id,
        SurfaceRequest request,
        DesktopAttachPoint attachPoint,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return dispatcher.InvokeAsync(
            () => CreateOnDispatcher(id, request, attachPoint),
            cancellationToken);
    }

    public Task ReplaceAsync(
        IReadOnlyList<ulong> provisionalSurfaceHandles,
        IReadOnlyList<ulong> replacedSurfaceHandles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provisionalSurfaceHandles);
        ArgumentNullException.ThrowIfNull(replacedSurfaceHandles);
        ThrowIfDisposed();
        ulong[] provisional = provisionalSurfaceHandles.ToArray();
        ulong[] replaced = replacedSurfaceHandles.ToArray();
        return dispatcher.InvokeAsync(
            () => ReplaceOnDispatcher(provisional, replaced),
            cancellationToken);
    }

    public Task DestroyAsync(ulong surfaceHandle, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return dispatcher.InvokeAsync(
            () => DestroyOnDispatcher(ToNativeHandle(surfaceHandle)),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            await dispatcher.InvokeAsync(DisposeOnDispatcher, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ownsDispatcher)
            {
                await dispatcher.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal Task<bool> IsVisibleAsync(ulong surfaceHandle, CancellationToken cancellationToken) =>
        dispatcher.InvokeAsync(
            () => (unchecked((ulong)DesktopNativeMethods.GetWindowLongPtr(
                    ToNativeHandle(surfaceHandle),
                    DesktopNativeMethods.WindowLongStyle).ToInt64()) &
                DesktopNativeMethods.WindowStyleVisible) != 0,
            cancellationToken);

    internal Task<uint> GetWindowThreadIdAsync(
        ulong surfaceHandle,
        CancellationToken cancellationToken) =>
        dispatcher.InvokeAsync(
            () => DesktopNativeMethods.GetWindowThreadProcessId(
                ToNativeHandle(surfaceHandle),
                out _),
            cancellationToken);

    internal uint LastCreateThreadId { get; private set; }

    internal uint LastReplaceThreadId { get; private set; }

    internal uint LastDestroyThreadId { get; private set; }

    private ulong CreateOnDispatcher(
        SurfaceId id,
        SurfaceRequest request,
        DesktopAttachPoint attachPoint)
    {
        ThrowIfDisposed();
        LastCreateThreadId = DesktopNativeMethods.GetCurrentThreadId();
        EnsureWindowClassRegistered();
        nint parent = ToNativeHandle(attachPoint.WindowHandle);
        NativeRect parentBounds = GetParentBounds(parent);
        uint style = DesktopNativeMethods.WindowStyleChild |
            DesktopNativeMethods.WindowStyleClipChildren |
            DesktopNativeMethods.WindowStyleClipSiblings;
        nint window = DesktopNativeMethods.CreateWindowEx(
            DesktopNativeMethods.WindowExStyleNoActivate |
                DesktopNativeMethods.WindowExStyleToolWindow,
            className,
            $"LiveWall Surface {id.Value}",
            style,
            checked(request.Bounds.X - parentBounds.Left),
            checked(request.Bounds.Y - parentBounds.Top),
            request.Bounds.Width,
            request.Bounds.Height,
            parent,
            0,
            instance,
            0);
        if (window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed.");
        }

        try
        {
            PositionHidden(window, request.Bounds, parentBounds);
            surfaces.Add(window, DesktopSurfaceWindowState.Provisional);
            return unchecked((ulong)window.ToInt64());
        }
        catch
        {
            _ = DesktopNativeMethods.DestroyWindow(window);
            throw;
        }
    }

    private void ReplaceOnDispatcher(
        IReadOnlyList<ulong> provisionalSurfaceHandles,
        IReadOnlyList<ulong> replacedSurfaceHandles)
    {
        ThrowIfDisposed();
        LastReplaceThreadId = DesktopNativeMethods.GetCurrentThreadId();
        nint[] provisional = ValidateReplacementHandles(
            provisionalSurfaceHandles,
            DesktopSurfaceWindowState.Provisional,
            "provisional");
        nint[] replaced = ValidateReplacementHandles(
            replacedSurfaceHandles,
            DesktopSurfaceWindowState.Active,
            "active");
        if (provisional.Intersect(replaced).Any())
        {
            throw new ArgumentException("A Surface cannot be both provisional and replaced.");
        }

        nint deferred = DesktopNativeMethods.BeginDeferWindowPos(
            checked(provisional.Length + replaced.Length));
        if (deferred == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "BeginDeferWindowPos failed.");
        }

        foreach (nint window in replaced)
        {
            deferred = DeferVisibility(deferred, window, visible: false);
        }

        foreach (nint window in provisional)
        {
            deferred = DeferVisibility(deferred, window, visible: true);
        }

        if (!DesktopNativeMethods.EndDeferWindowPos(deferred))
        {
            int error = Marshal.GetLastWin32Error();
            RollBackVisibility(provisional, replaced);
            throw new Win32Exception(error, "EndDeferWindowPos failed; Surface visibility was rolled back.");
        }

        foreach (nint window in replaced)
        {
            surfaces[window] = DesktopSurfaceWindowState.Retired;
        }

        foreach (nint window in provisional)
        {
            surfaces[window] = DesktopSurfaceWindowState.Active;
        }
    }

    private nint[] ValidateReplacementHandles(
        IReadOnlyList<ulong> handles,
        DesktopSurfaceWindowState expectedState,
        string role)
    {
        nint[] windows = handles.Select(ToNativeHandle).ToArray();
        if (windows.Distinct().Count() != windows.Length)
        {
            throw new ArgumentException($"The {role} Surface list contains duplicate handles.");
        }

        foreach (nint window in windows)
        {
            if (!surfaces.TryGetValue(window, out DesktopSurfaceWindowState state) ||
                state != expectedState ||
                !DesktopNativeMethods.IsWindow(window))
            {
                throw new InvalidOperationException(
                    $"The {role} Surface is missing, invalid, or in state '{state}'.");
            }
        }

        return windows;
    }

    private static nint DeferVisibility(nint deferred, nint window, bool visible)
    {
        uint flags = DesktopNativeMethods.SetWindowPositionNoActivate |
            DesktopNativeMethods.SetWindowPositionNoMove |
            DesktopNativeMethods.SetWindowPositionNoSize;
        nint insertAfter;
        if (visible)
        {
            flags |= DesktopNativeMethods.SetWindowPositionShowWindow;
            insertAfter = DesktopNativeMethods.WindowBottom;
        }
        else
        {
            flags |= DesktopNativeMethods.SetWindowPositionHideWindow |
                DesktopNativeMethods.SetWindowPositionNoZOrder;
            insertAfter = 0;
        }

        nint next = DesktopNativeMethods.DeferWindowPos(
            deferred,
            window,
            insertAfter,
            0,
            0,
            0,
            0,
            flags);
        if (next == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DeferWindowPos failed.");
        }

        return next;
    }

    private static void RollBackVisibility(
        IEnumerable<nint> provisional,
        IEnumerable<nint> replaced)
    {
        foreach (nint window in provisional)
        {
            _ = SetVisibility(window, visible: false);
        }

        foreach (nint window in replaced)
        {
            _ = SetVisibility(window, visible: true);
        }
    }

    private static bool SetVisibility(nint window, bool visible)
    {
        uint flags = DesktopNativeMethods.SetWindowPositionNoActivate |
            DesktopNativeMethods.SetWindowPositionNoMove |
            DesktopNativeMethods.SetWindowPositionNoSize |
            DesktopNativeMethods.SetWindowPositionNoZOrder |
            (visible
                ? DesktopNativeMethods.SetWindowPositionShowWindow
                : DesktopNativeMethods.SetWindowPositionHideWindow);
        return DesktopNativeMethods.SetWindowPos(window, 0, 0, 0, 0, 0, flags);
    }

    private void DestroyOnDispatcher(nint surface)
    {
        LastDestroyThreadId = DesktopNativeMethods.GetCurrentThreadId();
        if (!surfaces.ContainsKey(surface))
        {
            return;
        }

        if (DesktopNativeMethods.IsWindow(surface) && !DesktopNativeMethods.DestroyWindow(surface))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DestroyWindow failed.");
        }

        surfaces.Remove(surface);
    }

    private void DisposeOnDispatcher()
    {
        List<Exception> failures = [];
        foreach (nint surface in surfaces.Keys.ToArray())
        {
            try
            {
                DestroyOnDispatcher(surface);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (registered)
        {
            if (!DesktopNativeMethods.UnregisterClass(className, instance))
            {
                failures.Add(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "UnregisterClass failed."));
            }

            registered = false;
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Desktop Surface cleanup failed.", failures);
        }
    }

    private void EnsureWindowClassRegistered()
    {
        if (registered)
        {
            return;
        }

        instance = DesktopNativeMethods.GetModuleHandle(null);
        if (instance == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetModuleHandle failed.");
        }

        WindowClassEx windowClass = new()
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProcedure = windowProcedure,
            Instance = instance,
            ClassName = className,
        };
        if (DesktopNativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed.");
        }

        registered = true;
    }

    private static NativeRect GetParentBounds(nint parent)
    {
        if (!DesktopNativeMethods.IsWindow(parent) ||
            !DesktopNativeMethods.GetWindowRect(parent, out NativeRect bounds))
        {
            throw new DesktopAttachPointUnavailableException(
                "The desktop attach point is no longer a valid window.");
        }

        return bounds;
    }

    private static void PositionHidden(
        nint surface,
        DisplayBounds bounds,
        NativeRect parentBounds)
    {
        if (!DesktopNativeMethods.SetWindowPos(
                surface,
                DesktopNativeMethods.WindowBottom,
                checked(bounds.X - parentBounds.Left),
                checked(bounds.Y - parentBounds.Top),
                bounds.Width,
                bounds.Height,
                DesktopNativeMethods.SetWindowPositionNoActivate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos failed.");
        }
    }

    private static nint ToNativeHandle(ulong handle) =>
        unchecked((nint)(long)handle);

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam) =>
        DesktopNativeMethods.DefWindowProc(window, message, wParam, lParam);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private enum DesktopSurfaceWindowState
    {
        Provisional,
        Active,
        Retired,
    }
}
