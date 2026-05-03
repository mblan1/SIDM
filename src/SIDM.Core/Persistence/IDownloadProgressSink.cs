namespace SIDM.Core.Persistence;

/// <summary>
/// Lower-level progress sink that knows about download identity. The orchestrator
/// adapts a per-download <see cref="SIDM.Core.Engine.ISegmentProgressSink"/> to call
/// this with the download id captured at construction time.
/// </summary>
public interface IDownloadProgressSink
{
    void Report(long downloadId, int segmentIndex, long bytesDownloaded);

    Task FlushAsync(CancellationToken cancellationToken = default);
}
