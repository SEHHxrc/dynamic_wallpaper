using System.Buffers.Binary;
using System.Text.Json;

namespace LiveWall.Infrastructure.Ipc;

/// <summary>
/// Reads and writes UTF-8 JSON frames prefixed by a four-byte little-endian payload length.
/// </summary>
public sealed class LengthPrefixedJsonChannel : IAsyncDisposable
{
    public const int DefaultMaximumPayloadBytes = 1024 * 1024;

    private readonly Stream stream;
    private readonly JsonSerializerOptions serializerOptions;
    private readonly bool leaveOpen;
    private readonly SemaphoreSlim readLock = new(1, 1);
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private bool disposed;

    public LengthPrefixedJsonChannel(
        Stream stream,
        JsonSerializerOptions? serializerOptions = null,
        int maximumPayloadBytes = DefaultMaximumPayloadBytes,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException("The framed stream must be readable and writable.", nameof(stream));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);

        this.stream = stream;
        this.serializerOptions = serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        this.leaveOpen = leaveOpen;
        MaximumPayloadBytes = maximumPayloadBytes;
    }

    public int MaximumPayloadBytes { get; }

    public async ValueTask WriteAsync<T>(T value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, serializerOptions);
        ValidatePayloadLength(payload.Length);

        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async ValueTask<T> ReadAsync<T>(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            byte[] prefix = new byte[sizeof(int)];
            await ReadExactlyAsync(prefix, "length prefix", cancellationToken).ConfigureAwait(false);

            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            ValidatePayloadLength(payloadLength);

            byte[] payload = new byte[payloadLength];
            await ReadExactlyAsync(payload, "JSON payload", cancellationToken).ConfigureAwait(false);

            try
            {
                T? value = JsonSerializer.Deserialize<T>(payload, serializerOptions);
                return value ?? throw new ProtocolFrameException("The JSON frame contains null where a value was required.");
            }
            catch (JsonException exception)
            {
                throw new ProtocolFrameException("The JSON frame payload is malformed.", exception);
            }
        }
        finally
        {
            readLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!leaveOpen)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        readLock.Dispose();
        writeLock.Dispose();
    }

    private void ValidatePayloadLength(int payloadLength)
    {
        if (payloadLength <= 0 || payloadLength > MaximumPayloadBytes)
        {
            throw new ProtocolFrameException(
                $"Frame payload length {payloadLength} is outside the allowed range 1..{MaximumPayloadBytes} bytes.");
        }
    }

    private async ValueTask ReadExactlyAsync(
        Memory<byte> buffer,
        string framePart,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ProtocolFrameException(
                    $"The stream ended after {totalRead} of {buffer.Length} bytes in the frame {framePart}.");
            }

            totalRead += read;
        }
    }
}
