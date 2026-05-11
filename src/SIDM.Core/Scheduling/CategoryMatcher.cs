using SIDM.Core.Models;

namespace SIDM.Core.Scheduling;

/// <summary>
/// Looks up which user-defined <see cref="Category"/> claims a given file
/// extension. Extensions on the category are stored as a comma- or
/// semicolon-separated string ("mp4,mkv;webm"); matching is case-insensitive
/// and the leading dot on either side is tolerated.
///
/// First match wins — the category list is enumerated in user-visible order
/// (typically alphabetical via repository ordering), so the user can resolve
/// ambiguity (e.g. "iso" claimed by both Images and Software) by renaming.
/// </summary>
public static class CategoryMatcher
{
    private static readonly char[] Separators = [',', ';', ' '];

    public static Category? Match(IReadOnlyList<Category> categories, string fileNameOrExtension)
    {
        var ext = Normalize(fileNameOrExtension);
        if (string.IsNullOrEmpty(ext)) return null;

        foreach (var c in categories)
        {
            if (c.Extensions is null) continue;
            foreach (var raw in c.Extensions.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(Normalize(raw), ext, StringComparison.Ordinal))
                {
                    return c;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Reduces "video.MP4", ".mp4", "MP4", " mp4 " all to "mp4". Returns the
    /// empty string when no extension is present.
    /// </summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var trimmed = input.Trim();

        // If the caller passed a file name, strip everything up to the last dot.
        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < trimmed.Length - 1)
        {
            trimmed = trimmed[(lastDot + 1)..];
        }
        else if (lastDot == trimmed.Length - 1)
        {
            // Trailing dot with no extension.
            return "";
        }

        return trimmed.ToLowerInvariant();
    }
}
