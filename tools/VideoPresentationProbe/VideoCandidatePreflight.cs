using System.Collections.Concurrent;
using System.Diagnostics;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;
using LiveWall.Host.Orchestration;
using LiveWall.Infrastructure.FileSystem;

namespace LiveWall.VideoPresentationProbe;

internal static class VideoCandidatePreflight
{
    public static int Run(string renderer, string library, string content, string output)
    {
        if (File.Exists(output)) throw new IOException("Use a fresh product-chain result path.");
        VideoProductCandidateAssets assets = VideoProductCandidateAssets.Load(content, library);
        using ProbeWindow window = ProbeWindow.CreateHiddenHost(320, 180);
        ConcurrentQueue<Action> ownerWork = new();
        HiddenDesktop desktop = new(unchecked((ulong)window.Handle.ToInt64()), () =>
        {
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ownerWork.Enqueue(() => { try { window.Dispose(); completion.TrySetResult(); } catch (Exception exception) { completion.TrySetException(exception); } });
            return completion.Task;
        });
        List<Process> processes = [];
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(70));
        RendererProductCandidateRequest request = new(renderer, new("hidden-test", "0", false),
            new DisplayTopology(1, [new(new DisplayId("hidden-test"), "hidden-container", new(0, 0, 320, 180), 1, 60, true)]),
            TimeSpan.FromSeconds(1), VideoAssets: assets, FirstFrameCommitted: sessions =>
            {
                foreach (var session in sessions)
                {
                    Process process = Process.GetProcessById(session.RendererProcessId!.Value);
                    _ = process.SafeHandle; processes.Add(process);
                }
            });
        Task<RendererProductCandidateResult> run = Task.Run(() => RendererProductCandidateDiagnostics.RunWithDesktopAsync(
            request, assets.Entry, assets.CreateProvider(renderer), desktop, budget.Token));
        while (!run.IsCompleted)
        {
            while (ownerWork.TryDequeue(out Action? action)) action();
            window.Pump(); Thread.Sleep(2);
        }
        string? failure = null;
        RendererProductCandidateResult? result = null;
        try { result = run.GetAwaiter().GetResult(); }
        catch (Exception exception) { failure = exception.GetType().Name + ": " + exception.Message; }
        window.Dispose();
        List<int?> exits = [];
        foreach (Process process in processes)
        {
            try { exits.Add(process.WaitForExit(3000) ? process.ExitCode : null); }
            finally { process.Dispose(); }
        }
        bool valid = failure is null && result?.FirstFrameCommitted == true && result.OwnedResourcesCleaned &&
            result.RetirementResult.Graceful && desktop.ReplacementCount == 1 && desktop.Destroyed &&
            exits.Count == 1 && exits[0] == 0;
        var report = new { schemaVersion = 1, scope = "C1ProductHostChainHiddenContainer", recordedAt = DateTimeOffset.UtcNow,
            valid, failure, hostGeneration = result?.HostGeneration, apply = result?.GenerationResult, timeline = result?.Timeline,
            retirement = result?.RetirementResult, replacementCount = desktop.ReplacementCount,
            hostSurfaceDestroyed = desktop.Destroyed, rendererExitCodes = exits, shellTouched = false,
            attachmentBackendValidated = false, desktopPresentationValidated = false, productionAttachmentEnabled = false };
        AtomicFileWriter.WriteJsonAsync(Path.GetFullPath(output), report).GetAwaiter().GetResult();
        Console.WriteLine($"C1_PRODUCT_PREFLIGHT: valid={valid}; result={Path.GetFullPath(output)}");
        return valid ? 0 : 1;
    }

    private sealed class HiddenDesktop(ulong window, Func<Task> destroy) : IDesktopHost
    {
        public int ReplacementCount { get; private set; }
        public bool Destroyed { get; private set; }
        public Task<DesktopTopology> EnsureTopologyAsync(DisplayTopology displays, CancellationToken token) =>
            Task.FromResult(new DesktopTopology(1, [new("hidden-test", "DirectComposition", "HwndChild")]));
        public Task<DesktopSurface> CreateSurfaceAsync(SurfaceRequest request, CancellationToken token) =>
            Task.FromResult(new DesktopSurface(new SurfaceId("hidden-product-surface"), request.DisplayIds, window));
        public Task ResizeSurfaceAsync(SurfaceId id, SurfaceRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task ReplaceSurfacesAsync(SurfaceReplacement replacement, CancellationToken token)
        {
            if (replacement.ProvisionalSurfaceIds.Count != 1 || replacement.ReplacedSurfaceIds.Count != 0 || ReplacementCount != 0)
                throw new InvalidOperationException("Unexpected hidden product batch.");
            ReplacementCount++; return Task.CompletedTask; // Deliberately never show the hidden container.
        }
        public async Task DestroySurfaceAsync(SurfaceId id, CancellationToken token) { await destroy().ConfigureAwait(false); Destroyed = true; }
        public Task AbandonSurfaceAsync(SurfaceId id, CancellationToken token) => DestroySurfaceAsync(id, token);
    }
}
