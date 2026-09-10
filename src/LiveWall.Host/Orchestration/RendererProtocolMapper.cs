using System.Text.Json;
using LiveWall.Application.Sessions;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Domain.Playback;

namespace LiveWall.Host.Orchestration;

internal static class RendererProtocolMapper
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static RendererEnvelope ToEnvelope(string sessionId, RendererCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(command);
        CommandMapping mapping = command switch
        {
            AttachRendererSurface attach => new(
                RendererCommandTypes.AttachSurface,
                new AttachSurfacePayload(
                    attach.SurfaceWindowHandle,
                    attach.Bounds.Width,
                    attach.Bounds.Height,
                    attach.ScaleFactor)),
            LoadRendererContent load => new(
                RendererCommandTypes.LoadWallpaper,
                new LoadWallpaperPayload(
                    load.Wallpaper.Id.Value,
                    load.Wallpaper.Kind.ToString(),
                    load.CanonicalContentRoot,
                    load.Wallpaper.EntryPoint,
                    new Dictionary<string, JsonElement>())),
            ChangeRendererState { TargetState: PlaybackState.Playing } =>
                new(RendererCommandTypes.Play, new EmptyPayload()),
            ChangeRendererState { TargetState: PlaybackState.Paused } =>
                new(RendererCommandTypes.Pause, new EmptyPayload()),
            ChangeRendererState { TargetState: PlaybackState.Suspended } =>
                new(RendererCommandTypes.Suspend, new EmptyPayload()),
            ChangeRendererState { TargetState: PlaybackState.Stopped } =>
                new(RendererCommandTypes.Shutdown, new ShutdownPayload("playback-stopped")),
            ChangeRendererState state => throw new InvalidOperationException(
                $"Playback state {state.TargetState} has no Renderer Protocol v1 command mapping."),
            SetRendererBounds bounds => new(
                RendererCommandTypes.SetBounds,
                new SetBoundsPayload(
                    bounds.Bounds.X,
                    bounds.Bounds.Y,
                    bounds.Bounds.Width,
                    bounds.Bounds.Height,
                    bounds.ScaleFactor)),
            SetRendererFit fit => new(
                RendererCommandTypes.SetFit,
                new SetFitPayload(fit.FitMode.ToString())),
            SetRendererVolume volume => new(
                RendererCommandTypes.SetVolume,
                new SetVolumePayload(volume.Volume, volume.Muted)),
            ThrottleRenderer throttle => new(
                RendererCommandTypes.Throttle,
                new ThrottlePayload(throttle.FramesPerSecond, throttle.Quality.ToString())),
            ShutdownRenderer shutdown => new(
                RendererCommandTypes.Shutdown,
                new ShutdownPayload(shutdown.Reason)),
            _ => throw new InvalidOperationException(
                $"Renderer command {command.GetType().Name} has no protocol mapping."),
        };

        return CreateEnvelope(sessionId, command.Generation, mapping.Type, mapping.Payload);
    }

    public static RendererEvent? ToEvent(RendererEnvelope envelope) => envelope.Type switch
    {
        RendererEventTypes.SurfaceAttached => new RendererSurfaceAttached(envelope.Generation),
        RendererEventTypes.ContentLoaded => new RendererContentLoaded(envelope.Generation),
        RendererEventTypes.FirstFramePresented => new RendererFirstFramePresented(envelope.Generation),
        RendererEventTypes.PlaybackStateChanged => MapPlaybackState(envelope),
        RendererEventTypes.RecoverableError => MapError(envelope, recoverable: true),
        RendererEventTypes.FatalError => MapError(envelope, recoverable: false),
        RendererEventTypes.ShutdownCompleted => new RendererShutdownCompleted(envelope.Generation),
        RendererEventTypes.TelemetryUpdated => null,
        _ => throw new InvalidOperationException(
            $"Renderer event type '{envelope.Type}' is not valid after initialization."),
    };

    public static RendererEnvelope CreateEnvelope<T>(
        string sessionId,
        long generation,
        string type,
        T payload) =>
        new(
            ProtocolVersion.Version1,
            Guid.NewGuid().ToString("N"),
            sessionId,
            generation,
            type,
            JsonSerializer.SerializeToElement(payload, SerializerOptions));

    public static T ReadPayload<T>(RendererEnvelope envelope) =>
        envelope.Payload.Deserialize<T>(SerializerOptions) ??
        throw new InvalidOperationException($"Renderer message {envelope.Type} has an empty payload.");

    private static RendererPlaybackStateChanged MapPlaybackState(RendererEnvelope envelope)
    {
        PlaybackStateChangedPayload payload = ReadPayload<PlaybackStateChangedPayload>(envelope);
        if (!Enum.TryParse(payload.State, ignoreCase: true, out PlaybackState state))
        {
            throw new InvalidOperationException(
                $"Renderer returned unknown playback state '{payload.State}'.");
        }

        return new RendererPlaybackStateChanged(envelope.Generation, state);
    }

    private static RendererFailed MapError(RendererEnvelope envelope, bool recoverable)
    {
        RendererErrorPayload payload = ReadPayload<RendererErrorPayload>(envelope);
        return new RendererFailed(
            envelope.Generation,
            payload.Code,
            payload.Message,
            recoverable && payload.Retryable);
    }

    private readonly record struct CommandMapping(string Type, object Payload);
}
