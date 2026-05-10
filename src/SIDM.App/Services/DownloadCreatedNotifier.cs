namespace SIDM.App.Services;

/// <summary>
/// In-process pub/sub for "a new download row was just created". The
/// <see cref="IpcDispatcher"/> publishes whenever a browser extension hands us
/// a download via the named pipe; the <see cref="ViewModels.DownloadsViewModel"/>
/// subscribes so the running window picks up IPC-initiated downloads without
/// needing to be reopened.
///
/// Handlers run on whatever thread <see cref="Publish"/> is called from —
/// subscribers must marshal to their own UI dispatcher if needed.
/// </summary>
public sealed class DownloadCreatedNotifier
{
    public event Action<long>? Created;

    public void Publish(long downloadId) => Created?.Invoke(downloadId);
}
