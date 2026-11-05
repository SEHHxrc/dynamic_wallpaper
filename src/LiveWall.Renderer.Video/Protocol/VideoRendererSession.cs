using System.Threading.Channels;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Infrastructure.Ipc;

namespace LiveWall.Renderer.Video.Protocol;

// One receive pump, one command writer. The pump may cancel preparation for a validated
// Shutdown/EOF, but only the actor may change protocol state or write events.
internal sealed class VideoRendererSession(string sessionId, IVideoSessionBackend backend,
    TimeSpan? stageBudget = null)
{
    public static IReadOnlyList<string> Capabilities { get; } = ["HwndChild", "DirectComposition", "Video"];
    private readonly TimeSpan budget = stageBudget ?? TimeSpan.FromSeconds(10);
    private VideoSessionTransaction? state;
    private long generation;

    public async Task RunAsync(LengthPrefixedJsonChannel pipe, string authenticationToken, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationToken);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using CancellationTokenSource stopPreparation = new();
        Channel<RendererEnvelope> commands = Channel.CreateBounded<RendererEnvelope>(new BoundedChannelOptions(32)
            { SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false });
        VideoProtocolValidation validator = new(sessionId);
        bool backendDisposed = false;
        Task pump = Task.CompletedTask;
        try
        {
            await Send(pipe, 0, RendererEventTypes.Hello,
                new HelloPayload("LiveWall.Renderer.Video", "0.1.0", Capabilities, authenticationToken), token).ConfigureAwait(false);
            authenticationToken = "";
            pump = PumpAsync();
            while (true)
            {
                RendererEnvelope command;
                if (state is null)
                {
                    using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                    handshake.CancelAfter(budget);
                    command = await ReadNextAsync(handshake.Token).ConfigureAwait(false);
                }
                else command = await ReadNextAsync(token).ConfigureAwait(false);
                if (command.Type == RendererCommandTypes.Shutdown)
                {
                    if (command.Generation < generation) throw new InvalidDataException("Stale Shutdown.");
                    ShutdownPayload shutdown = VideoProtocolValidation.Payload<ShutdownPayload>(command);
                    if (string.IsNullOrWhiteSpace(shutdown.Reason) || shutdown.Reason.Length > 128)
                        throw new InvalidDataException("Invalid shutdown reason.");
                    state?.BeginShutdown();
                    backendDisposed = true;
                    await backend.DisposeAsync().ConfigureAwait(false);
                    state?.ShutdownCompleted();
                    await Send(pipe, generation, RendererEventTypes.ShutdownCompleted,
                        new ShutdownCompletedPayload(shutdown.Reason), token).ConfigureAwait(false);
                    return;
                }
                if (stopPreparation.IsCancellationRequested) continue;
                using CancellationTokenSource phase = CancellationTokenSource.CreateLinkedTokenSource(
                    token, stopPreparation.Token, lifetime.Token);
                phase.CancelAfter(budget);
                try { await ExecuteAsync(pipe, command, phase.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stopPreparation.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    // Only Shutdown may follow; no late frame or partial preparation is committed.
                    state?.BeginShutdown();
                }
            }
        }
        catch (Exception exception)
        {
            string failedPhase = state?.Phase.ToString() ?? "Handshake";
            state?.Fault();
            try
            {
                using CancellationTokenSource errorBudget = new(TimeSpan.FromSeconds(1));
                await Send(pipe, generation, RendererEventTypes.FatalError,
                    new RendererErrorPayload(exception is VideoCommandRejected rejected ? rejected.Code :
                            exception is OperationCanceledException && !token.IsCancellationRequested ? "renderer.video.stage_timeout" :
                            exception is InvalidDataException ? "renderer.protocol.invalid_state" : RendererErrorCodes.Internal,
                        "Video session rejected or failed an operation.", false,
                        new Dictionary<string, string> { ["phase"] = failedPhase }), errorBudget.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* The Host may already have closed its pipe. */ }
            throw;
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await stopPreparation.CancelAsync().ConfigureAwait(false);
            try { await pump.ConfigureAwait(false); } catch (Exception) { /* Reported through channel completion. */ }
            if (!backendDisposed) await backend.DisposeAsync().ConfigureAwait(false);
        }

        async Task PumpAsync()
        {
            try
            {
                while (true)
                {
                    RendererEnvelope command = await pipe.ReadAsync<RendererEnvelope>(lifetime.Token).ConfigureAwait(false);
                    validator.Validate(command);
                    if (command.Type == RendererCommandTypes.Shutdown && command.Generation >= Volatile.Read(ref generation))
                    {
                        ShutdownPayload shutdown = VideoProtocolValidation.Payload<ShutdownPayload>(command);
                        if (string.IsNullOrWhiteSpace(shutdown.Reason) || shutdown.Reason.Length > 128)
                            throw new InvalidDataException("Invalid shutdown reason.");
                        await stopPreparation.CancelAsync().ConfigureAwait(false);
                        await commands.Writer.WriteAsync(command, lifetime.Token).ConfigureAwait(false);
                        break;
                    }
                    if (!commands.Writer.TryWrite(command)) throw new InvalidDataException("Renderer command queue is full.");
                }
                commands.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
                commands.Writer.TryComplete(exception);
            }
        }

        async Task<RendererEnvelope> ReadNextAsync(CancellationToken cancellation)
        {
            Task<RendererEnvelope> read = commands.Reader.ReadAsync(cancellation).AsTask();
            while (true)
            {
                Task completed = await Task.WhenAny(read, backend.Failure, Task.Delay(250, cancellation)).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (completed == backend.Failure) await backend.Failure.ConfigureAwait(false);
                if (completed == read) return await read.ConfigureAwait(false);
                backend.CheckHealth(); // Normal client API only on this control actor.
            }
        }
    }

    private async Task ExecuteAsync(LengthPrefixedJsonChannel pipe, RendererEnvelope command, CancellationToken token)
    {
        if (command.Type == RendererCommandTypes.Initialize)
        {
            if (state is not null || command.Generation <= 0) throw new InvalidDataException("Duplicate/invalid Initialize.");
            InitializePayload initialize = VideoProtocolValidation.Payload<InitializePayload>(command);
            if (string.IsNullOrWhiteSpace(initialize.RendererId) || initialize.RendererId.Length > 128 ||
                string.IsNullOrWhiteSpace(initialize.Locale) || initialize.Locale.Length > 64 ||
                initialize.RequestedCapabilities is null || initialize.RequestedCapabilities.Count > 16 ||
                initialize.RequestedCapabilities.Any(capability => !Capabilities.Contains(capability)))
                throw new InvalidDataException("Unsupported initialization.");
            state = new(sessionId, command.Generation);
            Volatile.Write(ref generation, command.Generation);
            state.BeginInitialize();
            await backend.InitializeAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            state.Initialized();
            await Send(pipe, generation, RendererEventTypes.Initialized,
                new InitializedPayload(initialize.RequestedCapabilities), token).ConfigureAwait(false);
            return;
        }
        if (state is null) throw new InvalidDataException("Initialize is required.");
        switch (command.Type)
        {
            case RendererCommandTypes.AttachSurface:
                AttachSurfacePayload attach = VideoProtocolValidation.Payload<AttachSurfacePayload>(command);
                state.BeginAttach(command.Generation, attach);
                Volatile.Write(ref generation, state.Generation);
                ulong child = await backend.AttachAsync(attach, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                state.SurfaceAttached();
                await Send(pipe, generation, RendererEventTypes.SurfaceAttached, new SurfaceAttachedPayload(child), token).ConfigureAwait(false);
                if (state.PendingFrame is not null) await SubmitAsync(pipe, bounds: false, token).ConfigureAwait(false);
                break;
            case RendererCommandTypes.LoadWallpaper:
                LoadWallpaperPayload load = VideoProtocolValidation.Payload<LoadWallpaperPayload>(command);
                string media;
                try { media = VideoContentPath.Resolve(load); }
                catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException)
                { throw new VideoCommandRejected(RendererErrorCodes.ContentInvalidPath); }
                state.BeginLoad(command.Generation);
                await backend.LoadAsync(media, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                state.ContentLoaded();
                await Send(pipe, generation, RendererEventTypes.ContentLoaded, new ContentLoadedPayload(load.WallpaperId), token).ConfigureAwait(false);
                await SubmitAsync(pipe, bounds: false, token).ConfigureAwait(false);
                break;
            case RendererCommandTypes.SetBounds:
                state.BeginBounds(command.Generation, VideoProtocolValidation.Payload<SetBoundsPayload>(command));
                await SubmitAsync(pipe, bounds: true, token).ConfigureAwait(false);
                break;
            case RendererCommandTypes.Play:
            case RendererCommandTypes.Resume:
            case RendererCommandTypes.Pause:
            case RendererCommandTypes.Suspend:
                state.ValidatePlayback(command.Generation);
                string playback = command.Type is RendererCommandTypes.Play or RendererCommandTypes.Resume ? "Playing" :
                    command.Type == RendererCommandTypes.Pause ? "Paused" : "Suspended";
                await backend.PlaybackAsync(playback, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await Send(pipe, generation, RendererEventTypes.PlaybackStateChanged,
                    new PlaybackStateChangedPayload(playback, null, null), token).ConfigureAwait(false);
                break;
            case RendererCommandTypes.SetFit:
                state.ValidatePlayback(command.Generation);
                string fit = VideoProtocolValidation.Payload<SetFitPayload>(command).FitMode;
                if (fit is not ("Cover" or "Contain" or "Stretch" or "Center")) throw new InvalidDataException("Invalid fit.");
                await backend.FitAsync(fit, token).ConfigureAwait(false);
                break;
            case RendererCommandTypes.SetVolume:
                state.ValidatePlayback(command.Generation);
                SetVolumePayload volume = VideoProtocolValidation.Payload<SetVolumePayload>(command);
                if (!double.IsFinite(volume.Volume) || volume.Volume is < 0 or > 1) throw new InvalidDataException("Invalid volume.");
                await backend.VolumeAsync(volume.Volume, volume.Muted, token).ConfigureAwait(false);
                break;
            default:
                throw new VideoCommandRejected(RendererErrorCodes.ProtocolMessageUnsupported);
        }
    }

    private async Task SubmitAsync(LengthPrefixedJsonChannel pipe, bool bounds, CancellationToken token)
    {
        VideoFrameTicket ticket = state!.PendingFrame ?? throw new InvalidOperationException("No frame requested.");
        VideoFrameSubmission submission = bounds ? await backend.BoundsAsync(ticket, token).ConfigureAwait(false) :
            await backend.SubmitAsync(ticket, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        VideoFrameRejection rejection = state.TryAcceptFrame(submission);
        if (rejection != VideoFrameRejection.None) throw new InvalidDataException($"Frame submission rejected: {rejection}.");
        await Send(pipe, generation, RendererEventTypes.FirstFramePresented,
            new FirstFramePresentedPayload(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), token).ConfigureAwait(false);
    }

    private Task Send<T>(LengthPrefixedJsonChannel pipe, long currentGeneration, string type, T payload, CancellationToken token) =>
        pipe.WriteAsync(VideoProtocolValidation.Event(sessionId, currentGeneration, type, payload), token).AsTask();
}

internal sealed class VideoCommandRejected(string code) : IOException("Video command was rejected.")
{
    public string Code { get; } = code;
}
