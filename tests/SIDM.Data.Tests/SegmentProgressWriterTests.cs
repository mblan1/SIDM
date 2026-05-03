using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SIDM.Core.Models;
using SIDM.Data.Repositories;

namespace SIDM.Data.Tests;

public class SegmentProgressWriterTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<long> SeedDownloadWithSegmentsAsync(int segmentCount, long bytesPerSegment = 1024)
    {
        await using var ctx = _db.CreateContext();
        var dl = new Download
        {
            Url = "https://cdn.test/x",
            FileName = "x.bin",
            TargetPath = @"C:\x.bin",
            SegmentCount = segmentCount,
            Status = DownloadStatus.Downloading,
        };
        ctx.Downloads.Add(dl);
        await ctx.SaveChangesAsync();

        for (var i = 0; i < segmentCount; i++)
        {
            ctx.Segments.Add(new Segment
            {
                DownloadId = dl.Id,
                Idx = i,
                StartByte = i * bytesPerSegment,
                EndByte = (i + 1) * bytesPerSegment - 1,
                BytesDownloaded = 0,
                Status = SegmentStatus.Active,
            });
        }
        await ctx.SaveChangesAsync();
        return dl.Id;
    }

    private async Task<long> ReadBytesDownloadedAsync(long downloadId, int idx)
    {
        await using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT BytesDownloaded FROM Segments WHERE DownloadId = $did AND Idx = $idx";
        cmd.Parameters.AddWithValue("$did", downloadId);
        cmd.Parameters.AddWithValue("$idx", idx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Flush_persists_buffered_progress_events()
    {
        var downloadId = await SeedDownloadWithSegmentsAsync(segmentCount: 4);

        var writer = new SegmentProgressWriter(_db.ConnectionString, NullLogger<SegmentProgressWriter>.Instance);

        writer.Report(downloadId, segmentIndex: 0, bytesDownloaded: 100);
        writer.Report(downloadId, segmentIndex: 1, bytesDownloaded: 200);
        writer.Report(downloadId, segmentIndex: 2, bytesDownloaded: 300);
        writer.Report(downloadId, segmentIndex: 3, bytesDownloaded: 400);

        await writer.FlushAsync();

        (await ReadBytesDownloadedAsync(downloadId, 0)).Should().Be(100);
        (await ReadBytesDownloadedAsync(downloadId, 1)).Should().Be(200);
        (await ReadBytesDownloadedAsync(downloadId, 2)).Should().Be(300);
        (await ReadBytesDownloadedAsync(downloadId, 3)).Should().Be(400);
    }

    [Fact]
    public async Task Repeated_reports_for_same_segment_coalesce_to_latest_value()
    {
        var downloadId = await SeedDownloadWithSegmentsAsync(segmentCount: 1);

        var writer = new SegmentProgressWriter(_db.ConnectionString, NullLogger<SegmentProgressWriter>.Instance);

        // Hammer the same segment with monotonically increasing values.
        for (var i = 1; i <= 50; i++)
        {
            writer.Report(downloadId, 0, bytesDownloaded: i * 10);
        }

        await writer.FlushAsync();

        (await ReadBytesDownloadedAsync(downloadId, 0)).Should().Be(500,
            "only the most recent value per (downloadId, idx) should be persisted");
    }

    [Fact]
    public async Task Reports_for_unknown_segment_are_silently_ignored()
    {
        // No download seeded — UPDATE matches zero rows. Writer should not throw.
        var writer = new SegmentProgressWriter(_db.ConnectionString, NullLogger<SegmentProgressWriter>.Instance);

        writer.Report(downloadId: 9999, segmentIndex: 0, bytesDownloaded: 100);

        // Need a real DB file — initializing schema separately.
        await using (var ctx = _db.CreateContext()) { /* migrate */ }

        var act = () => writer.FlushAsync();
        await act.Should().NotThrowAsync();
    }
}
