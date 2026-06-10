using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.Core.Http;
using SIDM.VideoGrabber;
using SIDM.VideoGrabber.Dash;
using SIDM.VideoGrabber.Hls;

namespace SIDM.App.ViewModels;

public partial class AddDownloadViewModel : ObservableObject
{
    [ObservableProperty]
    private string _url = "";

    [ObservableProperty]
    private string _targetFolder = DefaultDownloadsFolder();

    [ObservableProperty]
    private string _fileName = "";

    [ObservableProperty]
    private int _segments = 8;

    [ObservableProperty]
    private long? _expectedLength;

    [ObservableProperty]
    private string? _mime;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// yt-dlp format selector chosen by the browser-overlay picker (e.g.
    /// <c>"137+bestaudio/best"</c>). Null for plain HTTP captures; when set,
    /// the dialog renders a read-only "Format: …" row to confirm the
    /// resolution + size the user picked. Round-trips into
    /// <see cref="Download.SelectedYtDlpFormat"/> on accept.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasYtDlpFormat))]
    [NotifyPropertyChangedFor(nameof(ShowSegments))]
    private string? _ytDlpFormat;

    /// <summary>Human-readable label the dialog renders next to "Format:"
    /// (e.g. "1080p MP4 · ~145 MB"). Set together with <see cref="YtDlpFormat"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasYtDlpFormat))]
    [NotifyPropertyChangedFor(nameof(ShowSegments))]
    private string? _ytDlpFormatLabel;

    /// <summary>Drives the visibility of the format row.</summary>
    public bool HasYtDlpFormat => !string.IsNullOrEmpty(YtDlpFormat) && !string.IsNullOrEmpty(YtDlpFormatLabel);

    public bool IsValid => !string.IsNullOrWhiteSpace(Url)
                           && Uri.TryCreate(Url, UriKind.Absolute, out var u)
                           && (u.Scheme == "http" || u.Scheme == "https")
                           && !string.IsNullOrWhiteSpace(TargetFolder);

    public bool IsVideoUrl => YouTubeUrlDetector.IsVideoUrl(Url);
    public bool IsHlsUrl => HlsUrlDetector.IsHlsUrl(Url);
    public bool IsDashUrl => DashUrlDetector.IsDashUrl(Url);
    public bool IsSpecialUrl => IsVideoUrl || IsHlsUrl || IsDashUrl;

    /// <summary>
    /// The multi-segment slider only applies to plain HTTP downloads. Hide it
    /// for streaming/yt-dlp routes — and also when a format was chosen in the
    /// browser picker, since that always goes through yt-dlp even if the URL
    /// itself wasn't recognized as a video host.
    /// </summary>
    public bool ShowSegments => !IsSpecialUrl && !HasYtDlpFormat;

    public string TargetPath => Path.Combine(TargetFolder, string.IsNullOrWhiteSpace(FileName)
        ? GuessFileNameFromUrl(Url)
        : FileName);

    public string Extension => NormalizeExtension(FileName);

    public string SizeDisplay => ExpectedLength is { } len ? FormatBytes(len) : "Unknown";
    public string MimeDisplay => string.IsNullOrWhiteSpace(Mime) ? "—" : Mime!;

    /// <summary>Auto-fills <see cref="FileName"/> from the URL when the URL changes.</summary>
    partial void OnUrlChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(FileName))
        {
            FileName = GuessFileNameFromUrl(value);
        }
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(TargetPath));
        OnPropertyChanged(nameof(IsVideoUrl));
        OnPropertyChanged(nameof(IsHlsUrl));
        OnPropertyChanged(nameof(IsDashUrl));
        OnPropertyChanged(nameof(IsSpecialUrl));
        OnPropertyChanged(nameof(ShowSegments));
    }

    partial void OnFileNameChanged(string value)
    {
        OnPropertyChanged(nameof(TargetPath));
        OnPropertyChanged(nameof(Extension));
    }

    partial void OnTargetFolderChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(TargetPath));
    }

    partial void OnExpectedLengthChanged(long? value) => OnPropertyChanged(nameof(SizeDisplay));
    partial void OnMimeChanged(string? value) => OnPropertyChanged(nameof(MimeDisplay));

    public static string DefaultDownloadsFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads");
    }

    public static string GuessFileNameFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        var name = Path.GetFileName(uri.AbsolutePath);
        name = string.IsNullOrWhiteSpace(name) ? "" : Uri.UnescapeDataString(name);

        // When the path segment is an opaque id (no extension / hash / GUID),
        // the real name is often embedded in the query — the presigned-CDN
        // shape (S3 response-content-disposition, Hugging Face Xet) where the
        // path is a content hash. Prefer that over the useless path segment.
        if (LooksUnreliableFileName(name))
        {
            var fromQuery = FileNameResolver.FromUrlQuery(uri);
            if (!string.IsNullOrWhiteSpace(fromQuery)) return fromQuery!;
        }

        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }

    /// <summary>
    /// True when a candidate file name shouldn't be trusted as-is and is
    /// worth a HEAD probe to recover the real name from Content-Disposition.
    /// Two cases:
    ///   - no file extension (CDN URLs whose last segment is an opaque id), or
    ///   - the name (sans extension) is a bare GUID, e.g. GitHub release
    ///     assets redirect to <c>…/&lt;guid&gt;</c>.
    /// </summary>
    public static bool LooksUnreliableFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return true;

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return true;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return GuidLike.IsMatch(stem);
    }

    private static readonly System.Text.RegularExpressions.Regex GuidLike =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Returns the lowercased extension WITHOUT the leading dot. ".tar.gz" is
    /// not handled specially — we use the last component only, which is fine
    /// for the per-type folder memory feature.
    /// </summary>
    public static string NormalizeExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return "";
        return ext.TrimStart('.').ToLowerInvariant();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.CurrentCulture, "{0:F1} {1}", value, units[unit]);
    }
}
