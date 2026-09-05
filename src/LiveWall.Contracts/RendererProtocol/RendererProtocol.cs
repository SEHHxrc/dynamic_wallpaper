using System.Text.Json;

namespace LiveWall.Contracts.RendererProtocol;

public sealed record ProtocolVersion(int Major, int Minor)
{
    public static ProtocolVersion Version1 { get; } = new(1, 0);
}

public sealed record RendererEnvelope(
    ProtocolVersion Protocol,
    string MessageId,
    string SessionId,
    long Generation,
    string Type,
    JsonElement Payload);

public static class RendererCommandTypes
{
    public const string Initialize = nameof(Initialize);
    public const string AttachSurface = nameof(AttachSurface);
    public const string LoadWallpaper = nameof(LoadWallpaper);
    public const string Play = nameof(Play);
    public const string Pause = nameof(Pause);
    public const string Throttle = nameof(Throttle);
    public const string Suspend = nameof(Suspend);
    public const string Resume = nameof(Resume);
    public const string SetBounds = nameof(SetBounds);
    public const string SetFit = nameof(SetFit);
    public const string SetVolume = nameof(SetVolume);
    public const string SetProperties = nameof(SetProperties);
    public const string SetAudioFrame = nameof(SetAudioFrame);
    public const string Shutdown = nameof(Shutdown);
}

public static class RendererEventTypes
{
    public const string Hello = nameof(Hello);
    public const string Initialized = nameof(Initialized);
    public const string SurfaceAttached = nameof(SurfaceAttached);
    public const string ContentLoaded = nameof(ContentLoaded);
    public const string FirstFramePresented = nameof(FirstFramePresented);
    public const string PlaybackStateChanged = nameof(PlaybackStateChanged);
    public const string TelemetryUpdated = nameof(TelemetryUpdated);
    public const string RecoverableError = nameof(RecoverableError);
    public const string FatalError = nameof(FatalError);
    public const string ShutdownCompleted = nameof(ShutdownCompleted);
}

public sealed record InitializePayload(
    string RendererId,
    IReadOnlyList<string> RequestedCapabilities,
    string Locale);

public sealed record HelloPayload(
    string RendererName,
    string RendererVersion,
    IReadOnlyList<string> SupportedCapabilities,
    string AuthenticationToken);

public sealed record AttachSurfacePayload(ulong WindowHandle, int Width, int Height, double ScaleFactor);

public sealed record EmptyPayload;

public sealed record LoadWallpaperPayload(
    string WallpaperId,
    string Kind,
    string ContentRoot,
    string EntryPoint,
    IReadOnlyDictionary<string, JsonElement> Properties);

public sealed record SetBoundsPayload(int X, int Y, int Width, int Height, double ScaleFactor);

public sealed record SetFitPayload(string FitMode);

public sealed record SetVolumePayload(double Volume, bool Muted);

public sealed record ThrottlePayload(int FramesPerSecond, string Quality);

public sealed record SetPropertiesPayload(IReadOnlyDictionary<string, JsonElement> Properties);

public sealed record SetAudioFramePayload(
    int FormatVersion,
    long Sequence,
    IReadOnlyList<float> FrequencyBins);

public sealed record ShutdownPayload(string Reason);

public sealed record InitializedPayload(IReadOnlyList<string> EnabledCapabilities);

public sealed record SurfaceAttachedPayload(ulong WindowHandle);

public sealed record ContentLoadedPayload(string WallpaperId);

public sealed record FirstFramePresentedPayload(long PresentationTimestampMilliseconds);

public sealed record PlaybackStateChangedPayload(string State, int? FramesPerSecond, string? Quality);

public sealed record ShutdownCompletedPayload(string Reason);

public sealed record RendererErrorPayload(
    string Code,
    string Message,
    bool Retryable,
    IReadOnlyDictionary<string, string>? Details);

public sealed record RendererTelemetryPayload(
    double FramesPerSecond,
    double CpuPercent,
    long WorkingSetBytes,
    long? GpuMemoryBytes,
    long DroppedFrames);

public static class RendererErrorCodes
{
    public const string ProtocolMessageUnsupported = "renderer.protocol.message_unsupported";
    public const string ProtocolVersionUnsupported = "renderer.protocol.version_unsupported";
    public const string ContentUnsupported = "renderer.content.unsupported";
    public const string ContentInvalidPath = "renderer.content.invalid_path";
    public const string Internal = "renderer.internal";
}
