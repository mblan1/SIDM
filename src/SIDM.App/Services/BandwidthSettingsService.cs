using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.Core.Abstractions;
using SIDM.Core.Persistence;

namespace SIDM.App.Services;

/// <summary>
/// Bridges the persisted "bandwidth.globalBytesPerSec" setting and the live
/// <see cref="IBandwidthGovernor"/>. Loaded once at startup; the settings
/// dialog calls <see cref="SetAsync"/> when the user picks a new cap.
/// </summary>
public sealed class BandwidthSettingsService
{
    public const string GlobalBytesPerSecondKey = "bandwidth.globalBytesPerSec";

    private readonly IBandwidthGovernor _governor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BandwidthSettingsService> _logger;

    public BandwidthSettingsService(
        IBandwidthGovernor governor,
        IServiceScopeFactory scopeFactory,
        ILogger<BandwidthSettingsService> logger)
    {
        _governor = governor;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public long CurrentBytesPerSecond => _governor.BytesPerSecond;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            var stored = await settings.GetAsync<long?>(GlobalBytesPerSecondKey, cancellationToken);
            _governor.BytesPerSecond = stored is > 0 ? stored.Value : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load bandwidth setting; keeping default (unlimited)");
        }
    }

    public async Task SetAsync(long bytesPerSecond, CancellationToken cancellationToken = default)
    {
        var v = Math.Max(0, bytesPerSecond);
        _governor.BytesPerSecond = v;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await settings.SetAsync<long>(GlobalBytesPerSecondKey, v, cancellationToken);
    }
}
