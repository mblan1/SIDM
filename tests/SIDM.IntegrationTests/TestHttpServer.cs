using System.Net;
using System.Text.RegularExpressions;

namespace SIDM.IntegrationTests;

/// <summary>
/// Minimal in-process HTTP server backed by <see cref="HttpListener"/>. Lets each
/// integration test register per-path handlers that observe the request and write
/// the response — full control over Range semantics and fault injection without
/// any third-party HTTP-mock library quirks.
/// </summary>
internal sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Dictionary<string, Func<HttpListenerRequest, HttpListenerResponse, Task>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public Uri BaseAddress { get; }

    public TestHttpServer()
    {
        var port = GetFreePort();
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseAddress.AbsoluteUri);
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public TestHttpServer Map(string path, Func<HttpListenerRequest, HttpListenerResponse, Task> handler)
    {
        _handlers[path] = handler;
        return this;
    }

    /// <summary>Stubs <paramref name="path"/> as a fully range-honoring resource.</summary>
    public TestHttpServer MapRangeResource(string path, byte[] body)
    {
        return Map(path, async (req, resp) =>
        {
            if (req.HttpMethod == "HEAD")
            {
                resp.StatusCode = 200;
                resp.Headers["Accept-Ranges"] = "bytes";
                resp.ContentLength64 = body.Length;
                resp.ContentType = "application/octet-stream";
                resp.OutputStream.Close();
                return;
            }

            var (start, end) = ParseRange(req.Headers["Range"], body.Length);
            var slice = body.AsMemory((int)start, (int)(end - start + 1));
            resp.StatusCode = 206;
            resp.Headers["Accept-Ranges"] = "bytes";
            resp.Headers["Content-Range"] = $"bytes {start}-{end}/{body.Length}";
            resp.ContentLength64 = slice.Length;
            resp.ContentType = "application/octet-stream";
            await resp.OutputStream.WriteAsync(slice);
            resp.OutputStream.Close();
        });
    }

    /// <summary>
    /// Stubs a resource that fails the first <paramref name="initialFailures"/> GETs
    /// (with <paramref name="failureStatusCode"/>) before behaving normally.
    /// </summary>
    public TestHttpServer MapFlakyRangeResource(string path, byte[] body, int initialFailures, int failureStatusCode = 503)
    {
        var failed = 0;
        return Map(path, async (req, resp) =>
        {
            if (req.HttpMethod == "HEAD")
            {
                resp.StatusCode = 200;
                resp.Headers["Accept-Ranges"] = "bytes";
                resp.ContentLength64 = body.Length;
                resp.OutputStream.Close();
                return;
            }

            if (Interlocked.Increment(ref failed) <= initialFailures)
            {
                resp.StatusCode = failureStatusCode;
                resp.OutputStream.Close();
                return;
            }

            var (start, end) = ParseRange(req.Headers["Range"], body.Length);
            var slice = body.AsMemory((int)start, (int)(end - start + 1));
            resp.StatusCode = 206;
            resp.Headers["Content-Range"] = $"bytes {start}-{end}/{body.Length}";
            resp.ContentLength64 = slice.Length;
            await resp.OutputStream.WriteAsync(slice);
            resp.OutputStream.Close();
        });
    }

    /// <summary>
    /// Stubs a resource that advertises Accept-Ranges but always returns 200 OK to
    /// ranged GETs. Models a misbehaving CDN.
    /// </summary>
    public TestHttpServer MapLyingRangeResource(string path, byte[] body)
    {
        return Map(path, async (req, resp) =>
        {
            if (req.HttpMethod == "HEAD")
            {
                resp.StatusCode = 200;
                resp.Headers["Accept-Ranges"] = "bytes";
                resp.ContentLength64 = body.Length;
                resp.OutputStream.Close();
                return;
            }

            // Ignore Range header. Return 200 with full body.
            resp.StatusCode = 200;
            resp.ContentLength64 = body.Length;
            resp.ContentType = "application/octet-stream";
            await resp.OutputStream.WriteAsync(body);
            resp.OutputStream.Close();
        });
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = Task.Run(async () =>
            {
                try
                {
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
                    if (_handlers.TryGetValue(path, out var handler))
                    {
                        await handler(ctx.Request, ctx.Response);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.OutputStream.Close();
                    }
                }
                catch
                {
                    try { ctx.Response.StatusCode = 500; ctx.Response.OutputStream.Close(); }
                    catch { }
                }
            });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _cts.Dispose();
    }

    private static (long start, long end) ParseRange(string? rangeHeader, long total)
    {
        if (string.IsNullOrEmpty(rangeHeader)) return (0, total - 1);
        var m = Regex.Match(rangeHeader, @"bytes=(\d+)-(\d*)");
        if (!m.Success) return (0, total - 1);
        var start = long.Parse(m.Groups[1].Value);
        var end = string.IsNullOrEmpty(m.Groups[2].Value) ? total - 1 : long.Parse(m.Groups[2].Value);
        return (start, end);
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
