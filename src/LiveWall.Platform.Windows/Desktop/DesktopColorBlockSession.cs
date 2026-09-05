using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Platform.Windows.NativeMethods;

namespace LiveWall.Platform.Windows.Desktop;

public sealed class DesktopColorBlockSession : IAsyncDisposable
{
    private const string TestLabel = "LIVEWALL DESKTOP HOST TEST — AUTO CLEANUP";
    private readonly DesktopWindowDispatcher dispatcher = new();
    private readonly string className = $"LiveWall.ColorBlock.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly DesktopNativeMethods.WindowProcedure windowProcedure;
    private nint instance;
    private nint backgroundBrush;
    private nint colorWindow;
    private bool registered;
    private bool disposed;

    private DesktopColorBlockSession()
    {
        windowProcedure = WindowProcedure;
    }

    public static async Task<DesktopColorBlockSession> CreateAsync(
        ulong parentWindowHandle,
        int width,
        int height,
        uint colorReference,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        DesktopColorBlockSession session = new();
        try
        {
            await session.dispatcher.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            await session.dispatcher.InvokeAsync(
                    () => session.CreateWindows(
                        unchecked((nint)(long)parentWindowHandle),
                        width,
                        height,
                        colorReference),
                    cancellationToken)
                .ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
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
            await dispatcher.InvokeAsync(CleanupWindows, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            await dispatcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void CreateWindows(nint parent, int width, int height, uint colorReference)
    {
        if (!DesktopNativeMethods.IsWindow(parent))
        {
            throw new ArgumentException("The diagnostic parent Surface is not a valid window.");
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
        colorWindow = DesktopNativeMethods.CreateWindowEx(
            DesktopNativeMethods.WindowExStyleNoActivate,
            className,
            TestLabel,
            DesktopNativeMethods.WindowStyleChild |
                DesktopNativeMethods.WindowStyleVisible,
            0,
            0,
            width,
            height,
            parent,
            0,
            instance,
            0);
        if (colorWindow == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Color block window creation failed.");
        }

        int labelWidth = Math.Min(width, 720);
        int labelHeight = Math.Min(height, 96);
        nint label = DesktopNativeMethods.CreateWindowEx(
            DesktopNativeMethods.WindowExStyleNoActivate,
            "STATIC",
            TestLabel,
            DesktopNativeMethods.WindowStyleChild |
                DesktopNativeMethods.WindowStyleVisible |
                DesktopNativeMethods.StaticStyleCenter |
                DesktopNativeMethods.StaticStyleCenterImage,
            Math.Max(0, (width - labelWidth) / 2),
            Math.Max(0, (height - labelHeight) / 2),
            labelWidth,
            labelHeight,
            colorWindow,
            0,
            instance,
            0);
        if (label == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Diagnostic label creation failed.");
        }
    }

    private void CleanupWindows()
    {
        List<Exception> failures = [];
        if (colorWindow != 0)
        {
            if (DesktopNativeMethods.IsWindow(colorWindow) &&
                !DesktopNativeMethods.DestroyWindow(colorWindow))
            {
                failures.Add(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Diagnostic window cleanup failed."));
            }

            colorWindow = 0;
        }

        if (registered)
        {
            if (!DesktopNativeMethods.UnregisterClass(className, instance))
            {
                failures.Add(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Diagnostic class cleanup failed."));
            }

            registered = false;
        }

        if (backgroundBrush != 0)
        {
            if (!DesktopNativeMethods.DeleteObject(backgroundBrush))
            {
                failures.Add(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Diagnostic brush cleanup failed."));
            }

            backgroundBrush = 0;
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Color block diagnostic cleanup failed.", failures);
        }
    }

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam) =>
        DesktopNativeMethods.DefWindowProc(window, message, wParam, lParam);
}
