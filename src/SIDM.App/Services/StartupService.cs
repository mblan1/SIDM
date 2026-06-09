using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SIDM.Core.Persistence;

namespace SIDM.App.Services;

/// <summary>
/// "Start SIDM when Windows starts." Backed by a boolean setting (default ON)
/// and a per-user <c>HKCU\…\CurrentVersion\Run</c> entry. The entry launches the
/// app with <c>--background</c> so it comes up hidden in the tray — ready to
/// capture browser downloads — instead of popping the main window on every
/// login.
///
/// On startup the service reconciles the registry to match the setting and the
/// CURRENT exe path, so the entry self-heals across Velopack updates (which swap
/// the app folder) and a changed install location. All registry work is
/// best-effort: a failure here must never crash the app.
/// </summary>
public sealed class StartupService
{
    public const string SettingKey = "startup.enabled";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SIDM";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StartupService> _logger;

    public StartupService(IServiceScopeFactory scopeFactory, ILogger<StartupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Desired state, loaded from settings. Default is ON.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>
    /// Reads the setting (defaulting to ON the first time it's ever read, and
    /// persisting that default) and makes the registry match the current exe
    /// path. Call once at startup. Best-effort — never throws.
    /// </summary>
    public async Task LoadAndReconcileAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            // bool? distinguishes "never set" (null → default ON) from an
            // explicit user choice of false.
            var stored = await settings.GetAsync<bool?>(SettingKey, cancellationToken);
            if (stored is null)
            {
                IsEnabled = true;
                await settings.SetAsync(SettingKey, true, cancellationToken);
            }
            else
            {
                IsEnabled = stored.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load startup preference; defaulting to enabled");
            IsEnabled = true;
        }

        ApplyToRegistry(IsEnabled);
    }

    /// <summary>Persists the new state and updates the registry immediately.</summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        IsEnabled = enabled;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            await settings.SetAsync(SettingKey, enabled, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist startup preference");
        }
        ApplyToRegistry(enabled);
    }

    private void ApplyToRegistry(bool enabled)
    {
        try
        {
            if (enabled) WriteRunKey();
            else RemoveRunKey();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update the Windows startup registry entry");
        }
    }

    /// <summary>
    /// Writes the HKCU Run entry pointing at the current exe with --background.
    /// </summary>
    private static void WriteRunKey()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.SetValue(RunValueName, $"\"{exe}\" --background", RegistryValueKind.String);
    }

    private static void RemoveRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Best-effort removal of the Run entry — called from the Velopack
    /// before-uninstall hook so SIDM doesn't leave an orphaned entry pointing at
    /// a deleted exe. Static so it works with no DI container available.
    /// </summary>
    public static void RemoveFromStartupForUninstall()
    {
        try { RemoveRunKey(); } catch { /* best-effort */ }
    }
}
