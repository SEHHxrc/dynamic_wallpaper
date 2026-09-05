using System.Text.Json;
using FluentAssertions;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Contracts.Rpc;

namespace LiveWall.Renderer.ContractTests;

public sealed class ProtocolSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void UiEventSerializesStateRevisionInCamelCase()
    {
        RpcEvent value = new(1, "event-1", 42, UiEventTypes.AppStateChanged, EmptyJson());

        string json = JsonSerializer.Serialize(value, Options);

        json.Should().Contain("\"stateRevision\":42");
        json.Should().Contain("\"type\":\"AppStateChanged\"");
    }

    [Fact]
    public void RendererErrorUsesTheSharedRetryableFieldName()
    {
        RendererErrorPayload value = new("renderer.internal", "Failure", true, null);

        string json = JsonSerializer.Serialize(value, Options);

        json.Should().Contain("\"retryable\":true");
        json.Should().NotContain("isTransient");
    }

    [Fact]
    public void RendererHelloCarriesTheOneTimeAuthenticationToken()
    {
        HelloPayload value = new("video", "1.0.0", ["video"], new string('a', 64));

        string json = JsonSerializer.Serialize(value, Options);

        json.Should().Contain("\"authenticationToken\":");
    }

    [Fact]
    public void RendererEnvelopeIgnoresUnknownMinorFieldsWhenReading()
    {
        const string json = """
            {
              "protocol": { "major": 1, "minor": 1, "future": true },
              "messageId": "message-1",
              "sessionId": "session-1",
              "generation": 3,
              "type": "Hello",
              "payload": {},
              "futureEnvelopeField": "ignored"
            }
            """;

        RendererEnvelope? envelope = JsonSerializer.Deserialize<RendererEnvelope>(json, Options);

        envelope.Should().NotBeNull();
        envelope!.Protocol.Should().Be(new ProtocolVersion(1, 1));
        envelope.Generation.Should().Be(3);
    }

    [Fact]
    public void DeferredCapturePreviewIsNotAdvertisedInProtocolV1()
    {
        typeof(RendererCommandTypes)
            .GetFields()
            .Select(field => field.GetRawConstantValue())
            .Should()
            .NotContain("CapturePreview");
    }

    private static JsonElement EmptyJson() => JsonDocument.Parse("{}").RootElement.Clone();
}
