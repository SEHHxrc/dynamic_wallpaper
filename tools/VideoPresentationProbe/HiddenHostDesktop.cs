using System.Collections.Concurrent;
using LiveWall.Application.Sessions;
using LiveWall.Domain.Displays;

namespace LiveWall.VideoPresentationProbe;

// Private hidden Host containers for the formal Video executable; never shown, never parented to the Shell.
// Window work runs on the thread that calls Pump, which must be the thread that owns the windows.
internal sealed class HiddenHostDesktop : IDesktopHost, IDisposable
{
    private readonly ConcurrentQueue<Action> owner = new();
    private readonly ConcurrentDictionary<SurfaceId, ProbeWindow> windows = new();
    private readonly ConcurrentDictionary<SurfaceId, TaskCompletionSource> destroyed = new();
    public int Replacements { get; private set; }
    public int LiveCount => windows.Count;
    public Task Destroyed(SurfaceId id) => destroyed[id].Task;
    private Task<T> OnOwner<T>(Func<T> action)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Enqueue(() => { try { completion.TrySetResult(action()); } catch (Exception e) { completion.TrySetException(e); } });
        return completion.Task;
    }
    public void Pump() { while (owner.TryDequeue(out Action? work)) work(); foreach (var window in windows.Values) { window.Pump(); break; } }
    public Task<DesktopTopology> EnsureTopologyAsync(DisplayTopology displays, CancellationToken token) => Task.FromResult(new DesktopTopology(1, [new("hidden", "DirectComposition", "HwndChild")]));
    public Task<DesktopSurface> CreateSurfaceAsync(SurfaceRequest request, CancellationToken token) => OnOwner(() =>
    {
        SurfaceId id = new(Guid.NewGuid().ToString("N"));
        ProbeWindow window = ProbeWindow.CreateHiddenHost(request.Bounds.Width, request.Bounds.Height);
        windows[id] = window; destroyed[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return new DesktopSurface(id, request.DisplayIds, unchecked((ulong)window.Handle.ToInt64()));
    });
    public Task ResizeSurfaceAsync(SurfaceId id, SurfaceRequest request, CancellationToken token) => OnOwner(() => { windows[id].Resize(request.Bounds.Width, request.Bounds.Height); return true; });
    public Task ReplaceSurfacesAsync(SurfaceReplacement replacement, CancellationToken token) { Replacements++; return Task.CompletedTask; }
    public Task DestroySurfaceAsync(SurfaceId id, CancellationToken token) => OnOwner(() =>
    {
        if (windows.TryRemove(id, out ProbeWindow? window)) window.Dispose();
        destroyed[id].TrySetResult(); return true;
    });
    public Task AbandonSurfaceAsync(SurfaceId id, CancellationToken token) => DestroySurfaceAsync(id, token);
    public void Dispose() { foreach (var id in windows.Keys) if (windows.TryRemove(id, out ProbeWindow? window)) window.Dispose(); }
}
