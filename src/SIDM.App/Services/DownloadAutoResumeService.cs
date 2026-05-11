using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIDM.Core.Models;
using SIDM.Core.Persistence;

namespace SIDM.App.Services;

/// <summary>
/// On app startup, finds downloads left in <see cref="DownloadStatus.Downloading"/>
/// or <see cref="DownloadStatus.Probing"/> by an unclean shutdown (process killed,
/// crash, power loss) and re-kicks them through the <see cref="DownloadEngine"/>.
///
/// Downloads in <see cref="DownloadStatus.Paused"/> are left alone — the user
/// explicitly stopped them and would be surprised by automatic resumption.
/// </summary>
public sealed class DownloadAutoResumeService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DownloadQueue _queue;
    private readonly BandwidthSettingsService _bandwidth;
    private readonly ILogger<DownloadAutoResumeService> _logger;

    public DownloadAutoResumeService(
        IServiceScopeFactory scopeFactory,
        DownloadQueue queue,
        BandwidthSettingsService bandwidth,
        ILogger<DownloadAutoResumeService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _bandwidth = bandwidth;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Apply persisted user settings BEFORE we (re)enqueue anything — the
        // queue cap and bandwidth cap need to be live when downloads start.
        await _queue.LoadAsync(cancellationToken);
        await _bandwidth.LoadAsync(cancellationToken);

        long[] orphanIds;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var orphans = (await repo.GetByStatusAsync(DownloadStatus.Downloading, cancellationToken))
                .Concat(await repo.GetByStatusAsync(DownloadStatus.Probing, cancellationToken))
                .Concat(await repo.GetByStatusAsync(DownloadStatus.Queued, cancellationToken))
                .ToList();

            orphanIds = orphans.Select(o => o.Id).ToArray();

            // Reset to Queued so the UI shows the correct transient state and the
            // queue/engine can move them through Downloading itself.
            foreach (var d in orphans)
            {
                d.Status = DownloadStatus.Queued;
                await repo.UpdateAsync(d, cancellationToken);
            }
        }

        if (orphanIds.Length == 0) return;

        _logger.LogInformation("Auto-resuming {Count} orphaned download(s) from previous session", orphanIds.Length);
        foreach (var id in orphanIds)
        {
            await _queue.EnqueueAsync(id, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
