using SIDM.Core.Abstractions;

namespace SIDM.Core.Bandwidth;

/// <summary>
/// Classic token-bucket throttle. The bucket holds at most ~1 second worth of
/// tokens (1 token = 1 byte). Tokens refill continuously based on the wall
/// clock so the rate is independent of how often <see cref="ConsumeAsync"/> is
/// called. When the bucket is empty, callers wait for tokens to be produced.
///
/// Burst behavior: a freshly-constructed governor starts FULL, so a small file
/// can be downloaded immediately at any speed; the cap only bites once the
/// bucket has been drained by the first second of downloading.
///
/// Setting <see cref="BytesPerSecond"/> to <c>0</c> disables throttling. The
/// fast path is then a single atomic read with no allocations — safe to call
/// on the per-buffer hot path inside <see cref="Engine.SegmentWorker"/>.
/// </summary>
public sealed class TokenBucketGovernor : IBandwidthGovernor
{
    private readonly object _lock = new();
    private long _bytesPerSecond;
    private double _tokens;       // current bucket level (bytes)
    private long _lastRefillTicks;
    private long _capacity;       // burst size (≈ 1 second at the configured rate)

    public TokenBucketGovernor(long bytesPerSecond = 0)
    {
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _capacity = _bytesPerSecond;
        _tokens = _capacity;
        _lastRefillTicks = DateTime.UtcNow.Ticks;
    }

    public long BytesPerSecond
    {
        get
        {
            lock (_lock) { return _bytesPerSecond; }
        }
        set
        {
            var v = Math.Max(0, value);
            lock (_lock)
            {
                RefillLocked(DateTime.UtcNow.Ticks);
                _bytesPerSecond = v;
                _capacity = v;
                if (_tokens > _capacity) _tokens = _capacity;
            }
        }
    }

    public async ValueTask ConsumeAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0) return;

        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                if (_bytesPerSecond <= 0)
                {
                    return; // unlimited
                }

                RefillLocked(DateTime.UtcNow.Ticks);

                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                // Not enough tokens. Wait just long enough to produce what we need.
                var deficit = bytes - _tokens;
                wait = TimeSpan.FromSeconds(deficit / _bytesPerSecond);
            }

            // Bound the worst-case sleep so a rate-change wakes us within a tick.
            if (wait > TimeSpan.FromMilliseconds(200)) wait = TimeSpan.FromMilliseconds(200);
            if (wait < TimeSpan.FromMilliseconds(1)) wait = TimeSpan.FromMilliseconds(1);

            await Task.Delay(wait, cancellationToken);
        }
    }

    private void RefillLocked(long nowTicks)
    {
        if (_bytesPerSecond <= 0)
        {
            _lastRefillTicks = nowTicks;
            return;
        }

        var elapsedSeconds = Math.Max(0, (nowTicks - _lastRefillTicks)) / (double)TimeSpan.TicksPerSecond;
        if (elapsedSeconds <= 0) return;

        _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _bytesPerSecond);
        _lastRefillTicks = nowTicks;
    }
}

/// <summary>No-op governor — used when bandwidth limiting is disabled or in tests.</summary>
public sealed class NoopBandwidthGovernor : IBandwidthGovernor
{
    public long BytesPerSecond { get; set; }
    public ValueTask ConsumeAsync(int bytes, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
