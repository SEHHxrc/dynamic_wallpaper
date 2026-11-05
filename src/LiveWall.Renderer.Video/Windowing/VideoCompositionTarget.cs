using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace LiveWall.Renderer.Video.Windowing;

internal sealed class VideoCompositionTarget : IDisposable
{
    private VideoChildWindow? child;
    private IDXGIDevice? dxgiDevice;
    private IDXGIFactory2? factory;
    private IDXGISwapChain1? swapChain;
    private IDCompositionDevice? composition;
    private IDCompositionTarget? target;
    private IDCompositionVisual? visual;
    public ID3D11Texture2D Buffer { get; private set; } = null!;
    private readonly int width;
    private readonly int height;
    public bool IsValid => child?.IsValid == true;
    public ulong WindowHandle => unchecked((ulong)child!.Handle.ToInt64());
    public bool MatchesSize(int expectedWidth, int expectedHeight) => child?.MatchesSize(expectedWidth, expectedHeight) == true;

    public VideoCompositionTarget(ID3D11Device device, uint hostPid, ulong host, int width, int height)
    {
        this.width = width; this.height = height;
        try
        {
            child = new(host, hostPid, width, height);
            dxgiDevice = device.QueryInterface<IDXGIDevice>();
            factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            SwapChainDescription1 description = new(checked((uint)width), checked((uint)height),
                Format.B8G8R8A8_UNorm, false, Usage.RenderTargetOutput | Usage.ShaderInput, 2,
                Scaling.Stretch, SwapEffect.FlipSequential, AlphaMode.Ignore, SwapChainFlags.None);
            swapChain = factory.CreateSwapChainForComposition(device, description, null);
            Buffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
            composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            composition.CreateTargetForHwnd(child.Handle, true, out target).CheckError();
            composition.CreateVisual(out visual).CheckError();
            visual!.SetContent(swapChain).CheckError();
            target!.SetRoot(visual).CheckError();
            RequireSuccess(composition.Commit().Code);
        }
        catch { Dispose(); throw; }
    }

    public int Present()
    {
        if (!IsValid) throw new InvalidOperationException("Video binding is no longer valid.");
        int result = swapChain!.Present(0, PresentFlags.None).Code;
        RequireSuccess(result);
        return result;
    }
    public (int Commit, int Completion) Commit(bool synchronize)
    {
        int commit = composition!.Commit().Code; RequireSuccess(commit);
        int completion = synchronize ? composition.WaitForCommitCompletion().Code : 0;
        RequireSuccess(completion);
        return (commit, completion);
    }
    public int ResizeResident(int newWidth, int newHeight)
    {
        if (!IsValid) throw new InvalidOperationException("Video binding is no longer valid.");
        visual!.SetTransform(Matrix3x2.CreateScale((float)newWidth / width, (float)newHeight / height)).CheckError();
        child!.Resize(newWidth, newHeight);
        return Commit(false).Commit;
    }
    public void Dispose()
    {
        List<Exception> errors = [];
        Cleanup(() => { if (target is not null) { target.SetRoot(null!).CheckError(); composition?.Commit().CheckError(); } });
        Cleanup(() => Buffer?.Dispose());
        Cleanup(() => visual?.Dispose()); Cleanup(() => target?.Dispose());
        Cleanup(() => composition?.Dispose()); Cleanup(() => swapChain?.Dispose());
        Cleanup(() => factory?.Dispose()); Cleanup(() => dxgiDevice?.Dispose()); Cleanup(() => child?.Dispose());
        child = null; target = null; visual = null; composition = null; swapChain = null;
        factory = null; dxgiDevice = null; Buffer = null!;
        if (errors.Count != 0) throw new AggregateException("Video target cleanup failed.", errors);
        void Cleanup(Action action) { try { action(); } catch (Exception exception) { errors.Add(exception); } }
    }
    private static void RequireSuccess(int code)
    {
        if (code != 0) throw new InvalidOperationException($"Video submission did not return S_OK: 0x{code:X8}.");
    }
}
