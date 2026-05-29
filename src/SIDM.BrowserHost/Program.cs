using System.Diagnostics;
using System.IO.Pipes;
using SIDM.Ipc;

namespace SIDM.BrowserHost;

/// <summary>
/// Native Messaging Host bridge. Chrome/Edge/Firefox spawns this exe and pipes
/// length-prefixed JSON to/from us via stdin/stdout. We forward those frames
/// to the running SIDM.App over a named pipe, and pipe the responses back.
///
/// If SIDM.App isn't running, we attempt to launch it and retry the connection
/// with a short backoff before giving up.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan AppLaunchTimeout = TimeSpan.FromSeconds(10);

    private static async Task<int> Main(string[] args)
    {
        // Optional: --probe just confirms the host is registered correctly without
        // requiring browser-side stdin to be connected.
        if (args.Length == 1 && args[0] == "--probe")
        {
            Console.Error.WriteLine($"SIDM.BrowserHost ready. Pipe: {PipeNameProvider.ForCurrentUser()}");
            return 0;
        }

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        try
        {
            await BridgeAsync(stdin, stdout, CancellationToken.None);
            return 0;
        }
        catch (EndOfStreamException)
        {
            return 0; // browser closed the stream
        }
        catch (Exception ex)
        {
            try
            {
                await IpcFraming.WriteAsync(stdout, new ErrorMessage("HostError", ex.Message));
            }
            catch { }
            return 1;
        }
    }

    private static async Task BridgeAsync(Stream stdin, Stream stdout, CancellationToken cancellationToken)
    {
        await using var pipe = await ConnectToAppAsync(cancellationToken);

        // Two pumps: stdin → pipe and pipe → stdout. Each runs until its source ends.
        var stdinToPipe = PumpAsync(stdin, pipe, cancellationToken);
        var pipeToStdout = PumpAsync(pipe, stdout, cancellationToken);

        await Task.WhenAny(stdinToPipe, pipeToStdout);
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var msg = await IpcFraming.ReadAsync(source, cancellationToken);
                if (msg is null) return;
                await IpcFraming.WriteAsync(destination, msg, cancellationToken);
            }
        }
        catch (Exception)
        {
            // EOS or pipe broken — pump ends.
        }
    }

    private static async Task<NamedPipeClientStream> ConnectToAppAsync(CancellationToken cancellationToken)
    {
        var name = PipeNameProvider.ForCurrentUser();
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync((int)TimeSpan.FromMilliseconds(500).TotalMilliseconds, cancellationToken);
            return pipe;
        }
        catch (TimeoutException)
        {
            // App not running — try to launch it.
            await pipe.DisposeAsync();
            TryLaunchApp();

            pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)AppLaunchTimeout.TotalMilliseconds, cancellationToken);
            return pipe;
        }
    }

    private static void TryLaunchApp()
    {
        try
        {
            // SIDM.App.exe lives next to SIDM.BrowserHost.exe in our installer layout.
            var hostPath = Environment.ProcessPath ?? "";
            var dir = Path.GetDirectoryName(hostPath);
            if (string.IsNullOrEmpty(dir)) return;

            var candidates = new[]
            {
                Path.Combine(dir, "SIDM.App.exe"),
                Path.Combine(dir, "..", "SIDM.App.exe"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    // --background tells SIDM.App this is a download-capture
                    // cold launch: suppress the main window, show a tray cue,
                    // and just open the download dialog. Without it, every
                    // browser download would pop the full main window.
                    var psi = new ProcessStartInfo
                    {
                        FileName = candidate,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    psi.ArgumentList.Add("--background");
                    Process.Start(psi);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to launch SIDM.App.exe: {ex.Message}");
        }
    }
}
