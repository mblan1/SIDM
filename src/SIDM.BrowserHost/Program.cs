using System.Buffers.Binary;
using System.Text.Json;

namespace SIDM.BrowserHost;

internal static class Program
{
    private static async Task<int> Main()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        try
        {
            while (await ReadFramedAsync(stdin) is { } message)
            {
                var response = HandleMessage(message);
                await WriteFramedAsync(stdout, response);
            }
        }
        catch (EndOfStreamException)
        {
        }

        return 0;
    }

    private static JsonDocument HandleMessage(JsonDocument message)
    {
        var type = message.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
        return type switch
        {
            "hello" => JsonDocument.Parse("""
                {"type":"hello","appVersion":"0.1.0","capabilities":["download"]}
                """),
            _ => JsonDocument.Parse("""
                {"type":"error","message":"unknown message type"}
                """),
        };
    }

    private static async Task<JsonDocument?> ReadFramedAsync(Stream stream)
    {
        var lengthBytes = new byte[4];
        var read = await stream.ReadAsync(lengthBytes.AsMemory(0, 4));
        if (read == 0) return null;
        if (read < 4) throw new EndOfStreamException();

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("frame size out of range");

        var payload = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var n = await stream.ReadAsync(payload.AsMemory(offset, length - offset));
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }

        return JsonDocument.Parse(payload);
    }

    private static async Task WriteFramedAsync(Stream stream, JsonDocument doc)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(doc.RootElement);
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, json.Length);
        await stream.WriteAsync(lengthBytes);
        await stream.WriteAsync(json);
        await stream.FlushAsync();
    }
}
