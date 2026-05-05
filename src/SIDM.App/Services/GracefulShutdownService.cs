using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIDM.Core.Models;
using SIDM.Core.Persistence;
using SIDM.Data.Repositories;

namespace SIDM.App.Services;

/// <summary>
/// On app shutdown, pauses every active download cleanly so the next launch sees
/// them in <see cref="DownloadStatus.Paused"/> rather than a fake
/// <see cref="DownloadStatus.Downloading"/> (which would trigger auto-resume).
/// Also flushes the segment progress writer so the latest byte counts hit disk
/// before the process exits.
/// </summary>
public sealed class GracefulShutdownService : IHostedService
{
    private readonly DownloadEngine _engine;
    private readonly SegmentProgressWriter _progressWriter;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GracefulShutdownService> _logger;

    public GracefulShutdownService(
        DownloadEngine engine,
        SegmentProgressWriter progressWriter,
        IServiceScopeFactory scopeFactory,
        ILogger<GracefulShutdownService> logger)
    {
        _engine = engine;
        _progressWriter = progressWriter;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Pause all active downloads — they'll be marked Paused by their own
        // cancellation handlers in DownloadEngine.PersistResultAsync. Then mark any
        // that didn't get the chance to update as Paused defensively.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();

        var active = await repo.GetByStatusAsync(DownloadStatus.Downloading, cancellationToken);
        foreach (var d in active)
        {
            if (_engine.Pause(d.Id))
            {
                _logger.LogInformation("Pausing {Id} for graceful shutdown", d.Id);
            }
            else
            {
                // Defensive: engine didn't know about it (e.g., never started this session).
                d.Status = DownloadStatus.Paused;
                await repo.UpdateAsync(d, cancellationToken);
            }
        }

        // Give the engine a moment to update DB state from cancellations before flushing.
        try { await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken); }
        catch (OperationCanceledException) { }

        // Flush any buffered segment progress.
        try { await _progressWriter.FlushAsync(cancellationToken); }
        catch (Exception ex) { _logger.LogWarning(ex, "Final progress flush failed during shutdown"); }
    }
}
