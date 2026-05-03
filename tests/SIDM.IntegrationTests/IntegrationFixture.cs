using Microsoft.Extensions.DependencyInjection;
using SIDM.Core.Engine;
using SIDM.Core.Http;

namespace SIDM.IntegrationTests;

/// <summary>
/// Per-test fixture: starts a fresh in-process HTTP server, builds a real DI graph
/// (AddSidmHttp + AddSidmEngine) pointed at it, and exposes the orchestrator and
/// a scratch directory. Disposing tears the server down.
/// </summary>
internal sealed class IntegrationFixture : IDisposable
{
    public TestHttpServer Server { get; }
    public Uri BaseAddress => Server.BaseAddress;
    public string ScratchDir { get; }
    public DownloadOrchestrator Orchestrator { get; }

    private readonly ServiceProvider _services;

    public IntegrationFixture()
    {
        Server = new TestHttpServer();

        ScratchDir = Path.Combine(Path.GetTempPath(), "sidm-int-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(ScratchDir);

        var collection = new ServiceCollection();
        collection.AddLogging(); // satisfies ILoggerFactory and ILogger<T>
        collection.AddSidmHttp();
        collection.AddSidmEngine();
        _services = collection.BuildServiceProvider();

        Orchestrator = _services.GetRequiredService<DownloadOrchestrator>();
    }

    public string PathFor(string name) => Path.Combine(ScratchDir, name);

    public Uri UrlFor(string path) => new(BaseAddress, path);

    public void Dispose()
    {
        try { Server.Dispose(); } catch { }
        try { _services.Dispose(); } catch { }
        try { Directory.Delete(ScratchDir, recursive: true); } catch { }
    }
}
