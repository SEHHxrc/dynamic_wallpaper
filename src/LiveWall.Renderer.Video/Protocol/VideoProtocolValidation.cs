using System.Text.Json;
using System.Text.Json.Serialization;
using LiveWall.Contracts.RendererProtocol;

namespace LiveWall.Renderer.Video.Protocol;

internal sealed class VideoProtocolValidation(string sessionId)
{
    private readonly HashSet<string> messageIds = new(StringComparer.Ordinal);
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public void Validate(RendererEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Protocol is not { Major: 1, Minor: >= 0 })
            throw new VideoCommandRejected(RendererErrorCodes.ProtocolVersionUnsupported);
        if (envelope.SessionId != sessionId || envelope.Generation < 0 ||
            string.IsNullOrWhiteSpace(envelope.MessageId) || envelope.MessageId.Length > 64 ||
            string.IsNullOrWhiteSpace(envelope.Type) || envelope.Type.Length > 64 ||
            envelope.Payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid Renderer envelope.");
        // Fail closed on exhaustion; never evict IDs and allow an old command to replay.
        if (messageIds.Count >= 65536 || !messageIds.Add(envelope.MessageId))
            throw new InvalidDataException("Duplicate message or connection message budget exhausted.");
    }

    public static T Payload<T>(RendererEnvelope envelope) where T : class =>
        envelope.Payload.Deserialize<T>(Json) ?? throw new InvalidDataException("Missing command payload.");

    public static RendererEnvelope Event<T>(string sessionId, long generation, string type, T payload) =>
        new(ProtocolVersion.Version1, Guid.NewGuid().ToString("N"), sessionId, generation, type,
            JsonSerializer.SerializeToElement(payload, Json));
}
