using SIDM.Core.Persistence;

namespace SIDM.App.Services;

/// <summary>
/// Fans out a single <see cref="IDownloadProgressSink.Report"/> call to multiple
/// downstream sinks. Used to persist progress to SQLite *and* notify the UI thread
/// without forcing either layer to know about the other.
/// </summary>
public sealed class CompositeProgressSink : IDownloadProgressSink
{
    private readonly IReadOnlyList<IDownloadProgressSink> _inner;

    public CompositeProgressSink(IEnumerable<IDownloadProgressSink> inner)
    {
        _inner = inner.ToArray();
    }

    public void Report(long downloadId, int segmentIndex, long bytesDownloaded)
    {
        foreach (var sink in _inner)
        {
            sink.Report(downloadId, segmentIndex, bytesDownloaded);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(_inner.Select(s => s.FlushAsync(cancellationToken)));
}
