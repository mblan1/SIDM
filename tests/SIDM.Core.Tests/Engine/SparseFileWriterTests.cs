using Microsoft.Extensions.Logging.Abstractions;
using SIDM.Core.Engine;

namespace SIDM.Core.Tests.Engine;

public class SparseFileWriterTests : IDisposable
{
    private readonly string _scratchDir;

    public SparseFileWriterTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "sidm-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchDir, recursive: true); }
        catch { /* test cleanup best-effort */ }
    }

    private string PathFor(string name) => Path.Combine(_scratchDir, name);

    [Fact]
    public async Task Allocate_creates_temp_file_of_exact_total_bytes()
    {
        var target = PathFor("hello.bin");

        await using var writer = SparseFileWriter.Allocate(target, totalBytes: 4096, NullLogger<SparseFileWriter>.Instance);

        File.Exists(writer.TempFilePath).Should().BeTrue();
        new FileInfo(writer.TempFilePath).Length.Should().Be(4096);
        writer.TempFilePath.Should().EndWith(SparseFileWriter.TempSuffix);
        writer.TargetFilePath.Should().Be(target);
    }

    [Fact]
    public async Task WriteAtAsync_then_Finalize_produces_target_file_with_correct_bytes()
    {
        var target = PathFor("payload.bin");
        var data = new byte[8192];
        new Random(42).NextBytes(data);

        await using (var writer = SparseFileWriter.Allocate(target, totalBytes: data.Length, NullLogger<SparseFileWriter>.Instance))
        {
            // Write in 4 chunks at non-sequential offsets to prove offset writes work.
            await writer.WriteAtAsync(0, data.AsMemory(0, 2048), CancellationToken.None);
            await writer.WriteAtAsync(6144, data.AsMemory(6144, 2048), CancellationToken.None);
            await writer.WriteAtAsync(2048, data.AsMemory(2048, 2048), CancellationToken.None);
            await writer.WriteAtAsync(4096, data.AsMemory(4096, 2048), CancellationToken.None);

            var finalPath = await writer.FinalizeAsync(CancellationToken.None);
            finalPath.Should().Be(target);
        }

        File.Exists(target).Should().BeTrue();
        var actual = await File.ReadAllBytesAsync(target);
        actual.Should().Equal(data);
    }

    [Fact]
    public async Task Concurrent_writes_at_disjoint_offsets_are_safe()
    {
        var target = PathFor("concurrent.bin");
        const int total = 16 * 1024;
        const int segmentSize = 1024;
        const int segments = total / segmentSize;

        var data = new byte[total];
        new Random(7).NextBytes(data);

        await using var writer = SparseFileWriter.Allocate(target, totalBytes: total, NullLogger<SparseFileWriter>.Instance);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, segments),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (i, ct) =>
            {
                var offset = i * segmentSize;
                await writer.WriteAtAsync(offset, data.AsMemory(offset, segmentSize), ct);
            });

        await writer.FinalizeAsync(CancellationToken.None);

        var actual = await File.ReadAllBytesAsync(target);
        actual.Should().Equal(data);
    }

    [Fact]
    public async Task Finalize_renames_with_collision_suffix_when_target_already_exists()
    {
        var target = PathFor("dupe.bin");
        await File.WriteAllBytesAsync(target, new byte[] { 1, 2, 3 });

        var data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        await using var writer = SparseFileWriter.Allocate(target, totalBytes: data.Length, NullLogger<SparseFileWriter>.Instance);
        await writer.WriteAtAsync(0, data, CancellationToken.None);
        var finalPath = await writer.FinalizeAsync(CancellationToken.None);

        finalPath.Should().NotBe(target);
        Path.GetFileName(finalPath).Should().Be("dupe (1).bin");
        var actual = await File.ReadAllBytesAsync(finalPath);
        actual.Should().Equal(data);
    }

    [Fact]
    public async Task WriteAtAsync_throws_when_offset_outside_total()
    {
        await using var writer = SparseFileWriter.Allocate(PathFor("oob.bin"), totalBytes: 100, NullLogger<SparseFileWriter>.Instance);

        var act = () => writer.WriteAtAsync(150, new byte[] { 0 }, CancellationToken.None).AsTask();
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task WriteAtAsync_throws_when_buffer_extends_past_total()
    {
        await using var writer = SparseFileWriter.Allocate(PathFor("over.bin"), totalBytes: 100, NullLogger<SparseFileWriter>.Instance);

        var act = () => writer.WriteAtAsync(80, new byte[50], CancellationToken.None).AsTask();
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Allocate_throws_for_invalid_total()
    {
        var act = () => SparseFileWriter.Allocate(PathFor("bad.bin"), totalBytes: 0, NullLogger<SparseFileWriter>.Instance);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ResolveCollision_returns_input_when_no_existing_file()
    {
        var path = PathFor("nope.bin");
        SparseFileWriter.ResolveCollision(path).Should().Be(path);
    }

    [Fact]
    public async Task ResolveCollision_increments_counter_when_multiple_collisions()
    {
        var path = PathFor("many.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 1 });
        await File.WriteAllBytesAsync(PathFor("many (1).bin"), new byte[] { 2 });
        await File.WriteAllBytesAsync(PathFor("many (2).bin"), new byte[] { 3 });

        var resolved = SparseFileWriter.ResolveCollision(path);
        Path.GetFileName(resolved).Should().Be("many (3).bin");
    }
}
