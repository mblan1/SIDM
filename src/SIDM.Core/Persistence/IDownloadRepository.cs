using SIDM.Core.Models;

namespace SIDM.Core.Persistence;

public interface IDownloadRepository
{
    Task<Download?> GetAsync(long id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Download>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Download>> GetByStatusAsync(DownloadStatus status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Download>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<long> AddAsync(Download download, CancellationToken cancellationToken = default);
    Task UpdateAsync(Download download, CancellationToken cancellationToken = default);
    Task RemoveAsync(long id, CancellationToken cancellationToken = default);

    Task ReplaceSegmentsAsync(long downloadId, IEnumerable<Segment> segments, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Segment>> GetSegmentsAsync(long downloadId, CancellationToken cancellationToken = default);
}
