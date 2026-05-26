namespace SIDM.Core.Models;

public class Download
{
    public long Id { get; set; }
    public required string Url { get; set; }
    public string? EffectiveUrl { get; set; }
    public required string FileName { get; set; }
    public required string TargetPath { get; set; }
    public long? TotalBytes { get; set; }
    public DownloadStatus Status { get; set; }
    public DownloadPriority Priority { get; set; } = DownloadPriority.Normal;
    public long? CategoryId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? Mime { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? CookiesJson { get; set; }
    public string? HeadersJson { get; set; }
    public string? ExpectedHash { get; set; }
    public string? HashAlgo { get; set; }
    public string? ErrorMessage { get; set; }
    public int SegmentCount { get; set; }
    public SourceKind SourceKind { get; set; } = SourceKind.Direct;
    public string? Manifest { get; set; }
    public int RetryCount { get; set; }

    /// <summary>
    /// yt-dlp format selector chosen by the user — e.g. <c>"137+140"</c> for
    /// 1080p video + best audio, or <c>"bestaudio[ext=m4a]"</c> for an
    /// audio-only download. Null means "let yt-dlp pick its default" (the
    /// engine then falls back to <c>bestvideo*+bestaudio/best</c>). Set by
    /// the browser-overlay format picker; ignored for non-yt-dlp routes.
    /// </summary>
    public string? SelectedYtDlpFormat { get; set; }

    public List<Segment> Segments { get; set; } = [];
}
