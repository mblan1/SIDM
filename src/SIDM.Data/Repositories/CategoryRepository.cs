using Microsoft.EntityFrameworkCore;
using SIDM.Core.Models;
using SIDM.Core.Persistence;

namespace SIDM.Data.Repositories;

public sealed class CategoryRepository : ICategoryRepository
{
    private readonly SidmDbContext _db;

    public CategoryRepository(SidmDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

    public Task<Category?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        _db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<long> AddAsync(Category category, CancellationToken cancellationToken = default)
    {
        _db.Categories.Add(category);
        await _db.SaveChangesAsync(cancellationToken);
        return category.Id;
    }

    public async Task UpdateAsync(Category category, CancellationToken cancellationToken = default)
    {
        _db.Categories.Update(category);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.Categories.FindAsync(new object[] { id }, cancellationToken);
        if (entity is null) return;
        _db.Categories.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
