using System.Diagnostics;
using SIDM.Core.Bandwidth;

namespace SIDM.Core.Tests.Bandwidth;

public class TokenBucketGovernorTests
{
    [Fact]
    public async Task Unlimited_governor_returns_immediately()
    {
        var governor = new TokenBucketGovernor(bytesPerSecond: 0);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            await governor.ConsumeAsync(64 * 1024, CancellationToken.None);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public async Task Negative_or_zero_byte_request_is_a_no_op()
    {
        var governor = new TokenBucketGovernor(bytesPerSecond: 1024);
        await governor.ConsumeAsync(0, CancellationToken.None);
        await governor.ConsumeAsync(-100, CancellationToken.None);
    }

    [Fact]
    public async Task Initial_burst_within_capacity_is_immediate()
    {
        // 100 KiB/s cap → 100 KiB burst capacity. Consuming exactly one full
        // bucket in one call should NOT block — the bucket starts full.
        var governor = new TokenBucketGovernor(bytesPerSecond: 100 * 1024);

        var sw = Stopwatch.StartNew();
        await governor.ConsumeAsync(100 * 1024, CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public async Task Sustained_consumption_above_cap_is_throttled()
    {
        // 50 KiB/s. We try to consume 150 KiB total — 50 fits in the initial
        // burst, the remaining 100 needs ~2 seconds to refill.
        var governor = new TokenBucketGovernor(bytesPerSecond: 50 * 1024);

        // Drain the initial bucket first (no wait), then measure.
        await governor.ConsumeAsync(50 * 1024, CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await governor.ConsumeAsync(50 * 1024, CancellationToken.None);
        await governor.ConsumeAsync(50 * 1024, CancellationToken.None);
        sw.Stop();

        // 100 KiB at 50 KiB/s ≈ 2 s. Allow generous lower bound (1.5 s) and
        // generous upper bound (4 s) to keep this test stable on slow CI.
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(1500));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(4000));
    }

    [Fact]
    public async Task BytesPerSecond_can_be_changed_at_runtime()
    {
        var governor = new TokenBucketGovernor(bytesPerSecond: 1024);
        governor.BytesPerSecond.Should().Be(1024);

        governor.BytesPerSecond = 0; // unlimited

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            await governor.ConsumeAsync(1024 * 1024, CancellationToken.None);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public async Task Cancellation_is_observed_while_blocked()
    {
        var governor = new TokenBucketGovernor(bytesPerSecond: 1); // glacial
        await governor.ConsumeAsync(1, CancellationToken.None); // drain

        using var cts = new CancellationTokenSource();
        var consumeTask = governor.ConsumeAsync(1_000_000, cts.Token).AsTask();

        await Task.Delay(50);
        cts.Cancel();

        await consumeTask.Awaiting(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Negative_BytesPerSecond_is_clamped_to_zero()
    {
        var governor = new TokenBucketGovernor(bytesPerSecond: 0);
        governor.BytesPerSecond = -100;
        governor.BytesPerSecond.Should().Be(0);
    }
}
