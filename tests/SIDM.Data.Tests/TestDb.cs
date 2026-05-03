using Microsoft.EntityFrameworkCore;
using SIDM.Data;

namespace SIDM.Data.Tests;

/// <summary>
/// Temp file-backed SQLite database, scoped to a single test. Disposing removes
/// the file. Using a real on-disk file (not <c>:memory:</c>) lets multiple
/// connections share state, which the SegmentProgressWriter tests need.
/// </summary>
internal sealed class TestDb : IDisposable
{
    public string Path { get; }
    public string ConnectionString => $"Data Source={Path};Cache=Shared;Pooling=True";

    public TestDb()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sidm-test-{Guid.NewGuid():N}.db");
    }

    public SidmDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SidmDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        var ctx = new SidmDbContext(options);
        ctx.Database.Migrate();
        return ctx;
    }

    public void Dispose()
    {
        try
        {
            // SQLite holds file locks for a moment after dispose; suppress sporadic IO errors.
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    if (File.Exists(Path)) File.Delete(Path);
                    if (File.Exists(Path + "-shm")) File.Delete(Path + "-shm");
                    if (File.Exists(Path + "-wal")) File.Delete(Path + "-wal");
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(20);
                }
            }
        }
        catch { }
    }
}
