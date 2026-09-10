using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveWall.Platform.Windows.NativeMethods;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LiveWall.Platform.Windows.Desktop;

/// <summary>Renderer-owned child HWND and DirectComposition resources for the isolated probe.</summary>
public sealed class RendererChildDirectCompositionSession : IAsyncDisposable
{
    public const string WindowClassPrefix = "LiveWall.RendererChildProbe";

    private readonly DesktopWindowDispatcher dispatcher = new();
    private readonly string className = $"{WindowClassPrefix}.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly DesktopNativeMethods.WindowProcedure windowProcedure;
    private nint instance;
    private nint childWindow;
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

    private RendererChildDirectCompositionSession() => windowProcedure = WindowProcedure;

    public ulong WindowHandle => unchecked((ulong)childWindow.ToInt64());

    public static async Task<RendererChildDirectCompositionSession> CreateAsync(
        ulong hostWindowHandle,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        RendererChildDirectCompositionSession session = new();
        try
        {
            await session.dispatcher.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            await session.dispatcher.InvokeAsync(
                    () => session.CreateOnDispatcher(
                        unchecked((nint)(long)hostWindowHandle), width, height),
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

    public async Task PresentAsync(uint colorReference, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await dispatcher.InvokeAsync(
                () => PresentOnDispatcher(colorReference),
                cancellationToken)
            .ConfigureAwait(false);
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

    private void CreateOnDispatcher(nint host, int width, int height)
    {
        if (!DesktopNativeMethods.IsWindow(host))
        {
            throw new DesktopAttachPointUnavailableException("The Renderer received an invalid Host Surface HWND.");
        }

        instance = DesktopNativeMethods.GetModuleHandle(null);
        WindowClassEx windowClass = new()
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProcedure = windowProcedure,
            Instance = instance,
            ClassName = className,
        };
        if (instance == 0 || DesktopNativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Renderer child class registration failed.");
        }
        registered = true;

        childWindow = DesktopNativeMethods.CreateWindowEx(
            DesktopNativeMethods.WindowExStyleNoActivate |
                DesktopNativeMethods.WindowExStyleToolWindow |
                DesktopNativeMethods.WindowExStyleNoRedirectionBitmap,
            className,
            "LiveWall Renderer-child DComp probe",
            DesktopNativeMethods.WindowStyleChild |
                DesktopNativeMethods.WindowStyleDisabled |
                DesktopNativeMethods.WindowStyleClipChildren |
                DesktopNativeMethods.WindowStyleClipSiblings,
            0, 0, width, height, host, 0, instance, 0);
        if (childWindow == 0 || !DesktopNativeMethods.SetWindowPos(
                childWindow, DesktopNativeMethods.WindowTop, 0, 0, width, height,
                DesktopNativeMethods.SetWindowPositionNoActivate |
                    DesktopNativeMethods.SetWindowPositionShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Renderer child creation failed.");
        }

        d3dDevice = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        d3dContext = d3dDevice.ImmediateContext;
        dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        dxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(debug: false);
        SwapChainDescription1 description = new(
            checked((uint)width), checked((uint)height), Format.B8G8R8A8_UNorm,
            stereo: false, Usage.RenderTargetOutput, bufferCount: 2, Scaling.Stretch,
            SwapEffect.FlipSequential, AlphaMode.Ignore, SwapChainFlags.None);
        swapChain = dxgiFactory.CreateSwapChainForComposition(d3dDevice, description, null);
        backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        renderTarget = d3dDevice.CreateRenderTargetView(backBuffer);
        compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        compositionDevice.CreateTargetForHwnd(childWindow, topmost: true, out compositionTarget).CheckError();
        compositionDevice.CreateVisual(out compositionVisual).CheckError();
        compositionVisual!.SetContent(swapChain).CheckError();
        compositionTarget!.SetRoot(compositionVisual).CheckError();
        compositionDevice.Commit().CheckError();
    }

    private void PresentOnDispatcher(uint colorReference)
    {
        if (d3dContext is null || renderTarget is null || swapChain is null ||
            compositionDevice is null)
        {
            throw new InvalidOperationException("Renderer DirectComposition resources are not ready.");
        }

        Color4 color = new(
            (colorReference & 0xFF) / 255f,
            ((colorReference >> 8) & 0xFF) / 255f,
            ((colorReference >> 16) & 0xFF) / 255f,
            1f);
        d3dContext.ClearRenderTargetView(renderTarget, color);
        swapChain.Present(1, PresentFlags.None).CheckError();
        compositionDevice.Commit().CheckError();
        int flushResult = DesktopNativeMethods.DwmFlush();
        if (flushResult != 0)
        {
            throw new InvalidOperationException($"DwmFlush failed (HRESULT=0x{flushResult:X8}).");
        }
    }

    private void CleanupOnDispatcher()
    {
        if (compositionTarget is not null)
        {
            compositionTarget.SetRoot(null!).CheckError();
            compositionDevice?.Commit().CheckError();
        }
        renderTarget?.Dispose();
        backBuffer?.Dispose();
        swapChain?.Dispose();
        compositionVisual?.Dispose();
        compositionTarget?.Dispose();
        compositionDevice?.Dispose();
        dxgiFactory?.Dispose();
        dxgiDevice?.Dispose();
        d3dContext?.ClearState();
        d3dContext?.Flush();
        d3dContext?.Dispose();
        d3dDevice?.Dispose();

        if (childWindow != 0 && DesktopNativeMethods.IsWindow(childWindow))
        {
            _ = DesktopNativeMethods.DestroyWindow(childWindow);
        }
        childWindow = 0;
        if (registered)
        {
            _ = DesktopNativeMethods.UnregisterClass(className, instance);
        }
        registered = false;
    }

    private static nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam) =>
        DesktopNativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
}
