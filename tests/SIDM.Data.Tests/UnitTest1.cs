using SIDM.Core.Models;
using SIDM.Data.Repositories;

namespace SIDM.Data.Tests;

public class DownloadRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static Download MakeDownload(string url = "https://cdn.test/file.zip", string name = "file.zip") => new()
    {
        Url = url,
        FileName = name,
        TargetPath = $@"C:\Downloads\{name}",
        Status = DownloadStatus.Queued,
        SegmentCount = 8,
    };

    [Fact]
    public async Task Add_then_Get_returns_persisted_download_with_id()
    {
        await using var ctx = _db.CreateContext();
        var repo = new DownloadRepository(ctx);

        var id = await repo.AddAsync(MakeDownload());
        id.Should().BeGreaterThan(0);

        var fetched = await repo.GetAsync(id);
        fetched.Should().NotBeNull();
        fetched!.Url.Should().Be("https://cdn.test/file.zip");
        fetched.SegmentCount.Should().Be(8);
        fetched.Status.Should().Be(DownloadStatus.Queued);
    }

    [Fact]
    public async Task GetByStatus_filters_correctly()
    {
        await using var ctx = _db.CreateContext();
        var repo = new DownloadRepository(ctx);

        await repo.AddAsync(MakeDownload("https://a/", "a"));
        var doneId = await repo.AddAsync(MakeDownload("https://b/", "b"));
        await repo.AddAsync(MakeDownload("https://c/", "c"));

        var done = await ctx.Downloads.FindAsync(doneId);
        done!.Status = DownloadStatus.Completed;
        await ctx.SaveChangesAsync();

        var queued = await repo.GetByStatusAsync(DownloadStatus.Queued);
        var completed = await repo.GetByStatusAsync(DownloadStatus.Completed);

        queued.Should().HaveCount(2);
        completed.Should().ContainSingle().Which.FileName.Should().Be("b");
    }

    [Fact]
    public async Task GetActive_includes_queued_probing_downloading_paused_only()
    {
        await using var ctx = _db.CreateContext();
        var repo = new DownloadRepository(ctx);

        foreach (var (status, name) in new[]
        {
            (DownloadStatus.Queued, "q"),
            (DownloadStatus.Downloading, "d"),
            (DownloadStatus.Paused, "p"),
            (DownloadStatus.Completed, "done"),
            (DownloadStatus.Failed, "fail"),
            (DownloadStatus.Canceled, "x"),
        })
        {
            var dl = MakeDownload($"https://{name}/", name);
            dl.Status = status;
            await repo.AddAsync(dl);
        }

        var active = await repo.GetActiveAsync();
        active.Select(d => d.FileName).Should().BeEquivalentTo(new[] { "q", "d", "p" });
    }

    [Fact]
    public async Task ReplaceSegments_overwrites_existing_segments()
    {
        await using var ctx = _db.CreateContext();
        var repo = new DownloadRepository(ctx);

        var id = await repo.AddAsync(MakeDownload());

        await repo.ReplaceSegmentsAsync(id, new[]
        {
            new Segment { Idx = 0, StartByte = 0,    EndByte = 999,  Status = SegmentStatus.Pending },
            new Segment { Idx = 1, StartByte = 1000, EndByte = 1999, Status = SegmentStatus.Pending },
        });

        (await repo.GetSegmentsAsync(id)).Should().HaveCount(2);

        // Replacing with a different layout drops the old rows.
        await repo.ReplaceSegmentsAsync(id, new[]
        {
            new Segment { Idx = 0, StartByte = 0, EndByte = 1999, Status = SegmentStatus.Pending },
        });

        var after = await repo.GetSegmentsAsync(id);
        after.Should().ContainSingle();
        after[0].EndByte.Should().Be(1999);
    }

    [Fact]
    public async Task Remove_deletes_download_and_cascades_to_segments()
    {
        await using var ctx = _db.CreateContext();
        var repo = new DownloadRepository(ctx);

        var id = await repo.AddAsync(MakeDownload());
        await repo.ReplaceSegmentsAsync(id, new[]
        {
            new Segment { Idx = 0, StartByte = 0, EndByte = 99, Status = SegmentStatus.Pending },
        });

        await repo.RemoveAsync(id);

        (await repo.GetAsync(id)).Should().BeNull();
        (await repo.GetSegmentsAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAll_orders_newest_first()
    {
        await using var ctx = _db.CreateContext();
        var repo = new DownloadRepository(ctx);

        var older = MakeDownload("https://old/", "old");
        older.CreatedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        await repo.AddAsync(older);

        var newer = MakeDownload("https://new/", "new");
        newer.CreatedUtc = DateTimeOffset.UtcNow;
        await repo.AddAsync(newer);

        var all = await repo.GetAllAsync();
        all.Select(d => d.FileName).Should().Equal("new", "old");
    }
}

public class SettingsRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Get_returns_default_for_unknown_key()
    {
        await using var ctx = _db.CreateContext();
        var repo = new SettingsRepository(ctx);

        (await repo.GetAsync<int>("missing")).Should().Be(0);
        (await repo.GetAsync<string>("missing")).Should().BeNull();
    }

    [Fact]
    public async Task Set_then_Get_roundtrips_primitive_value()
    {
        await using var ctx = _db.CreateContext();
        var repo = new SettingsRepository(ctx);

        await repo.SetAsync("downloads.maxConcurrent", 5);
        await repo.SetAsync("ui.theme", "dark");

        (await repo.GetAsync<int>("downloads.maxConcurrent")).Should().Be(5);
        (await repo.GetAsync<string>("ui.theme")).Should().Be("dark");
    }

    [Fact]
    public async Task Set_overwrites_existing_value()
    {
        await using var ctx = _db.CreateContext();
        var repo = new SettingsRepository(ctx);

        await repo.SetAsync("k", 1);
        await repo.SetAsync("k", 99);

        (await repo.GetAsync<int>("k")).Should().Be(99);
    }

    [Fact]
    public async Task Set_then_Get_roundtrips_complex_record()
    {
        await using var ctx = _db.CreateContext();
        var repo = new SettingsRepository(ctx);

        var bandwidth = new BandwidthSettings(1024, true, new[] { "monday", "friday" });
        await repo.SetAsync("bw", bandwidth);

        var fetched = await repo.GetAsync<BandwidthSettings>("bw");
        fetched.Should().BeEquivalentTo(bandwidth);
    }

    [Fact]
    public async Task Remove_deletes_entry()
    {
        await using var ctx = _db.CreateContext();
        var repo = new SettingsRepository(ctx);

        await repo.SetAsync("k", "v");
        await repo.RemoveAsync("k");

        (await repo.GetAsync<string>("k")).Should().BeNull();
    }

    private sealed record BandwidthSettings(int KiBps, bool Enabled, string[] Days);
}
