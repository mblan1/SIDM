using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIDM.Core.Persistence;
using Velopack;
using Velopack.Sources;

namespace SIDM.App.Services;

/// <summary>
/// Outcome of a check-for-updates round, suitable for binding to the main
/// window's status bar / settings dialog. <see cref="State"/> is the
/// machine-readable summary; <see cref="Message"/> is the user-visible
/// string the UI surfaces verbatim.
/// </summary>
public sealed record UpdateCheckResult(UpdateCheckState State, string Message, string? AvailableVersion = null)
{
    public static UpdateCheckResult NotConfigured { get; } =
        new(UpdateCheckState.NotConfigured, "Update feed not configured.");

    public static UpdateCheckResult UpToDate { get; } =
        new(UpdateCheckState.UpToDate, "SIDM is up to date.");
}

/// <summary>Live download progress for a pending update.</summary>
/// <param name="Percent">0–100.</param>
/// <param name="BytesPerSecond">Smoothed transfer rate; 0 until enough samples.</param>
/// <param name="ReceivedBytes">Bytes downloaded so far (derived from percent × total).</param>
/// <param name="TotalBytes">Total package size, or 0 when unknown.</param>
public sealed record UpdateDownloadProgress(int Percent, double BytesPerSecond, long ReceivedBytes, long TotalBytes);

public enum UpdateCheckState
{
    /// <summary>No feed URL persisted; nothing to do.</summary>
    NotConfigured,
    /// <summary>UpdateManager ran and reported no newer release.</summary>
    UpToDate,
    /// <summary>A newer release is available; <see cref="UpdateCheckResult.AvailableVersion"/> has the SemVer.</summary>
    UpdateAvailable,
    /// <summary>An update was downloaded and is queued to apply on next launch.</summary>
    Pending,
    /// <summary>The feed could not be reached (network / 404 / etc.).</summary>
    FeedError,
    /// <summary>The app is running uninstalled (e.g. dotnet run) — Velopack refuses to update.</summary>
    NotInstalled,
}

/// <summary>
/// Wraps Velopack's <see cref="UpdateManager"/> with the SIDM-specific bits:
///   - feed URL stored in <see cref="ISettingsRepository"/> so the user can
///     point at GitHub Releases, a local folder, or any static URL,
///   - safe fallbacks when the app is running uninstalled (Velopack only
///     applies updates to a packaged install — running via `dotnet run`
///     yields <see cref="UpdateCheckState.NotInstalled"/>),
///   - a single in-flight check at a time (mostly to keep the UI tidy).
///
/// The actual <see cref="UpdateManager.ApplyUpdatesAndRestart(VelopackAsset?, string[]?)"/>
/// call happens on user click, not silently — the user shouldn't lose
/// running downloads to a surprise restart.
/// </summary>
public sealed class UpdaterService
{
    public const string FeedUrlSettingKey = "updates.feedUrl";
    public const string AutoCheckSettingKey = "updates.autoCheckOnStartup";

    /// <summary>
    /// Hard-coded fallback so a brand-new install auto-checks the public
    /// GitHub releases without the user having to paste a URL into Settings.
    /// Overridden the moment the user enters their own value.
    /// </summary>
    public const string DefaultFeedUrl = "https://github.com/mblan1/SIDM";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpdaterService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _pendingUpdate;

    public UpdaterService(IServiceScopeFactory scopeFactory, ILogger<UpdaterService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string? FeedUrl { get; private set; }

    /// <summary>
    /// True iff the startup hosted service should run a check. Defaults to
    /// <c>true</c> on first run (no persisted value yet) so brand-new installs
    /// get auto-updates out of the box; the user can opt out from Settings.
    /// </summary>
    public bool AutoCheckOnStartup { get; private set; } = true;

    /// <summary>
    /// Most recent result, or null if no check has run yet. Read by the tray
    /// when it initializes so a startup-time update notification still fires
    /// if the check completed before the tray was wired up.
    /// </summary>
    public UpdateCheckResult? LastResult { get; private set; }

    /// <summary>
    /// Fires when a check finishes with <see cref="UpdateCheckState.UpdateAvailable"/>.
    /// The tray subscribes to show a balloon notification.
    /// </summary>
    public event Action<UpdateCheckResult>? UpdateAvailable;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            var persistedFeed = await settings.GetAsync<string>(FeedUrlSettingKey, cancellationToken);
            // Empty-string sentinel means "user explicitly cleared it" — respect that.
            FeedUrl = persistedFeed is null ? DefaultFeedUrl
                : string.IsNullOrWhiteSpace(persistedFeed) ? null
                : persistedFeed;

            // Detect first-run separately from persisted=false so we can default
            // to enabled. ISettingsRepository.GetAsync<bool> returns default(bool)
            // = false for a missing key, indistinguishable from an explicit
            // user-set false; GetAllRawAsync gives us key presence.
            var raw = await settings.GetAllRawAsync(cancellationToken);
            if (raw.TryGetValue(AutoCheckSettingKey, out var persisted))
            {
                AutoCheckOnStartup = bool.TryParse(persisted, out var b) && b;
            }
            else
            {
                AutoCheckOnStartup = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load updater settings");
        }
    }

    public async Task SetFeedUrlAsync(string? url, CancellationToken cancellationToken = default)
    {
        FeedUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        if (FeedUrl is null) await settings.RemoveAsync(FeedUrlSettingKey, cancellationToken);
        else await settings.SetAsync(FeedUrlSettingKey, FeedUrl, cancellationToken);
    }

    public async Task SetAutoCheckAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        AutoCheckOnStartup = enabled;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await settings.SetAsync(AutoCheckSettingKey, enabled, cancellationToken);
    }

    /// <summary>
    /// Asks the feed if a newer release is available. When yes, also downloads
    /// it in the background (the file lands in Velopack's pending-updates
    /// folder; the actual apply-and-restart is a separate, user-initiated
    /// call to <see cref="ApplyPendingAndRestartAsync"/>).
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var result = await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
        LastResult = result;
        if (result.State == UpdateCheckState.UpdateAvailable)
        {
            try { UpdateAvailable?.Invoke(result); }
            catch (Exception ex) { _logger.LogDebug(ex, "UpdateAvailable handler threw"); }
        }
        return result;
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(FeedUrl))
        {
            return UpdateCheckResult.NotConfigured;
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // Already checking — collapse to a single in-flight call.
            return new UpdateCheckResult(UpdateCheckState.UpToDate, "Update check already in progress…");
        }

        try
        {
            var source = BuildSource(FeedUrl!);
            var manager = new UpdateManager(source);

            if (!manager.IsInstalled)
            {
                // dotnet run / unpacked layout — Velopack refuses to operate.
                return new UpdateCheckResult(UpdateCheckState.NotInstalled,
                    "Updates are only available in installed builds.");
            }

            UpdateInfo? info;
            try
            {
                info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Update feed error");
                return new UpdateCheckResult(UpdateCheckState.FeedError, $"Could not reach update feed: {ex.Message}");
            }

            if (info is null)
            {
                _pendingUpdate = null;
                return UpdateCheckResult.UpToDate;
            }

            // Don't pre-download here — the user downloads on click so they
            // see live progress + speed. Just record what's available.
            _pendingUpdate = info;
            var version = info.TargetFullRelease.Version.ToString();
            return new UpdateCheckResult(UpdateCheckState.UpdateAvailable,
                $"SIDM {version} is available.",
                AvailableVersion: version);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// (Re-)downloads the pending update, reporting 0–100% progress. Velopack
    /// caches partial/complete downloads, so when the background check already
    /// pre-downloaded the package this returns near-instantly at 100%; when it
    /// hasn't (or only partially), the caller gets real progress to show in a
    /// bar. Returns false when there's nothing to download or the app isn't an
    /// installed build.
    /// </summary>
    public async Task<bool> DownloadPendingAsync(IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (_pendingUpdate is null || string.IsNullOrWhiteSpace(FeedUrl)) return false;
        try
        {
            var manager = new UpdateManager(BuildSource(FeedUrl!));
            if (!manager.IsInstalled) return false;

            // Velopack's callback gives percent only. Derive bytes from the
            // known package size and compute a smoothed speed over ~0.3s
            // windows. Velopack resumes a partial download from the packages
            // folder, so on a re-run percent simply starts wherever the last
            // attempt left off.
            long total = 0;
            try { total = _pendingUpdate.TargetFullRelease?.Size ?? 0; } catch { total = 0; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastElapsed = TimeSpan.Zero;
            long lastBytes = 0;
            double lastBps = 0;

            void Report(int pct)
            {
                long received = total > 0 ? (long)(pct / 100.0 * total) : 0;
                var now = sw.Elapsed;
                var dt = (now - lastElapsed).TotalSeconds;
                if (dt >= 0.3 && received >= lastBytes)
                {
                    lastBps = (received - lastBytes) / dt;
                    lastBytes = received;
                    lastElapsed = now;
                }
                progress?.Report(new UpdateDownloadProgress(pct, lastBps, received, total));
            }

            await manager.DownloadUpdatesAsync(
                _pendingUpdate,
                progress is null ? null : Report,
                cancelToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            // User closed the update window mid-download. Velopack keeps the
            // partial package, so the next attempt resumes from it.
            _logger.LogInformation("Update download canceled — partial kept for resume");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download pending update");
            return false;
        }
    }

    /// <summary>
    /// Applies the pending update and restarts. Returns false if no update is
    /// ready (caller should call <see cref="CheckAsync"/> first).
    /// </summary>
    public bool ApplyPendingAndRestart()
    {
        if (_pendingUpdate is null || string.IsNullOrWhiteSpace(FeedUrl)) return false;
        try
        {
            var manager = new UpdateManager(BuildSource(FeedUrl!));
            if (!manager.IsInstalled) return false;
            manager.ApplyUpdatesAndRestart(_pendingUpdate);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply pending update");
            return false;
        }
    }

    /// <summary>
    /// Build a Velopack source from the user's feed URL. Currently supports:
    ///   - GitHub releases (URL of the form https://github.com/owner/repo[/releases])
    ///   - any other URL or local folder → treated as a static feed (the
    ///     folder with RELEASES + .nupkgs that scripts/publish.ps1 emits).
    /// </summary>
    private static IUpdateSource BuildSource(string feed)
    {
        if (feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return new GithubSource(feed, accessToken: null, prerelease: false);
        }
        return new SimpleWebSource(feed);
    }
}

/// <summary>
/// Hosted service that runs <see cref="UpdaterService.CheckAsync"/> at app
/// startup when the user has opted into auto-checking. Failures are logged
/// and silent — the user should never see a startup error from "we tried
/// to talk to GitHub and it was down."
/// </summary>
public sealed class UpdaterStartupCheck : IHostedService
{
    /// <summary>How often to re-check for updates while SIDM keeps running, so
    /// a long-lived session notices a new release without a restart.</summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(1);

    private readonly UpdaterService _updater;
    private readonly ILogger<UpdaterStartupCheck> _logger;
    private readonly CancellationTokenSource _cts = new();

    public UpdaterStartupCheck(UpdaterService updater, ILogger<UpdaterStartupCheck> logger)
    {
        _updater = updater;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _updater.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!_updater.AutoCheckOnStartup) return;

        // Run on a background task so we don't delay the main window. Checks
        // once immediately, then on a timer for the lifetime of the app. Each
        // successful "update available" raises UpdaterService.UpdateAvailable,
        // which drives the tray balloon and (when the main window is open) the
        // forced-update gate — so a running instance picks up new releases
        // without a restart.
        _ = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await RunOnceAsync().ConfigureAwait(false);

        try
        {
            using var timer = new PeriodicTimer(RecheckInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await RunOnceAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // App shutting down — stop quietly.
        }
    }

    private async Task RunOnceAsync()
    {
        try
        {
            var result = await _updater.CheckAsync().ConfigureAwait(false);
            if (result.State == UpdateCheckState.UpdateAvailable)
            {
                _logger.LogInformation("Update available: {Version}", result.AvailableVersion);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed (silent)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }
}
