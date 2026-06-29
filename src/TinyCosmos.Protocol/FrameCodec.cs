using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TinyCosmos.Protocol;

public static class FrameCodec
{
    public const int MaxFrameBytes = 1024 * 1024;

    public static async ValueTask WriteAsync<T>(Stream stream, T message, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        if (bytes.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException("Frame exceeds maximum size.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException("Invalid protocol frame length.");
        }

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(payload, typeInfo)
            ?? throw new InvalidDataException("Protocol frame deserialized to null.");
    }

    private static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("Unexpected end of protocol stream.");
            }

            read += count;
        }
    }
}
