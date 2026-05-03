using Microsoft.EntityFrameworkCore;
using SIDM.Core.Models;
using SIDM.Core.Persistence;

namespace SIDM.Data.Repositories;

public sealed class DownloadRepository : IDownloadRepository
{
    private readonly SidmDbContext _db;

    public DownloadRepository(SidmDbContext db)
    {
        _db = db;
    }

    public Task<Download?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        _db.Downloads
            .Include(d => d.Segments.OrderBy(s => s.Idx))
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Download>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.Downloads
            .OrderByDescending(d => d.CreatedUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Download>> GetByStatusAsync(DownloadStatus status, CancellationToken cancellationToken = default) =>
        await _db.Downloads
            .Where(d => d.Status == status)
            .OrderByDescending(d => d.CreatedUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Download>> GetActiveAsync(CancellationToken cancellationToken = default) =>
        await _db.Downloads
            .Where(d => d.Status == DownloadStatus.Queued
                     || d.Status == DownloadStatus.Probing
                     || d.Status == DownloadStatus.Downloading
                     || d.Status == DownloadStatus.Paused)
            .OrderByDescending(d => d.CreatedUtc)
            .ToListAsync(cancellationToken);

    public async Task<long> AddAsync(Download download, CancellationToken cancellationToken = default)
    {
        _db.Downloads.Add(download);
        await _db.SaveChangesAsync(cancellationToken);
        return download.Id;
    }

    public async Task UpdateAsync(Download download, CancellationToken cancellationToken = default)
    {
        _db.Downloads.Update(download);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.Downloads.FindAsync(new object[] { id }, cancellationToken);
        if (entity is null) return;
        _db.Downloads.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReplaceSegmentsAsync(long downloadId, IEnumerable<Segment> segments, CancellationToken cancellationToken = default)
    {
        var existing = await _db.Segments.Where(s => s.DownloadId == downloadId).ToListAsync(cancellationToken);
        _db.Segments.RemoveRange(existing);

        foreach (var s in segments)
        {
            s.DownloadId = downloadId;
            _db.Segments.Add(s);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Segment>> GetSegmentsAsync(long downloadId, CancellationToken cancellationToken = default) =>
        await _db.Segments
            .Where(s => s.DownloadId == downloadId)
            .OrderBy(s => s.Idx)
            .ToListAsync(cancellationToken);
}
