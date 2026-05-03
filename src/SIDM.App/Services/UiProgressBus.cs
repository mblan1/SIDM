using System.Collections.Concurrent;
using SIDM.Core.Persistence;

namespace SIDM.App.Services;

/// <summary>
/// In-memory pub/sub bus for download progress. Implements
/// <see cref="IDownloadProgressSink"/> on the publisher side and exposes a
/// per-downloadId event on the subscriber side. The UI thread subscribes; the
/// engine publishes from worker threads. Subscribers must marshal to the
/// dispatcher themselves — this bus does not assume WPF.
/// </summary>
public sealed class UiProgressBus : IDownloadProgressSink
{
    private readonly ConcurrentDictionary<long, ProgressTopic> _topics = new();

    public void Report(long downloadId, int segmentIndex, long bytesDownloaded)
    {
        if (_topics.TryGetValue(downloadId, out var topic))
        {
            topic.Publish(segmentIndex, bytesDownloaded);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IDisposable Subscribe(long downloadId, Action<int /* segmentIndex */, long /* bytes */> handler)
    {
        var topic = _topics.GetOrAdd(downloadId, _ => new ProgressTopic());
        topic.Add(handler);
        return new Subscription(topic, handler);
    }

    private sealed class ProgressTopic
    {
        private readonly object _lock = new();
        private List<Action<int, long>> _handlers = new();

        public void Add(Action<int, long> h)
        {
            lock (_lock) { _handlers = new List<Action<int, long>>(_handlers) { h }; }
        }

        public void Remove(Action<int, long> h)
        {
            lock (_lock)
            {
                var copy = _handlers.ToList();
                copy.Remove(h);
                _handlers = copy;
            }
        }

        public void Publish(int idx, long bytes)
        {
            // Snapshot under lock; invoke outside.
            List<Action<int, long>> snapshot;
            lock (_lock) { snapshot = _handlers; }
            foreach (var h in snapshot)
            {
                try { h(idx, bytes); } catch { /* a single subscriber should not break others */ }
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private ProgressTopic? _topic;
        private readonly Action<int, long> _handler;

        public Subscription(ProgressTopic topic, Action<int, long> handler)
        {
            _topic = topic;
            _handler = handler;
        }

        public void Dispose()
        {
            _topic?.Remove(_handler);
            _topic = null;
        }
    }
}
