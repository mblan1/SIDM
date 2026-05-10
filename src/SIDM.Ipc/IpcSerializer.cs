using System.Text.Json;

namespace SIDM.Ipc;

public static class IpcSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static byte[] SerializeToUtf8Bytes(IpcMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes<IpcMessage>(message, Options);

    public static IpcMessage? Deserialize(ReadOnlySpan<byte> utf8Bytes) =>
        JsonSerializer.Deserialize<IpcMessage>(utf8Bytes, Options);
}
