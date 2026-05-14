using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SIDM.App.Services;

/// <summary>
/// Returns the real, colored Windows shell icon for a given file extension
/// — the same icon Explorer shows. Uses <c>SHGetFileInfo</c> with the
/// <c>USEFILEATTRIBUTES</c> flag so we never need the file to exist on disk;
/// passing "dummy.mp4" is enough to retrieve the registered .mp4 icon.
///
/// Icons are looked up once per extension and cached. The cache lives for
/// the lifetime of the process — extensions are a small fixed set in practice.
/// Returns a frozen <see cref="ImageSource"/> safe to bind to from any thread.
/// </summary>
public static class FileIconProvider
{
    private static readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the shell icon for the given lowercase, dot-less extension
    /// (e.g. "mp4", "zip"). Returns <c>null</c> if the shell call fails;
    /// callers should fall back to a generic icon.
    /// </summary>
    public static ImageSource? GetIcon(string? extensionLower)
    {
        var ext = string.IsNullOrWhiteSpace(extensionLower) ? string.Empty : extensionLower;
        return _cache.GetOrAdd(ext, LoadIconForExtension);
    }

    private static ImageSource? LoadIconForExtension(string ext)
    {
        // SHGetFileInfo needs SOMETHING to look at; "dummy.<ext>" with the
        // USEFILEATTRIBUTES flag tells the shell to use the extension only.
        var fakePath = string.IsNullOrEmpty(ext) ? "dummy" : $"dummy.{ext}";

        var shfi = new SHFILEINFO();
        var result = SHGetFileInfo(
            fakePath,
            FILE_ATTRIBUTE_NORMAL,
            ref shfi,
            (uint)Marshal.SizeOf(shfi),
            SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | SHGFI_LARGEICON);

        if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }

    // ---- P/Invoke ----

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
