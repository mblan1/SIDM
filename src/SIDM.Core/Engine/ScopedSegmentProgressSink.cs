using SIDM.Core.Persistence;

namespace SIDM.Core.Engine;

/// <summary>
/// Adapts an <see cref="IDownloadProgressSink"/> (which needs a download id) to the
/// per-worker <see cref="ISegmentProgressSink"/> contract by capturing the id at
/// construction.
/// </summary>
public sealed class ScopedSegmentProgressSink : ISegmentProgressSink
{
    private readonly long _downloadId;
    private readonly IDownloadProgressSink _inner;

    public ScopedSegmentProgressSink(long downloadId, IDownloadProgressSink inner)
    {
        _downloadId = downloadId;
        _inner = inner;
    }

    public void Report(int segmentIndex, long bytesDownloaded) =>
        _inner.Report(_downloadId, segmentIndex, bytesDownloaded);
}
