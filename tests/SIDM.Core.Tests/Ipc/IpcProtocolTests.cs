using SIDM.Ipc;

namespace SIDM.Core.Tests.Ipc;

public class IpcProtocolTests
{
    [Fact]
    public void HelloRequest_round_trips_through_serializer()
    {
        var original = new HelloRequest("test-client", "1.2.3");
        var bytes = IpcSerializer.SerializeToUtf8Bytes(original);
        var roundTripped = IpcSerializer.Deserialize(bytes);

        roundTripped.Should().BeOfType<HelloRequest>().Which.Should().Be(original);
    }

    [Fact]
    public void HelloResponse_round_trips_through_serializer()
    {
        var original = new HelloResponse("SIDM", "0.1.0", new[] { "download", "video" });
        var bytes = IpcSerializer.SerializeToUtf8Bytes(original);
        var rt = IpcSerializer.Deserialize(bytes).Should().BeOfType<HelloResponse>().Subject;
        rt.AppName.Should().Be(original.AppName);
        rt.AppVersion.Should().Be(original.AppVersion);
        rt.Capabilities.Should().Equal(original.Capabilities);
    }

    [Fact]
    public void DownloadRequest_round_trips_with_all_optional_fields()
    {
        var original = new DownloadRequestMessage(
            Url: "https://cdn.example.com/installer.exe",
            FileName: "installer.exe",
            SuggestedFolder: @"C:\Downloads",
            Headers: new Dictionary<string, string> { ["X-Auth"] = "token" },
            Cookies: new Dictionary<string, string> { ["session"] = "abc" },
            Referer: "https://referrer.example.com/",
            UserAgent: "Mozilla/5.0",
            ExpectedLength: 12345678,
            Mime: "application/x-msdownload");

        var bytes = IpcSerializer.SerializeToUtf8Bytes(original);
        var rt = IpcSerializer.Deserialize(bytes).Should().BeOfType<DownloadRequestMessage>().Subject;

        rt.Url.Should().Be(original.Url);
        rt.FileName.Should().Be(original.FileName);
        rt.SuggestedFolder.Should().Be(original.SuggestedFolder);
        rt.Headers.Should().BeEquivalentTo(original.Headers);
        rt.Cookies.Should().BeEquivalentTo(original.Cookies);
        rt.Referer.Should().Be(original.Referer);
        rt.UserAgent.Should().Be(original.UserAgent);
        rt.ExpectedLength.Should().Be(original.ExpectedLength);
        rt.Mime.Should().Be(original.Mime);
    }

    [Fact]
    public void DownloadRequest_round_trips_with_only_required_url()
    {
        IpcMessage original = new DownloadRequestMessage(Url: "https://x/y");
        var roundTripped = IpcSerializer.Deserialize(IpcSerializer.SerializeToUtf8Bytes(original));
        roundTripped.Should().BeOfType<DownloadRequestMessage>().Which.Url.Should().Be("https://x/y");
    }

    [Fact]
    public void Type_discriminator_is_emitted_in_camelCase()
    {
        var json = System.Text.Encoding.UTF8.GetString(
            IpcSerializer.SerializeToUtf8Bytes(new HelloRequest("c", "1")));
        json.Should().Contain("\"type\":\"hello\"");
    }

    [Fact]
    public void Unknown_type_discriminator_throws_on_deserialize()
    {
        var json = """{"type":"definitely-not-a-real-type","foo":"bar"}"""u8.ToArray();
        var act = () => IpcSerializer.Deserialize(json);
        act.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task Framing_can_round_trip_a_sequence_of_messages_through_a_MemoryStream()
    {
        using var stream = new MemoryStream();

        IpcMessage[] messages =
        {
            new HelloRequest("test", "1.0"),
            new DownloadRequestMessage("https://a/b", FileName: "b"),
            new ErrorMessage("Simulated", "test"),
            new DownloadResponseMessage(42, "Queued"),
        };

        foreach (var m in messages)
        {
            await IpcFraming.WriteAsync(stream, m);
        }

        stream.Position = 0;

        var read = new List<IpcMessage>();
        while (await IpcFraming.ReadAsync(stream) is { } msg)
        {
            read.Add(msg);
        }

        read.Should().HaveCount(messages.Length);
        read.Should().BeEquivalentTo(messages, opt => opt.RespectingRuntimeTypes());
    }

    [Fact]
    public async Task Framing_returns_null_at_end_of_stream()
    {
        using var stream = new MemoryStream();
        var read = await IpcFraming.ReadAsync(stream);
        read.Should().BeNull();
    }

    [Fact]
    public async Task Framing_throws_for_oversized_frame()
    {
        using var stream = new MemoryStream();
        var lengthBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, IpcFraming.MaxFrameSize + 1);
        await stream.WriteAsync(lengthBytes);
        stream.Position = 0;

        Func<Task> act = async () => { await IpcFraming.ReadAsync(stream); };
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public void PipeNameProvider_is_deterministic_for_current_user()
    {
        var a = PipeNameProvider.ForCurrentUser();
        var b = PipeNameProvider.ForCurrentUser();
        a.Should().Be(b);
        a.Should().StartWith("SIDM.IPC.");
        a.Length.Should().Be("SIDM.IPC.".Length + 8);
    }
}
