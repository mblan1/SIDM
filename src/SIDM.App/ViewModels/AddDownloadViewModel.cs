using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    private string? _errorMessage;

    public bool IsValid => !string.IsNullOrWhiteSpace(Url)
                           && Uri.TryCreate(Url, UriKind.Absolute, out var u)
                           && (u.Scheme == "http" || u.Scheme == "https")
                           && !string.IsNullOrWhiteSpace(TargetFolder);

    public string TargetPath => Path.Combine(TargetFolder, string.IsNullOrWhiteSpace(FileName)
        ? GuessFileNameFromUrl(Url)
        : FileName);

    /// <summary>Auto-fills <see cref="FileName"/> from the URL when the URL changes.</summary>
    partial void OnUrlChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(FileName))
        {
            FileName = GuessFileNameFromUrl(value);
        }
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(TargetPath));
    }

    partial void OnFileNameChanged(string value)
    {
        OnPropertyChanged(nameof(TargetPath));
    }

    partial void OnTargetFolderChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(TargetPath));
    }

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
}
