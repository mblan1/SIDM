using SIDM.Core.Models;

namespace SIDM.Core.Persistence;

/// <summary>
/// Read/write access to user-defined categories. Categories provide default
/// save folders for files matching one of their listed extensions
/// (e.g. category "Video" with extensions "mp4,mkv,webm" → "D:\Videos").
/// </summary>
public interface ICategoryRepository
{
    Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Category?> GetAsync(long id, CancellationToken cancellationToken = default);
    Task<long> AddAsync(Category category, CancellationToken cancellationToken = default);
    Task UpdateAsync(Category category, CancellationToken cancellationToken = default);
    Task RemoveAsync(long id, CancellationToken cancellationToken = default);
}
