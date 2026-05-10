using System.IO;
using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIDM.Ipc;

namespace SIDM.App.Services;

/// <summary>
/// Hosted service that listens on the per-user named pipe (see
/// <see cref="PipeNameProvider"/>). Each accepted connection runs in its own
/// task, reads framed JSON, dispatches via <see cref="IpcDispatcher"/>, and
/// writes the response back. Cooperative cancellation tears every connection
/// down on shutdown.
/// </summary>
public sealed class IpcPipeServer : BackgroundService
{
    private const int MaxConcurrentConnections = 4;

    private readonly IpcDispatcher _dispatcher;
    private readonly ILogger<IpcPipeServer> _logger;
    private readonly string _pipeName = PipeNameProvider.ForCurrentUser();

    public IpcPipeServer(IpcDispatcher dispatcher, ILogger<IpcPipeServer> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IPC pipe server listening on \\\\.\\pipe\\{Name}", _pipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName: _pipeName,
                    direction: PipeDirection.InOut,
                    maxNumberOfServerInstances: MaxConcurrentConnections,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create IPC pipe; retrying in 1s");
                await SafeDelay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                return;
            }

            // Run each connection on the threadpool so the listener can accept the next one.
            _ = Task.Run(() => HandleConnectionAsync(pipe, stoppingToken), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                IpcMessage? incoming;
                try
                {
                    incoming = await IpcFraming.ReadAsync(pipe, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
                {
                    _logger.LogWarning(ex, "IPC connection ended with bad frame");
                    break;
                }

                if (incoming is null) break;

                IpcMessage response;
                try
                {
                    response = await _dispatcher.DispatchAsync(incoming, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IPC dispatcher threw");
                    response = new ErrorMessage("DispatcherFailed", ex.Message);
                }

                try
                {
                    await IpcFraming.WriteAsync(pipe, response, cancellationToken);
                }
                catch (IOException)
                {
                    break;
                }
            }
        }
        finally
        {
            try { pipe.Dispose(); } catch { }
        }
    }

    private static async Task SafeDelay(TimeSpan duration, CancellationToken cancellationToken)
    {
        try { await Task.Delay(duration, cancellationToken); }
        catch (OperationCanceledException) { }
    }
}
