using SIDM.Ipc;

namespace SIDM.App.Services;

/// <summary>
/// Bridges an inbound IPC download request to the WPF UI: shows the IDM-style
/// "Add download" popup (pre-filled with the request), and on user accept
/// creates the row, kicks the engine, and opens the per-download progress
/// window. Implemented by <see cref="ViewModels.DownloadsViewModel"/> which
/// owns the dispatcher and the rows collection.
/// </summary>
public interface IDownloadIntake
{
    Task<DownloadIntakeResult> PromptAsync(DownloadRequestMessage request, CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of the popup. When the user accepts, <see cref="DownloadId"/> is
/// non-null and <see cref="Status"/> reflects the queued/downloading state.
/// When the user cancels, <see cref="DownloadId"/> is null and
/// <see cref="Status"/> is "Canceled".
/// </summary>
public sealed record DownloadIntakeResult(long? DownloadId, string Status);
