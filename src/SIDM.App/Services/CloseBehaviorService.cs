using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.Core.Persistence;

namespace SIDM.App.Services;

public enum CloseBehavior
{
    /// <summary>Clicking the window X minimizes SIDM to the tray; app keeps running.</summary>
    HideToTray = 0,
    /// <summary>Clicking the window X shuts the app down.</summary>
    Exit = 1,
}

/// <summary>
/// Persists the user's choice for what the window-close button does (hide to
/// tray vs. exit) and exposes an in-process "exit was requested" flag so
/// MainWindow's Closing handler knows whether to honor the close or hijack
/// it. The tray's "Exit" menu item sets this flag before invoking shutdown.
/// </summary>
public sealed class CloseBehaviorService
{
    public const string SettingKey = "ui.closeBehavior";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CloseBehaviorService> _logger;

    public CloseBehaviorService(IServiceScopeFactory scopeFactory, ILogger<CloseBehaviorService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Current behavior. Default is HideToTray — friendlier for a download manager.</summary>
    public CloseBehavior Current { get; private set; } = CloseBehavior.HideToTray;

    /// <summary>
    /// True between the moment the tray's Exit item is clicked (or any other
    /// caller asks for a real shutdown) and the actual process exit. Read by
    /// MainWindow.OnClosing to know whether to cancel-and-hide or let the
    /// close go through.
    /// </summary>
    public bool ExitRequested { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            var raw = await settings.GetAsync<string>(SettingKey, cancellationToken);
            Current = ParseOrDefault(raw);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load close-behavior preference; defaulting to HideToTray");
            Current = CloseBehavior.HideToTray;
        }
    }

    public async Task SaveAsync(CloseBehavior behavior, CancellationToken cancellationToken = default)
    {
        Current = behavior;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            await settings.SetAsync(SettingKey, behavior.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist close-behavior preference");
        }
    }

    /// <summary>
    /// Called by the tray's "Exit" menu item — sets the flag so MainWindow's
    /// Closing handler honors the shutdown instead of hijacking it.
    /// </summary>
    public void RequestExit() => ExitRequested = true;

    private static CloseBehavior ParseOrDefault(string? raw) =>
        Enum.TryParse<CloseBehavior>(raw, ignoreCase: true, out var v) ? v : CloseBehavior.HideToTray;
}
