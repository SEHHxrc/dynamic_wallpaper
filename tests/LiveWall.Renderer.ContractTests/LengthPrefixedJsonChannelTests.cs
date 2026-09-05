using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LiveWall.Contracts.RendererProtocol;
using LiveWall.Infrastructure.Ipc;

namespace LiveWall.Renderer.ContractTests;

public sealed class LengthPrefixedJsonChannelTests
{
    [Fact]
    public async Task RoundTripsARendererEnvelopeUsingCamelCaseJson()
    {
        using MemoryStream transport = new();
        await using LengthPrefixedJsonChannel framed = new(transport, leaveOpen: true);
        RendererEnvelope expected = new(
            ProtocolVersion.Version1,
            "message-1",
            "session-1",
            4,
            RendererCommandTypes.Play,
            EmptyJson());

        await framed.WriteAsync(expected);
        transport.Position = 0;
        RendererEnvelope actual = await framed.ReadAsync<RendererEnvelope>();

        actual.Should().BeEquivalentTo(expected, options => options.Excluding(envelope => envelope.Payload));
        actual.Payload.GetRawText().Should().Be(expected.Payload.GetRawText());
        Encoding.UTF8.GetString(transport.ToArray()).Should().Contain("\"messageId\":\"message-1\"");
    }

    [Fact]
    public async Task RejectsAnOversizedFrameBeforeReadingItsPayload()
    {
        using MemoryStream transport = FrameWithLength(1025);
        await using LengthPrefixedJsonChannel framed = new(transport, maximumPayloadBytes: 1024);

        Func<Task> act = async () => await framed.ReadAsync<RendererEnvelope>();

        await act.Should().ThrowAsync<ProtocolFrameException>()
            .WithMessage("*outside the allowed range*");
    }

    [Fact]
    public async Task RejectsATruncatedPayload()
    {
        using MemoryStream transport = FrameWithPayload(declaredLength: 20, "{}");
        await using LengthPrefixedJsonChannel framed = new(transport);

        Func<Task> act = async () => await framed.ReadAsync<RendererEnvelope>();

        await act.Should().ThrowAsync<ProtocolFrameException>()
            .WithMessage("*JSON payload*");
    }

    [Fact]
    public async Task RejectsATruncatedLengthPrefix()
    {
        using MemoryStream transport = new([1, 2]);
        await using LengthPrefixedJsonChannel framed = new(transport);

        Func<Task> act = async () => await framed.ReadAsync<RendererEnvelope>();

        await act.Should().ThrowAsync<ProtocolFrameException>()
            .WithMessage("*length prefix*");
    }

    [Fact]
    public async Task RejectsMalformedJson()
    {
        using MemoryStream transport = FrameWithPayload(declaredLength: 1, "{");
        await using LengthPrefixedJsonChannel framed = new(transport);

        Func<Task> act = async () => await framed.ReadAsync<RendererEnvelope>();

        await act.Should().ThrowAsync<ProtocolFrameException>()
            .WithMessage("*malformed*");
    }

    private static MemoryStream FrameWithLength(int length)
    {
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        return new MemoryStream(bytes);
    }

    private static MemoryStream FrameWithPayload(int declaredLength, string payload)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] frame = new byte[sizeof(int) + payloadBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, declaredLength);
        payloadBytes.CopyTo(frame.AsSpan(sizeof(int)));
        return new MemoryStream(frame);
    }

    private static JsonElement EmptyJson() => JsonDocument.Parse("{}").RootElement.Clone();
}
