using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.Core.Persistence;
using Wpf.Ui.Appearance;

namespace SIDM.App.Services;

/// <summary>
/// User-selectable UI theme (Light / Dark / Follow Windows). Persists the
/// choice via <see cref="ISettingsRepository"/> and pushes it to
/// <see cref="ApplicationThemeManager"/> from WPF-UI on apply.
/// </summary>
public enum AppTheme
{
    /// <summary>Follow the Windows personalization "Choose your mode" setting.</summary>
    System = 0,
    Light = 1,
    Dark = 2,
}

public sealed class ThemeService
{
    public const string SettingKey = "ui.theme";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ThemeService> _logger;

    public ThemeService(IServiceScopeFactory scopeFactory, ILogger<ThemeService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Current selection. Defaults to <see cref="AppTheme.System"/> until <see cref="LoadAndApplyAsync"/> runs.</summary>
    public AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>Reads the persisted choice and applies it to the live application.</summary>
    public async Task LoadAndApplyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            var stored = await settings.GetAsync<string>(SettingKey, cancellationToken);
            Current = ParseTheme(stored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load theme setting; using System");
            Current = AppTheme.System;
        }
        Apply(Current);
    }

    public async Task SetAsync(AppTheme theme, CancellationToken cancellationToken = default)
    {
        Current = theme;
        Apply(theme);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await settings.SetAsync(SettingKey, theme.ToString(), cancellationToken);
    }

    private static void Apply(AppTheme theme)
    {
        var wpfUiTheme = theme switch
        {
            AppTheme.Light => ApplicationTheme.Light,
            AppTheme.Dark => ApplicationTheme.Dark,
            _ => SystemPreferredTheme(),
        };
        ApplicationThemeManager.Apply(wpfUiTheme);
    }

    private static ApplicationTheme SystemPreferredTheme()
    {
        // ApplicationThemeManager exposes the system-preferred theme via its
        // own helper; on Windows 10/11 this reads the "Choose your mode"
        // registry key. Default to Dark when the call somehow fails.
        try
        {
            return ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark;
        }
        catch
        {
            return ApplicationTheme.Dark;
        }
    }

    private static AppTheme ParseTheme(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return AppTheme.System;
        return Enum.TryParse<AppTheme>(stored, ignoreCase: true, out var v) ? v : AppTheme.System;
    }
}
