using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SIDM.VideoGrabber;
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

    public bool IsValid => !string.IsNullOrWhiteSpace(Url)
                           && Uri.TryCreate(Url, UriKind.Absolute, out var u)
                           && (u.Scheme == "http" || u.Scheme == "https")
                           && !string.IsNullOrWhiteSpace(TargetFolder);

    public bool IsVideoUrl => YouTubeUrlDetector.IsVideoUrl(Url);
    public bool IsHlsUrl => HlsUrlDetector.IsHlsUrl(Url);
    public bool IsSpecialUrl => IsVideoUrl || IsHlsUrl;

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
        OnPropertyChanged(nameof(IsSpecialUrl));
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
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : Uri.UnescapeDataString(name);
    }

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
