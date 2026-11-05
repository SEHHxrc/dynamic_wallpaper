using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LiveWall.Infrastructure.FileSystem;
using LiveWall.Renderer.Video.Mpv;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LiveWall.VideoPresentationProbe;

// Offscreen GPU work only. No HWND, DComp target, Shell operation or protocol event.
internal static class RenderApiPreflight
{
    public static int Run(string dll, string hash, string media, string output)
    {
        if (File.Exists(output)) throw new IOException("Use a fresh preflight result path.");
        using MpvClient mpv = new(dll, hash);
        mpv.Initialize(0, renderApi: true);
        mpv.EnableDiagnosticMessages();
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(10));
        using ManualResetEventSlim loaded = new();
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<GpuResult> framesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<GpuResult> gpu = Task.Run(() => RunGpu(mpv, ready, framesReady, loaded, budget.Token));
        string? failure = null;
        string? gpuFailure = null;
        GpuResult? result = null;
        string? videoOutput = null;
        try
        {
            ready.Task.WaitAsync(budget.Token).GetAwaiter().GetResult();
            mpv.LoadLocalVideo(media);
            while (!framesReady.Task.IsCompleted)
            {
                if (mpv.Poll().FileLoaded) loaded.Set();
                budget.Token.ThrowIfCancellationRequested();
                Thread.Sleep(5);
            }
            result = framesReady.Task.GetAwaiter().GetResult();
            videoOutput = mpv.ReadProperty("current-vo");
        }
        catch (Exception exception) { failure = exception.GetType().Name + ": " + exception.Message; }
        finally
        {
            budget.Cancel();
            try { gpu.GetAwaiter().GetResult(); }
            catch (Exception exception)
            {
                gpuFailure = exception.GetType().Name + ": " + exception.Message;
                failure ??= gpuFailure;
            }
        }
        bool valid = failure is null && result is { NewVideoFrames: >= 2, DistinctPixelHashes: >= 2 } &&
            result.PresentResults.All(code => code == 0);
        var evidence = new { schemaVersion = 1, scope = "C1D3D11RenderApiOffscreenPreflight", recordedAt = DateTimeOffset.UtcNow,
            valid, failure, gpuFailure, librarySha256 = hash, mpvVersion = mpv.ReadProperty("mpv-version"),
            videoOutput, gpu = result, nativeDiagnostics = mpv.DiagnosticMessages,
            windowCreated = false, compositionTargetCreated = false, shellTouched = false,
            firstFramePresentedEmitted = false, desktopPresentationValidated = false,
            formalProtocolSessionValidated = false };
        AtomicFileWriter.WriteJsonAsync(Path.GetFullPath(output), evidence).GetAwaiter().GetResult();
        Console.WriteLine($"C1_RENDER_API_PREFLIGHT: valid={valid}; result={Path.GetFullPath(output)}");
        return valid ? 0 : 1;
    }

    private static GpuResult RunGpu(MpvClient mpv, TaskCompletionSource ready,
        TaskCompletionSource<GpuResult> framesReady, ManualResetEventSlim loaded, CancellationToken token)
    {
        try
        {
            using ID3D11Device device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            using ID3D11DeviceContext commands = device.ImmediateContext;
            using IDXGIFactory2 factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            SwapChainDescription1 description = new(320, 180, Format.B8G8R8A8_UNorm, false,
                Usage.RenderTargetOutput | Usage.ShaderInput, 2, Scaling.Stretch, SwapEffect.FlipSequential, AlphaMode.Ignore, SwapChainFlags.None);
            using IDXGISwapChain1 swapChain = factory.CreateSwapChainForComposition(device, description, null);
            using ID3D11Texture2D buffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
            Texture2DDescription stagingDescription = buffer.Description;
            stagingDescription.Usage = ResourceUsage.Staging;
            stagingDescription.BindFlags = BindFlags.None;
            stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
            using ID3D11Texture2D staging = device.CreateTexture2D(stagingDescription);
            using MpvD3D11RenderContext render = mpv.CreateD3D11RenderContext(device.NativePointer);
            ready.TrySetResult();
            HashSet<string> hashes = new(StringComparer.Ordinal);
            List<int> presents = [];
            int frames = 0;
            while (hashes.Count < 2)
            {
                token.ThrowIfCancellationRequested();
                if (!render.TryGetNextFrame(out bool isVideo)) { Thread.Sleep(5); continue; }
                commands.ClearState();
                int renderResult = render.RenderToTexture(buffer.NativePointer);
                if (renderResult != 0) throw new InvalidOperationException("Render did not report exact success.");
                if (isVideo && loaded.IsSet)
                {
                    commands.CopyResource(staging, buffer);
                    commands.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapping).CheckError();
                    try
                    {
                        byte[] pixels = new byte[320 * 180 * 4];
                        for (int row = 0; row < 180; row++)
                            Marshal.Copy(mapping.DataPointer + checked((int)mapping.RowPitch * row), pixels, row * 320 * 4, 320 * 4);
                        bool hasVideoColour = false;
                        for (int pixel = 0; pixel < pixels.Length; pixel += 4)
                            hasVideoColour |= pixels[pixel] != 0 || pixels[pixel + 1] != 0 || pixels[pixel + 2] != 0;
                        if (!hasVideoColour) throw new InvalidOperationException("Only empty RGB pixels were read back.");
                        hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                        frames++;
                    }
                    finally { commands.Unmap(staging, 0); }
                }
                int present = swapChain.Present(0, PresentFlags.None).Code;
                if (present != 0) throw new InvalidOperationException($"Present did not return S_OK: 0x{present:X8}.");
                presents.Add(present);
                render.ReportPresented();
            }
            GpuResult result = new(frames, hashes.Count, presents.ToArray(), hashes.Order(StringComparer.Ordinal).ToArray(),
                RenderThreadOwned: true, TextureAndSwapChainOwned: true);
            framesReady.TrySetResult(result);
            // Do not free the render context as soon as two frames arrive: that kills
            // the active VO and can race the control thread's final media observation.
            // Keep serving the render API until the control owner requests retirement.
            while (!token.IsCancellationRequested)
            {
                if (!render.TryGetNextFrame(out _)) { Thread.Sleep(5); continue; }
                commands.ClearState();
                if (render.RenderToTexture(buffer.NativePointer) != 0 || swapChain.Present(0, PresentFlags.None).Code != 0)
                    throw new InvalidOperationException("Render/Present failed before controlled retirement.");
                render.ReportPresented();
            }
            return result;
        }
        catch (Exception exception) { ready.TrySetException(exception); framesReady.TrySetException(exception); throw; }
    }

    private sealed record GpuResult(int NewVideoFrames, int DistinctPixelHashes, IReadOnlyList<int> PresentResults,
        IReadOnlyList<string> PixelHashes, bool RenderThreadOwned, bool TextureAndSwapChainOwned);
}
