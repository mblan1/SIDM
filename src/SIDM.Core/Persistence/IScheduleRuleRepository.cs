using SIDM.Core.Models;

namespace SIDM.Core.Persistence;

/// <summary>
/// Read/write access to user-configured time-window rules. Used by
/// <see cref="Scheduling.ScheduleEvaluator"/> to decide whether downloads are
/// allowed right now, and by the settings UI to manage the rule list.
/// </summary>
public interface IScheduleRuleRepository
{
    Task<IReadOnlyList<ScheduleRule>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ScheduleRule?> GetAsync(long id, CancellationToken cancellationToken = default);
    Task<long> AddAsync(ScheduleRule rule, CancellationToken cancellationToken = default);
    Task UpdateAsync(ScheduleRule rule, CancellationToken cancellationToken = default);
    Task RemoveAsync(long id, CancellationToken cancellationToken = default);
}
