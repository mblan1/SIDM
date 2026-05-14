using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIDM.Core.Models;
using SIDM.Core.Persistence;

namespace SIDM.App.Services;

/// <summary>
/// Seeds the five IDM-style default categories (Compressed, Documents, Music,
/// Programs, Video) on first run so the categories sidebar isn't empty when
/// the user opens SIDM for the first time. Idempotent — does nothing if any
/// category already exists.
/// </summary>
public sealed class CategorySeeder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CategorySeeder> _logger;

    public CategorySeeder(IServiceScopeFactory scopeFactory, ILogger<CategorySeeder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task SeedIfEmptyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICategoryRepository>();
            var existing = await repo.GetAllAsync(cancellationToken);
            if (existing.Count > 0) return;

            foreach (var c in Defaults)
            {
                await repo.AddAsync(c, cancellationToken);
            }

            _logger.LogInformation("Seeded {Count} default categories", Defaults.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seed default categories — sidebar will start empty");
        }
    }

    /// <summary>
    /// Default IDM-style categories. Extensions are comma-separated and match
    /// <see cref="SIDM.Core.Scheduling.CategoryMatcher"/>'s splitter logic.
    /// Colors are picked to be distinguishable in the sidebar against both
    /// light and dark themes.
    /// </summary>
    private static readonly Category[] Defaults =
    [
        new() { Name = "Compressed", Extensions = "zip,rar,7z,tar,gz,bz2,xz,zst,iso", Color = "#F59E0B" },
        new() { Name = "Documents", Extensions = "pdf,doc,docx,xls,xlsx,ppt,pptx,txt,rtf,odt,csv,epub", Color = "#3B82F6" },
        new() { Name = "Music", Extensions = "mp3,wav,flac,m4a,aac,ogg,opus,wma", Color = "#A855F7" },
        new() { Name = "Programs", Extensions = "exe,msi,dmg,deb,rpm,appx,msix,pkg,apk", Color = "#10B981" },
        new() { Name = "Video", Extensions = "mp4,mkv,webm,avi,mov,wmv,flv,ts,m3u8,mpd,m4v", Color = "#EF4444" },
    ];
}
