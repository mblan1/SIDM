using System.IO.Pipes;
using SIDM.Ipc;

namespace SIDM.IntegrationTests;

/// <summary>
/// Real named-pipe round-trip tests: spin up a server in one task, connect a
/// client from another, exchange framed JSON. Validates the wire that
/// SIDM.BrowserHost will use to talk to SIDM.App.
/// </summary>
public class IpcPipeRoundtripTests
{
    [Fact]
    public async Task Client_sends_HelloRequest_and_receives_HelloResponse()
    {
        var pipeName = $"SIDM.test.{Guid.NewGuid():N}".Substring(0, 24);

        var serverDone = RunServerOnceAsync(pipeName, (incoming) =>
        {
            incoming.Should().BeOfType<HelloRequest>();
            return Task.FromResult<IpcMessage>(new HelloResponse("SIDM", "0.1.0", new[] { "download" }));
        });

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);

        await IpcFraming.WriteAsync(client, new HelloRequest("test-client", "1.0"));
        var response = await IpcFraming.ReadAsync(client);

        response.Should().BeOfType<HelloResponse>();
        var hello = (HelloResponse)response!;
        hello.AppVersion.Should().Be("0.1.0");
        hello.Capabilities.Should().Contain("download");

        await serverDone;
    }

    [Fact]
    public async Task Client_sends_DownloadRequest_and_receives_DownloadResponse()
    {
        var pipeName = $"SIDM.test.{Guid.NewGuid():N}".Substring(0, 24);

        DownloadRequestMessage? capturedRequest = null;
        var serverDone = RunServerOnceAsync(pipeName, (incoming) =>
        {
            capturedRequest = (DownloadRequestMessage)incoming;
            return Task.FromResult<IpcMessage>(new DownloadResponseMessage(DownloadId: 99, Status: "Queued"));
        });

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);

        var request = new DownloadRequestMessage(
            Url: "https://cdn.example/installer.exe",
            FileName: "installer.exe",
            Headers: new Dictionary<string, string> { ["X-Custom"] = "value" },
            Cookies: new Dictionary<string, string> { ["session"] = "abc" },
            UserAgent: "test-agent",
            Referer: "https://referrer/");
        await IpcFraming.WriteAsync(client, request);
        var response = await IpcFraming.ReadAsync(client);

        await serverDone;

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Should().BeEquivalentTo(request);

        response.Should().BeOfType<DownloadResponseMessage>();
        var dl = (DownloadResponseMessage)response!;
        dl.DownloadId.Should().Be(99);
        dl.Status.Should().Be("Queued");
    }

    [Fact]
    public async Task Server_can_handle_multiple_messages_in_one_connection()
    {
        var pipeName = $"SIDM.test.{Guid.NewGuid():N}".Substring(0, 24);

        var serverDone = RunServerLoopAsync(pipeName, (incoming) => Task.FromResult<IpcMessage>(incoming switch
        {
            HelloRequest => new HelloResponse("SIDM", "0.1.0", Array.Empty<string>()),
            DownloadRequestMessage dl => new DownloadResponseMessage(7, "Queued"),
            _ => new ErrorMessage("Unknown"),
        }));

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);

        await IpcFraming.WriteAsync(client, new HelloRequest("c", "1"));
        var first = await IpcFraming.ReadAsync(client);
        first.Should().BeOfType<HelloResponse>();

        await IpcFraming.WriteAsync(client, new DownloadRequestMessage("https://a/b"));
        var second = await IpcFraming.ReadAsync(client);
        second.Should().BeOfType<DownloadResponseMessage>().Which.DownloadId.Should().Be(7);

        client.Dispose();
        await serverDone;
    }

    /// <summary>One-shot server: accept one connection, read one message, write one response, exit.</summary>
    private static Task RunServerOnceAsync(string pipeName, Func<IpcMessage, Task<IpcMessage>> handler) =>
        Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();

            var incoming = await IpcFraming.ReadAsync(server);
            if (incoming is null) return;
            var response = await handler(incoming);
            await IpcFraming.WriteAsync(server, response);
        });

    /// <summary>Loop server: accept one connection, then read+respond until the client disconnects.</summary>
    private static Task RunServerLoopAsync(string pipeName, Func<IpcMessage, Task<IpcMessage>> handler) =>
        Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();

            while (server.IsConnected)
            {
                IpcMessage? incoming;
                try { incoming = await IpcFraming.ReadAsync(server); }
                catch { return; }
                if (incoming is null) return;

                var response = await handler(incoming);
                try { await IpcFraming.WriteAsync(server, response); }
                catch { return; }
            }
        });
}
