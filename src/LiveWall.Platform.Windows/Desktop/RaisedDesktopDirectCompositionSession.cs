using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Platform.Windows.NativeMethods;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LiveWall.Platform.Windows.Desktop;

internal sealed class RaisedDesktopDirectCompositionSession : IAsyncDisposable
{
    private const string TestLabel = "LIVEWALL DIRECTCOMPOSITION TEST — AUTO CLEANUP";
    private readonly DesktopWindowDispatcher dispatcher = new();
    private readonly string className =
        $"LiveWall.RaisedDesktopColor.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly DesktopNativeMethods.WindowProcedure windowProcedure;
    private nint instance;
    private nint compositionWindow;
    private bool registered;
    private bool disposed;
    private ID3D11Device? d3dDevice;
    private ID3D11DeviceContext? d3dContext;
    private IDXGIDevice? dxgiDevice;
    private IDXGIFactory2? dxgiFactory;
    private IDXGISwapChain1? swapChain;
    private ID3D11Texture2D? backBuffer;
    private ID3D11RenderTargetView? renderTarget;
    private IDCompositionDevice? compositionDevice;
    private IDCompositionTarget? compositionTarget;
    private IDCompositionVisual? compositionVisual;

    private RaisedDesktopDirectCompositionSession()
    {
        windowProcedure = WindowProcedure;
    }

    public static async Task<RaisedDesktopDirectCompositionSession> CreateAsync(
        ulong progmanWindowHandle,
        ulong defViewWindowHandle,
        ulong workerWindowHandle,
        ShellWindowBounds virtualDesktopBounds,
        uint colorReference,
        CancellationToken cancellationToken)
    {
        RaisedDesktopDirectCompositionSession session = new();
        try
        {
            await session.dispatcher.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            await session.dispatcher.InvokeAsync(
                    () => session.CreateOnDispatcher(
                        ToNativeHandle(progmanWindowHandle),
                        ToNativeHandle(defViewWindowHandle),
                        ToNativeHandle(workerWindowHandle),
                        virtualDesktopBounds,
                        colorReference),
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
                    "Raised Desktop DirectComposition creation and cleanup both failed.",
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
        uint colorReference)
    {
        ValidateShellWindows(progman, defView, worker);
        CreateCompositionWindow(progman, defView, worker, virtualDesktopBounds);

        d3dDevice = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport);
        d3dContext = d3dDevice.ImmediateContext;
        dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        dxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(debug: false);

        SwapChainDescription1 description = new(
            checked((uint)virtualDesktopBounds.Width),
            checked((uint)virtualDesktopBounds.Height),
            Format.B8G8R8A8_UNorm,
            stereo: false,
            Usage.RenderTargetOutput,
            bufferCount: 2,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            AlphaMode.Ignore,
            SwapChainFlags.None);
        swapChain = dxgiFactory.CreateSwapChainForComposition(
            d3dDevice,
            description,
            restrictToOutput: null);
        backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        renderTarget = d3dDevice.CreateRenderTargetView(backBuffer);

        compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        compositionDevice.CreateTargetForHwnd(
            compositionWindow,
            topmost: true,
            out compositionTarget).CheckError();
        compositionDevice.CreateVisual(out compositionVisual).CheckError();
        compositionVisual!.SetContent(swapChain).CheckError();
        compositionTarget!.SetRoot(compositionVisual).CheckError();

        d3dContext.ClearRenderTargetView(renderTarget, ToOpaqueColor(colorReference));
        swapChain.Present(1, PresentFlags.None).CheckError();
        compositionDevice.Commit().CheckError();

        int compositionResult = DesktopNativeMethods.DwmFlush();
        if (compositionResult != 0)
        {
            throw new InvalidOperationException(
                $"Could not synchronize the DirectComposition probe with DWM " +
                $"(HRESULT=0x{compositionResult:X8}).");
        }
    }

    private static void ValidateShellWindows(nint progman, nint defView, nint worker)
    {
        if (!DesktopNativeMethods.IsWindow(progman) ||
            !DesktopNativeMethods.IsWindow(defView) ||
            !DesktopNativeMethods.IsWindow(worker))
        {
            throw new DesktopAttachPointUnavailableException(
                "Raised Desktop windows became invalid before the DirectComposition probe was created.");
        }
    }

    private void CreateCompositionWindow(
        nint progman,
        nint defView,
        nint worker,
        ShellWindowBounds virtualDesktopBounds)
    {
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

        compositionWindow = DesktopNativeMethods.CreateWindowEx(
            DesktopNativeMethods.WindowExStyleNoActivate |
                DesktopNativeMethods.WindowExStyleToolWindow |
                DesktopNativeMethods.WindowExStyleNoRedirectionBitmap,
            className,
            TestLabel,
            DesktopNativeMethods.WindowStyleChild |
                DesktopNativeMethods.WindowStyleDisabled |
                DesktopNativeMethods.WindowStyleClipChildren |
                DesktopNativeMethods.WindowStyleClipSiblings,
            0,
            0,
            virtualDesktopBounds.Width,
            virtualDesktopBounds.Height,
            progman,
            0,
            instance,
            0);
        if (compositionWindow == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Raised Desktop DirectComposition window creation failed.");
        }

        if ((unchecked((ulong)DesktopNativeMethods.GetWindowLongPtr(
                compositionWindow,
                DesktopNativeMethods.WindowLongStyle).ToInt64()) &
                DesktopNativeMethods.WindowStyleChild) == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "DirectComposition diagnostic window was not created as a Progman child.");
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
                compositionWindow,
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
                "Could not place the DirectComposition probe behind DefView.");
        }
    }

    private void CleanupOnDispatcher()
    {
        List<Exception> failures = [];
        DisposeGraphicsResources(failures);

        if (compositionWindow != 0 && DesktopNativeMethods.IsWindow(compositionWindow) &&
            !DesktopNativeMethods.DestroyWindow(compositionWindow))
        {
            failures.Add(new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Raised Desktop DirectComposition window cleanup failed."));
        }
        compositionWindow = 0;

        if (registered && !DesktopNativeMethods.UnregisterClass(className, instance))
        {
            failures.Add(new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Raised Desktop DirectComposition class cleanup failed."));
        }
        registered = false;

        if (failures.Count > 0)
        {
            throw new AggregateException("Raised Desktop DirectComposition cleanup failed.", failures);
        }
    }

    private void DisposeGraphicsResources(List<Exception> failures)
    {
        try
        {
            if (compositionTarget is not null)
            {
                compositionTarget.SetRoot(null!).CheckError();
                compositionDevice?.Commit().CheckError();
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        renderTarget?.Dispose();
        renderTarget = null;
        backBuffer?.Dispose();
        backBuffer = null;
        swapChain?.Dispose();
        swapChain = null;
        compositionVisual?.Dispose();
        compositionVisual = null;
        compositionTarget?.Dispose();
        compositionTarget = null;
        compositionDevice?.Dispose();
        compositionDevice = null;
        dxgiFactory?.Dispose();
        dxgiFactory = null;
        dxgiDevice?.Dispose();
        dxgiDevice = null;
        d3dContext?.ClearState();
        d3dContext?.Flush();
        d3dContext?.Dispose();
        d3dContext = null;
        d3dDevice?.Dispose();
        d3dDevice = null;
    }

    private static Color4 ToOpaqueColor(uint colorReference) => new(
        (colorReference & 0xFF) / 255f,
        ((colorReference >> 8) & 0xFF) / 255f,
        ((colorReference >> 16) & 0xFF) / 255f,
        1f);

    private static nint ToNativeHandle(ulong handle) => unchecked((nint)(long)handle);

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam) =>
        DesktopNativeMethods.DefWindowProc(window, message, wParam, lParam);
}
