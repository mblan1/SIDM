using Microsoft.EntityFrameworkCore;
using SIDM.Core.Models;
using SIDM.Core.Persistence;

namespace SIDM.Data.Repositories;

public sealed class ScheduleRuleRepository : IScheduleRuleRepository
{
    private readonly SidmDbContext _db;

    public ScheduleRuleRepository(SidmDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ScheduleRule>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.ScheduleRules
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

    public Task<ScheduleRule?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        _db.ScheduleRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<long> AddAsync(ScheduleRule rule, CancellationToken cancellationToken = default)
    {
        _db.ScheduleRules.Add(rule);
        await _db.SaveChangesAsync(cancellationToken);
        return rule.Id;
    }

    public async Task UpdateAsync(ScheduleRule rule, CancellationToken cancellationToken = default)
    {
        _db.ScheduleRules.Update(rule);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.ScheduleRules.FindAsync(new object[] { id }, cancellationToken);
        if (entity is null) return;
        _db.ScheduleRules.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
