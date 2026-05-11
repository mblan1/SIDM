using Microsoft.Extensions.DependencyInjection;
using SIDM.Core.Persistence;

namespace SIDM.App.Services;

/// <summary>
/// Tracks whether the user has seen the welcome window. First launch shows
/// the window; subsequent launches don't. Triggered from <see cref="MainWindow"/>
/// after <c>Downloads.LoadAsync()</c> completes so the welcome appears on
/// top of an already-populated UI rather than a blank window.
/// </summary>
public sealed class OnboardingService
{
    public const string CompletedSettingKey = "onboarding.completed";

    private readonly IServiceScopeFactory _scopeFactory;

    public OnboardingService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<bool> ShouldShowWelcomeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            return !await settings.GetAsync<bool>(CompletedSettingKey, cancellationToken);
        }
        catch
        {
            // If we can't read settings at all (corrupted DB? first boot?),
            // showing the welcome is the safer default.
            return true;
        }
    }

    public async Task MarkCompletedAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await settings.SetAsync(CompletedSettingKey, true, cancellationToken);
    }
}
