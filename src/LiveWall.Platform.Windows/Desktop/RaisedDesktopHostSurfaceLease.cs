using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

/// <summary>
/// Diagnostic-only Raised Desktop container. Shell handles remain private to this process;
/// callers may pass only <see cref="WindowHandle"/> to a renderer.
/// </summary>
public sealed class RaisedDesktopHostSurfaceLease : IAsyncDisposable
{
    public const string WindowClassPrefix = "LiveWall.RaisedDesktopHost";

    private readonly DesktopWindowDispatcher dispatcher = new();
    private readonly string className = $"{WindowClassPrefix}.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly DesktopNativeMethods.WindowProcedure windowProcedure;
    private nint instance;
    private nint window;
    private bool registered;
    private bool disposed;

    private RaisedDesktopHostSurfaceLease()
    {
        windowProcedure = WindowProcedure;
    }

    public ulong WindowHandle => unchecked((ulong)window.ToInt64());

    public int Width { get; private set; }

    public int Height { get; private set; }

    internal static async Task<RaisedDesktopHostSurfaceLease> CreateAsync(
        DesktopAttachmentLease attachmentLease,
        ShellWindowBounds virtualDesktopBounds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachmentLease);
        if (attachmentLease.PlacementKind != DesktopSurfacePlacementKind.BetweenAnchorAndBackdrop)
        {
            throw new ArgumentException("A Raised Desktop attachment lease is required.", nameof(attachmentLease));
        }

        RaisedDesktopHostSurfaceLease lease = new();
        try
        {
            await lease.dispatcher.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            await lease.dispatcher.InvokeAsync(
                    () => lease.CreateOnDispatcher(attachmentLease, virtualDesktopBounds),
                    cancellationToken)
                .ConfigureAwait(false);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
            await dispatcher.InvokeAsync(CleanupOnDispatcher, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            await dispatcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void CreateOnDispatcher(
        DesktopAttachmentLease attachmentLease,
        ShellWindowBounds bounds)
    {
        nint progman = ToHandle(attachmentLease.ParentWindowHandle);
        nint defView = ToHandle(attachmentLease.ZOrderAnchorWindowHandle);
        nint worker = ToHandle(attachmentLease.BackdropWindowHandle);
        if (!DesktopNativeMethods.IsWindow(progman) ||
            !DesktopNativeMethods.IsWindow(defView) ||
            !DesktopNativeMethods.IsWindow(worker))
        {
            throw new DesktopAttachPointUnavailableException(
                "Raised Desktop windows changed before the Host Surface was created.");
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Host Surface class registration failed.");
        }
        registered = true;

        Width = bounds.Width;
        Height = bounds.Height;
        window = DesktopNativeMethods.CreateWindowEx(
            DesktopNativeMethods.WindowExStyleNoActivate |
                DesktopNativeMethods.WindowExStyleToolWindow |
                DesktopNativeMethods.WindowExStyleNoRedirectionBitmap,
            className,
            "LiveWall Renderer-child Host Surface — diagnostic",
            DesktopNativeMethods.WindowStyleChild |
                DesktopNativeMethods.WindowStyleDisabled |
                DesktopNativeMethods.WindowStyleClipChildren |
                DesktopNativeMethods.WindowStyleClipSiblings,
            0,
            0,
            bounds.Width,
            bounds.Height,
            progman,
            0,
            instance,
            0);
        if (window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Host Surface creation failed.");
        }

        if (!DesktopNativeMethods.GetWindowRect(progman, out NativeRect progmanBounds))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read Progman bounds.");
        }

        if (!DesktopNativeMethods.SetWindowPos(
                worker,
                DesktopNativeMethods.WindowBottom,
                0,
                0,
                0,
                0,
                DesktopNativeMethods.SetWindowPositionNoActivate |
                    DesktopNativeMethods.SetWindowPositionNoMove |
                    DesktopNativeMethods.SetWindowPositionNoSize) ||
            !DesktopNativeMethods.SetWindowPos(
                window,
                defView,
                checked(bounds.X - progmanBounds.Left),
                checked(bounds.Y - progmanBounds.Top),
                bounds.Width,
                bounds.Height,
                DesktopNativeMethods.SetWindowPositionNoActivate |
                    DesktopNativeMethods.SetWindowPositionShowWindow))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not place the Host Surface between DefView and WorkerW.");
        }
    }

    private void CleanupOnDispatcher()
    {
        if (window != 0 && DesktopNativeMethods.IsWindow(window) &&
            !DesktopNativeMethods.DestroyWindow(window))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Host Surface cleanup failed.");
        }
        window = 0;

        if (registered && !DesktopNativeMethods.UnregisterClass(className, instance))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Host Surface class cleanup failed.");
        }
        registered = false;
    }

    private static nint ToHandle(ulong value) => unchecked((nint)(long)value);

    private static nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam) =>
        DesktopNativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
}
