using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIDM.Core.Persistence;

namespace SIDM.Data.Repositories;

/// <summary>
/// Hot-path writer for per-segment byte counts. Bypasses EF and uses raw
/// <see cref="Microsoft.Data.Sqlite"/> to coalesce many in-process progress events
/// into batched UPDATE statements. The orchestrator pumps thousands of events per
/// second per active download — sending each through EF would dominate CPU and
/// lock-contend with foreground reads.
///
/// Writes are buffered in a bounded channel and flushed by a background task
/// every <see cref="FlushInterval"/> or when <see cref="MaxBatchSize"/> events
/// accumulate, whichever comes first.
/// </summary>
public sealed class SegmentProgressWriter : BackgroundService, IDownloadProgressSink
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    public const int MaxBatchSize = 256;
    private const int ChannelCapacity = 4096;

    private readonly string _connectionString;
    private readonly ILogger<SegmentProgressWriter> _logger;
    private readonly Channel<ProgressEvent> _channel;

    /// <summary>Latest known byte count per (downloadId, segmentIdx). Coalesces writes
    /// when the channel produces multiple events for the same segment between flushes.</summary>
    private readonly Dictionary<(long DownloadId, int Idx), long> _pending = new();

    public SegmentProgressWriter(string connectionString, ILogger<SegmentProgressWriter> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
        _channel = Channel.CreateBounded<ProgressEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Report(long downloadId, int segmentIndex, long bytesDownloaded)
    {
        // Channel never blocks (DropOldest). On overflow we lose the *oldest* progress
        // tick — the latest count for the segment is still about to land soon.
        _channel.Writer.TryWrite(new ProgressEvent(downloadId, segmentIndex, bytesDownloaded));
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // Drain whatever's queued without waiting for the periodic timer.
        DrainChannelInto(_pending);
        await CommitPendingAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            DrainChannelInto(_pending);

            if (_pending.Count > 0)
            {
                try
                {
                    await CommitPendingAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to flush segment progress; will retry on next tick");
                }
            }
        }

        // Final drain on shutdown.
        DrainChannelInto(_pending);
        if (_pending.Count > 0)
        {
            try { await CommitPendingAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Final progress flush failed"); }
        }
    }

    private void DrainChannelInto(Dictionary<(long, int), long> sink)
    {
        while (_channel.Reader.TryRead(out var ev))
        {
            sink[(ev.DownloadId, ev.SegmentIndex)] = ev.BytesDownloaded;
            if (sink.Count >= MaxBatchSize) break;
        }
    }

    private async Task CommitPendingAsync(CancellationToken cancellationToken)
    {
        if (_pending.Count == 0) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE Segments SET BytesDownloaded = $bytes WHERE DownloadId = $did AND Idx = $idx";
        var pBytes = cmd.CreateParameter(); pBytes.ParameterName = "$bytes"; cmd.Parameters.Add(pBytes);
        var pDid   = cmd.CreateParameter(); pDid.ParameterName   = "$did";   cmd.Parameters.Add(pDid);
        var pIdx   = cmd.CreateParameter(); pIdx.ParameterName   = "$idx";   cmd.Parameters.Add(pIdx);
        cmd.Prepare();

        foreach (var ((downloadId, idx), bytes) in _pending)
        {
            pBytes.Value = bytes;
            pDid.Value = downloadId;
            pIdx.Value = idx;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        _pending.Clear();
    }

    private readonly record struct ProgressEvent(long DownloadId, int SegmentIndex, long BytesDownloaded);
}
