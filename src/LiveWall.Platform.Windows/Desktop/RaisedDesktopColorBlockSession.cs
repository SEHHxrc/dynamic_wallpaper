using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

internal sealed class RaisedDesktopColorBlockSession : IAsyncDisposable
{
    private const string TestLabel = "LIVEWALL RAISED DESKTOP TEST — AUTO CLEANUP";
    private readonly DesktopWindowDispatcher dispatcher = new();
    private readonly string className =
        $"LiveWall.RaisedDesktopColor.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly DesktopNativeMethods.WindowProcedure windowProcedure;
    private nint instance;
    private nint backgroundBrush;
    private nint colorWindow;
    private bool registered;
    private bool disposed;

    private RaisedDesktopColorBlockSession()
    {
        windowProcedure = WindowProcedure;
    }

    public static async Task<RaisedDesktopColorBlockSession> CreateAsync(
        ulong progmanWindowHandle,
        ulong defViewWindowHandle,
        ulong workerWindowHandle,
        ShellWindowBounds virtualDesktopBounds,
        uint colorReference,
        RaisedDesktopPresentationKind presentationKind,
        CancellationToken cancellationToken)
    {
        RaisedDesktopColorBlockSession session = new();
        try
        {
            await session.dispatcher.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            await session.dispatcher.InvokeAsync(
                    () => session.CreateOnDispatcher(
                        ToNativeHandle(progmanWindowHandle),
                        ToNativeHandle(defViewWindowHandle),
                        ToNativeHandle(workerWindowHandle),
                        virtualDesktopBounds,
                        colorReference,
                        presentationKind),
                    cancellationToken)
                .ConfigureAwait(false);
            return session;
        }
        catch (Exception creationFailure)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Raised Desktop color-block creation and cleanup both failed.",
                    creationFailure,
                    cleanupFailure);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(creationFailure)
                .Throw();
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
        nint progman,
        nint defView,
        nint worker,
        ShellWindowBounds virtualDesktopBounds,
        uint colorReference,
        RaisedDesktopPresentationKind presentationKind)
    {
        if (!DesktopNativeMethods.IsWindow(progman) ||
            !DesktopNativeMethods.IsWindow(defView) ||
            !DesktopNativeMethods.IsWindow(worker))
        {
            throw new DesktopAttachPointUnavailableException(
                "Raised Desktop windows became invalid before the color block was created.");
        }

        instance = DesktopNativeMethods.GetModuleHandle(null);
        if (instance == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetModuleHandle failed.");
        }

        backgroundBrush = DesktopNativeMethods.CreateSolidBrush(colorReference);
        if (backgroundBrush == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateSolidBrush failed.");
        }

        WindowClassEx windowClass = new()
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProcedure = windowProcedure,
            Instance = instance,
            BackgroundBrush = backgroundBrush,
            ClassName = className,
        };
        if (DesktopNativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed.");
        }
        registered = true;

        uint extendedStyle = DesktopNativeMethods.WindowExStyleNoActivate |
            DesktopNativeMethods.WindowExStyleToolWindow;
        if (presentationKind == RaisedDesktopPresentationKind.LayeredGdi)
        {
            extendedStyle |= DesktopNativeMethods.WindowExStyleLayered;
        }

        colorWindow = DesktopNativeMethods.CreateWindowEx(
            extendedStyle,
            className,
            TestLabel,
            DesktopNativeMethods.WindowStylePopup |
                DesktopNativeMethods.WindowStyleDisabled |
                DesktopNativeMethods.WindowStyleClipChildren |
                DesktopNativeMethods.WindowStyleClipSiblings,
            0,
            0,
            virtualDesktopBounds.Width,
            virtualDesktopBounds.Height,
            0,
            0,
            instance,
            0);
        if (colorWindow == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Raised Desktop color window creation failed.");
        }

        if (presentationKind == RaisedDesktopPresentationKind.LayeredGdi &&
            !DesktopNativeMethods.SetLayeredWindowAttributes(
                colorWindow,
                0,
                byte.MaxValue,
                DesktopNativeMethods.LayeredWindowAttributeAlpha))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "SetLayeredWindowAttributes failed before Raised Desktop reparenting.");
        }

        ulong style = unchecked((ulong)DesktopNativeMethods.GetWindowLongPtr(
            colorWindow,
            DesktopNativeMethods.WindowLongStyle).ToInt64());
        _ = DesktopNativeMethods.SetWindowLongPtr(
            colorWindow,
            DesktopNativeMethods.WindowLongStyle,
            unchecked((nint)(long)(
                (style & ~DesktopNativeMethods.WindowStylePopup &
                    ~DesktopNativeMethods.WindowStyleCaption) |
                DesktopNativeMethods.WindowStyleChild)));
        ulong childStyle = unchecked((ulong)DesktopNativeMethods.GetWindowLongPtr(
            colorWindow,
            DesktopNativeMethods.WindowLongStyle).ToInt64());
        if ((childStyle & DesktopNativeMethods.WindowStyleChild) == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not apply WS_CHILD before Raised Desktop reparenting.");
        }

        _ = DesktopNativeMethods.SetParent(colorWindow, progman);
        if (DesktopNativeMethods.GetParent(colorWindow) != progman)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetParent to Progman failed.");
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
                    DesktopNativeMethods.SetWindowPositionNoSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not keep the Shell WorkerW at the bottom.");
        }

        if (!DesktopNativeMethods.SetWindowPos(
                colorWindow,
                defView,
                checked(virtualDesktopBounds.X - progmanBounds.Left),
                checked(virtualDesktopBounds.Y - progmanBounds.Top),
                virtualDesktopBounds.Width,
                virtualDesktopBounds.Height,
                DesktopNativeMethods.SetWindowPositionNoActivate |
                    DesktopNativeMethods.SetWindowPositionShowWindow))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not place the Raised Desktop color block behind DefView.");
        }

        PaintColorWindow();
    }

    private void PaintColorWindow()
    {
        if (!DesktopNativeMethods.GetClientRect(colorWindow, out NativeRect clientBounds))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read the Raised Desktop color-block client bounds.");
        }

        nint deviceContext = DesktopNativeMethods.GetDC(colorWindow);
        if (deviceContext == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not acquire the Raised Desktop color-block device context.");
        }

        try
        {
            if (DesktopNativeMethods.FillRect(deviceContext, in clientBounds, backgroundBrush) == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not paint the Raised Desktop color block.");
            }

            if (!DesktopNativeMethods.GdiFlush())
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not flush the Raised Desktop color-block GDI commands.");
            }
        }
        finally
        {
            _ = DesktopNativeMethods.ReleaseDC(colorWindow, deviceContext);
        }

        int compositionResult = DesktopNativeMethods.DwmFlush();
        if (compositionResult != 0)
        {
            throw new InvalidOperationException(
                $"Could not synchronize the Raised Desktop color block with DWM " +
                $"(HRESULT=0x{compositionResult:X8}).");
        }
    }

    private void CleanupOnDispatcher()
    {
        List<Exception> failures = [];
        DestroyWindow(colorWindow, "Raised Desktop color window", failures);
        colorWindow = 0;

        if (registered)
        {
            if (!DesktopNativeMethods.UnregisterClass(className, instance))
            {
                failures.Add(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Raised Desktop diagnostic class cleanup failed."));
            }
            registered = false;
            backgroundBrush = 0;
        }

        if (backgroundBrush != 0)
        {
            if (!DesktopNativeMethods.DeleteObject(backgroundBrush))
            {
                failures.Add(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Raised Desktop diagnostic brush cleanup failed."));
            }
            backgroundBrush = 0;
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Raised Desktop color-block cleanup failed.", failures);
        }
    }

    private static void DestroyWindow(nint window, string label, List<Exception> failures)
    {
        if (window != 0 && DesktopNativeMethods.IsWindow(window) &&
            !DesktopNativeMethods.DestroyWindow(window))
        {
            failures.Add(new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"{label} cleanup failed."));
        }
    }

    private static nint ToNativeHandle(ulong handle) => unchecked((nint)(long)handle);

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam) =>
        DesktopNativeMethods.DefWindowProc(window, message, wParam, lParam);
}
