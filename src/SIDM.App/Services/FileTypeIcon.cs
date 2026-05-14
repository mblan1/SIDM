using Wpf.Ui.Controls;

namespace SIDM.App.Services;

/// <summary>
/// Maps a file extension to a Wpf.Ui <see cref="SymbolRegular"/> for the
/// per-row icon in the downloads grid. The mapping favors recognizability
/// over precision — we ship a single icon per category (video, audio,
/// archive, document, exe, image) rather than a unique glyph per format.
/// </summary>
public static class FileTypeIcon
{
    public static SymbolRegular ForExtension(string? extensionLower)
    {
        if (string.IsNullOrWhiteSpace(extensionLower)) return SymbolRegular.Document24;

        return extensionLower switch
        {
            // Video
            "mp4" or "mkv" or "webm" or "avi" or "mov" or "wmv" or "flv"
                or "ts" or "m3u8" or "mpd" or "m4v" or "mpg" or "mpeg"
                or "3gp" or "vob" or "ogv"
                => SymbolRegular.VideoClip24,

            // Audio
            "mp3" or "wav" or "flac" or "m4a" or "aac" or "ogg" or "opus"
                or "wma" or "ape" or "alac" or "aiff"
                => SymbolRegular.MusicNote124,

            // Archive
            "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz" or "zst"
                or "iso" or "lzma" or "lz4" or "cab"
                => SymbolRegular.FolderZip24,

            // Documents
            "pdf" => SymbolRegular.DocumentPdf24,
            "doc" or "docx" or "odt" or "rtf"
                => SymbolRegular.DocumentText24,
            "xls" or "xlsx" or "ods" or "csv"
                => SymbolRegular.DocumentTable24,
            "ppt" or "pptx" or "odp"
                => SymbolRegular.DocumentBulletList24,
            "txt" or "log" or "md" or "json" or "xml" or "yaml" or "yml" or "ini"
                => SymbolRegular.DocumentText24,
            "epub" or "mobi" or "azw" or "azw3"
                => SymbolRegular.BookOpen24,

            // Images
            "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "tiff" or "tif"
                or "svg" or "ico" or "heic" or "heif" or "raw" or "psd"
                => SymbolRegular.Image24,

            // Installers / executables
            "exe" or "msi" or "msix" or "appx"
                => SymbolRegular.AppGeneric24,
            "dmg" or "pkg" => SymbolRegular.AppGeneric24,
            "deb" or "rpm" or "apk" => SymbolRegular.AppGeneric24,

            // Code / dev
            "cs" or "csproj" or "sln" or "js" or "ts" or "tsx" or "jsx"
                or "html" or "htm" or "css" or "scss" or "less"
                or "py" or "rb" or "go" or "rs" or "java" or "kt" or "swift"
                or "c" or "cpp" or "h" or "hpp" or "sh" or "ps1" or "bat"
                => SymbolRegular.Code24,

            _ => SymbolRegular.Document24,
        };
    }
}
