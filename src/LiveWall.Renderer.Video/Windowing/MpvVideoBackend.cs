using System.Collections.Concurrent;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Renderer.Video.Mpv;
using LiveWall.Renderer.Video.Protocol;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace LiveWall.Renderer.Video.Windowing;

internal sealed class MpvVideoBackend(string libraryPath, string libraryHash, uint hostProcess,
    VideoControlEvidence? evidence = null) : IVideoSessionBackend
{
    private readonly TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MpvClient? mpv;
    private RenderWorker? worker;
    private bool disposed;
    private string expectedFit = "Cover";
    private double expectedVolume;
    private bool expectedMuted = true;
    private bool? expectedPause;
    public Task Failure => failure.Task;
    public void CheckHealth() { if (!disposed && mpv is not null) { _ = mpv.Poll(); if (evidence is not null) Observe("Health"); } }

    public async Task InitializeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (mpv is not null || disposed) throw new InvalidOperationException("Video backend cannot initialize twice.");
        mpv = new(libraryPath, libraryHash);
        mpv.Initialize(0, renderApi: true);
        worker = new(mpv, hostProcess, failure);
        await worker.Ready.WaitAsync(token).ConfigureAwait(false);
        Observe("Initialized");
    }
    public async Task<ulong> AttachAsync(AttachSurfacePayload surface, CancellationToken token)
    {
        ulong child = await Worker.InvokeAsync(state => state.Attach(surface), token).ConfigureAwait(false);
        Observe("Attached", surface.Width, surface.Height);
        return child;
    }

    public async Task LoadAsync(string mediaPath, CancellationToken token)
    {
        await Worker.InvokeAsync(state => { state.ResetContent(); return true; }, token).ConfigureAwait(false);
        mpv!.LoadLocalVideo(mediaPath);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            MpvOutputEvidence evidence = mpv.Poll();
            if (evidence.FileLoaded && evidence.VideoOutputConfigured &&
                int.TryParse(mpv.ReadProperty("video-params/w"), out int width) && width > 0 &&
                int.TryParse(mpv.ReadProperty("video-params/h"), out int height) && height > 0)
            { Observe("Loaded"); return; }
            if (failure.Task.IsCompleted) await failure.Task.ConfigureAwait(false);
            await Task.Delay(5, token).ConfigureAwait(false);
        }
    }
    public async Task<VideoFrameSubmission> SubmitAsync(VideoFrameTicket ticket, CancellationToken token)
    {
        while (true)
        {
            _ = mpv!.Poll(); // Normal client API stays on the control side.
            VideoFrameSubmission? submission = await Worker.InvokeAsync(state => state.Submit(ticket), token).ConfigureAwait(false);
            if (submission is not null) { Observe("Submitted", ticket.Width, ticket.Height); return submission; }
            await Task.Delay(5, token).ConfigureAwait(false);
        }
    }
    public async Task<VideoFrameSubmission> BoundsAsync(VideoFrameTicket ticket, CancellationToken token)
    {
        VideoFrameSubmission result = await Worker.InvokeAsync(state => state.Bounds(ticket), token).ConfigureAwait(false);
        Observe("Bounds", ticket.Width, ticket.Height);
        return result;
    }
    public Task PlaybackAsync(string state, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        mpv!.Pause(state != "Playing");
        if ((mpv.ReadProperty("pause") == "yes") != (state != "Playing"))
            throw new InvalidOperationException("Video pause readback failed.");
        expectedPause = state != "Playing";
        Observe(state);
        return Task.CompletedTask;
    }
    public Task FitAsync(string fit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); mpv!.SetFit(fit); expectedFit = fit;
        Observe("Fit"); return Task.CompletedTask;
    }
    public Task VolumeAsync(double volume, bool muted, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); mpv!.SetVolume(volume, muted);
        expectedVolume = volume * 100; expectedMuted = muted;
        Observe("Volume"); return Task.CompletedTask;
    }
    private void Observe(string phase, int width = 0, int height = 0)
    {
        VideoControlSnapshot snapshot = VideoControlSnapshot.Read(mpv!.ReadProperty);
        if (snapshot.Aspect != (expectedFit == "Stretch" ? "no" : "yes") ||
            snapshot.Unscaled != (expectedFit == "Center" ? "yes" : "no") ||
            snapshot.PanScan != (expectedFit == "Cover" ? 1 : 0) || snapshot.Volume is null ||
            Math.Abs(snapshot.Volume.Value - expectedVolume) > 0.001 || snapshot.Muted != expectedMuted ||
            expectedPause is { } paused && snapshot.Paused != paused)
            throw new InvalidOperationException("Video control settings readback failed.");
        evidence?.Record(phase, snapshot, width, height);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { if (worker is not null) await worker.DisposeAsync().ConfigureAwait(false); }
        finally { mpv?.Dispose(); mpv = null; }
        evidence?.Record("Disposed", null);
    }
    private RenderWorker Worker => !disposed && worker is not null ? worker : throw new InvalidOperationException("Video backend is unavailable.");

    private sealed class RenderWorker : IAsyncDisposable
    {
        private readonly ConcurrentQueue<(Action<RenderState> Run, Action<Exception> Fail)> work = new();
        private readonly AutoResetEvent wake = new(false);
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        private bool stopping;
        private int wakeDisposed;
        public Task Ready => ready.Task;

        public RenderWorker(MpvClient client, uint hostPid, TaskCompletionSource failure)
        {
            Thread thread = new(() => Run(client, hostPid, failure)) { IsBackground = true, Name = "LiveWall.Video.Render" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        public Task<T> InvokeAsync<T>(Func<RenderState, T> action, CancellationToken token)
        {
            TaskCompletionSource<T> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (stopping || stopped.Task.IsCompleted) throw new InvalidOperationException("Video render worker stopped.");
                work.Enqueue((state =>
                {
                    if (token.IsCancellationRequested) { result.TrySetCanceled(token); return; }
                    try
                    {
                        T value = action(state);
                        if (token.IsCancellationRequested) result.TrySetCanceled(token);
                        else result.TrySetResult(value);
                    }
                    catch (Exception exception) { result.TrySetException(exception); }
                }, exception => result.TrySetException(exception)));
                wake.Set();
            }
            return result.Task;
        }
        public async ValueTask DisposeAsync()
        {
            lock (gate) { stopping = true; wake.Set(); }
            try { await stopped.Task.ConfigureAwait(false); }
            finally { if (Interlocked.Exchange(ref wakeDisposed, 1) == 0) wake.Dispose(); }
        }
        private void Run(MpvClient client, uint hostPid, TaskCompletionSource failure)
        {
            Exception? problem = null;
            RenderState? state = null;
            try
            {
                state = new(client, hostPid, () => wake.Set());
                ready.TrySetResult();
                while (true)
                {
                    lock (gate) { if (stopping) break; }
                    while (work.TryDequeue(out var action)) action.Run(state);
                    VideoChildWindow.Pump();
                    state.DrawPending();
                    wake.WaitOne(20); // Update callback wakes real frames; bounded fallback pumps HWND messages.
                }
            }
            catch (Exception exception) { problem = exception; }
            finally
            {
                try { state?.Dispose(); }
                catch (Exception exception) { problem = problem is null ? exception : new AggregateException(problem, exception); }
                lock (gate)
                {
                    stopping = true;
                    // Complete every queued task, including cancellation/disposal races.
                    while (work.TryDequeue(out var action)) action.Fail(problem ?? new ObjectDisposedException("Video render worker"));
                }
                if (problem is null) { stopped.TrySetResult(); ready.TrySetCanceled(); }
                else { ready.TrySetException(problem); failure.TrySetException(problem); stopped.TrySetException(problem); }
            }
        }
    }

    private sealed class RenderState : IDisposable
    {
        private readonly uint hostPid;
        private ID3D11Device? device;
        private ID3D11DeviceContext? commands;
        private MpvD3D11RenderContext? render;
        private VideoCompositionTarget? target;
        private bool hasFrame;
        private long submittedSequence;
        private long acknowledgedSequence;
        public RenderState(MpvClient client, uint hostPid, Action wake)
        {
            this.hostPid = hostPid;
            try
            {
                device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                commands = device.ImmediateContext;
                render = client.CreateD3D11RenderContext(device.NativePointer);
                render.SetWakeup(wake);
            }
            catch { Dispose(); throw; }
        }
        public ulong Attach(AttachSurfacePayload surface)
        {
            VideoCompositionTarget replacement = new(device!, hostPid, surface.WindowHandle, surface.Width, surface.Height);
            VideoCompositionTarget? previous = target;
            target = replacement;
            previous?.Dispose(); // Create new before retiring old; never reuse old binding evidence.
            return replacement.WindowHandle;
        }
        public void ResetContent() => hasFrame = false;
        public void DrawPending()
        {
            if (target?.IsValid != true || !render!.TryGetNextFrame(out bool newVideo)) return;
            RenderAndPresent();
            hasFrame |= newVideo;
        }
        public VideoFrameSubmission? Submit(VideoFrameTicket ticket)
        {
            if (!hasFrame) return null;
            if (target?.MatchesSize(ticket.Width, ticket.Height) != true)
                throw new InvalidOperationException("Video target geometry does not match the frame ticket.");
            // Explicit redraw works for paused content; do not wait for a new decoded frame.
            int rendered = RenderAndPresent();
            (int commit, int completion) = target!.Commit(true);
            acknowledgedSequence = submittedSequence;
            return new(ticket, submittedSequence, ticket.Width, ticket.Height, true, target.IsValid,
                rendered, 0, commit, completion);
        }
        public VideoFrameSubmission Bounds(VideoFrameTicket ticket)
        {
            if (!hasFrame || acknowledgedSequence <= 0 || target?.IsValid != true)
                throw new InvalidOperationException("No resident Video frame for bounds adaptation.");
            int commit = target.ResizeResident(ticket.Width, ticket.Height);
            if (!target.MatchesSize(ticket.Width, ticket.Height)) throw new InvalidOperationException("Video bounds were not applied.");
            return new(ticket, acknowledgedSequence, ticket.Width, ticket.Height, true, true,
                null, null, commit, null, ReusesResidentTargetFrame: true);
        }
        private int RenderAndPresent()
        {
            if (target?.IsValid != true) throw new InvalidOperationException("No valid Video target.");
            commands!.ClearState();
            int result = render!.RenderToTexture(target.Buffer.NativePointer);
            if (result != 0) throw new InvalidOperationException("Video render did not report exact success.");
            _ = target.Present();
            submittedSequence = checked(submittedSequence + 1);
            render.ReportPresented();
            return result;
        }
        public void Dispose()
        {
            List<Exception> errors = [];
            Cleanup(() => render?.Dispose()); render = null;
            Cleanup(() => target?.Dispose()); target = null;
            Cleanup(() => { commands?.ClearState(); commands?.Flush(); });
            Cleanup(() => commands?.Dispose()); commands = null;
            Cleanup(() => device?.Dispose()); device = null;
            if (errors.Count != 0) throw new AggregateException("Video GPU cleanup failed.", errors);
            void Cleanup(Action action) { try { action(); } catch (Exception exception) { errors.Add(exception); } }
        }
    }
}
