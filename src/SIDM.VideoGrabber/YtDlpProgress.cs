namespace SIDM.VideoGrabber;

/// <summary>
/// A single progress sample parsed from yt-dlp's stdout while a download is
/// in flight. Bytes / total are absolute values (yt-dlp reports cumulatives);
/// fragment fields are present for HLS/DASH streams where the download is
/// composed of many small fragments rather than a single byte stream.
/// </summary>
/// <param name="DownloadedBytes">Bytes downloaded so far.</param>
/// <param name="TotalBytes">Estimated total bytes; null if unknown (live streams).</param>
/// <param name="FragmentIndex">For fragmented formats: index of the current fragment, 1-based.</param>
/// <param name="FragmentCount">For fragmented formats: total fragment count.</param>
/// <param name="EtaSeconds">ETA in seconds; null if unknown.</param>
/// <param name="SpeedBytesPerSecond">Current download speed in B/s; null if unknown.</param>
public sealed record YtDlpProgress(
    long DownloadedBytes,
    long? TotalBytes,
    int? FragmentIndex,
    int? FragmentCount,
    int? EtaSeconds,
    long? SpeedBytesPerSecond);
