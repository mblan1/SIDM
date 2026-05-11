namespace SIDM.Core.Abstractions;

/// <summary>
/// Throttles aggregate download throughput. A single governor instance is
/// shared across every active <see cref="Engine.SegmentWorker"/>, so the cap is
/// global (sum across all in-flight segments and all in-flight downloads).
///
/// Implementations must be safe to call concurrently. <see cref="ConsumeAsync"/>
/// is invoked once per HTTP read buffer (~64 KiB) on the hot path, so the
/// fast-path (no throttling configured) must be allocation-free.
/// </summary>
public interface IBandwidthGovernor
{
    /// <summary>
    /// Global cap in bytes per second. <c>0</c> means unlimited. Setting this
    /// value takes effect on the next call to <see cref="ConsumeAsync"/>;
    /// already-blocked callers are NOT woken — their wait expires naturally.
    /// </summary>
    long BytesPerSecond { get; set; }

    /// <summary>
    /// Acquires permission to consume <paramref name="bytes"/> from the shared
    /// pool. Returns synchronously when unlimited or when tokens are available;
    /// otherwise awaits until enough tokens have been produced.
    /// </summary>
    ValueTask ConsumeAsync(int bytes, CancellationToken cancellationToken);
}
