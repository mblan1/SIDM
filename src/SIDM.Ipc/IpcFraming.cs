using System.Buffers.Binary;

namespace SIDM.Ipc;

/// <summary>
/// Length-prefixed framing: 4-byte little-endian length followed by UTF-8 JSON.
/// Same format used by Chrome's Native Messaging Host protocol — keeps the
/// stdio bridge in <c>SIDM.BrowserHost</c> identical on both sides.
/// </summary>
public static class IpcFraming
{
    public const int MaxFrameSize = 16 * 1024 * 1024; // 16 MiB safety cap

    public static async Task<IpcMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var lengthBytes = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = await stream.ReadAsync(lengthBytes.AsMemory(read, 4 - read), cancellationToken);
            if (n == 0) return null;
            read += n;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length <= 0 || length > MaxFrameSize)
            throw new InvalidDataException($"frame size {length} out of range");

        var payload = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var n = await stream.ReadAsync(payload.AsMemory(offset, length - offset), cancellationToken);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }

        return IpcSerializer.Deserialize(payload);
    }

    public static async Task WriteAsync(Stream stream, IpcMessage message, CancellationToken cancellationToken = default)
    {
        var payload = IpcSerializer.SerializeToUtf8Bytes(message);
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, payload.Length);
        await stream.WriteAsync(lengthBytes.AsMemory(), cancellationToken);
        await stream.WriteAsync(payload.AsMemory(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
