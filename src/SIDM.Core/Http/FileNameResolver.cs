using System.Text.RegularExpressions;

namespace SIDM.Core.Http;

/// <summary>
/// Recovers a human filename embedded in a download URL's query string. This is
/// the common shape for presigned CDN links — Amazon S3
/// <c>response-content-disposition</c>, Hugging Face Xet, Google — whose path
/// is an opaque content hash (e.g. <c>…/e7bb6a02fd00…</c>) but whose query
/// carries the real <c>…filename="name.ext"</c>. Network-free, so it works even
/// when the final response omits the <c>Content-Disposition</c> header.
/// </summary>
public static partial class FileNameResolver
{
    // RFC 5987 form: filename*=UTF-8''<percent-encoded>. Preferred when present.
    [GeneratedRegex(@"filename\*\s*=\s*[^']*''([^;&""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameStar();

    // Quoted form: filename="<value>".
    [GeneratedRegex(@"filename\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameQuoted();

    // Bare form: filename=<value> (stops at ; & or ").
    [GeneratedRegex(@"filename\s*=\s*([^;&""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileNamePlain();

    /// <summary>
    /// Returns a sanitized filename parsed from the URL's query string, or null
    /// when the query carries no usable (extension-bearing) name.
    /// </summary>
    public static string? FromUrlQuery(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? FromUrlQuery(uri) : null;
    }

    /// <inheritdoc cref="FromUrlQuery(string?)"/>
    public static string? FromUrlQuery(Uri uri)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query)) return null;

        // Decode once so percent-encoded tokens (e.g. an S3
        // response-content-disposition param, or a filename buried in a signed
        // policy blob) are matchable regardless of how the link was encoded.
        var decoded = SafeUnescape(query);

        foreach (var rx in new[] { FileNameStar(), FileNameQuoted(), FileNamePlain() })
        {
            var m = rx.Match(decoded);
            if (!m.Success) continue;

            // RFC 5987 values are themselves percent-encoded after the charset.
            var raw = SafeUnescape(m.Groups[1].Value).Trim().Trim('"');
            var name = Sanitize(raw);

            // Only trust an extension-bearing name — a bare token here is more
            // likely a stray match than a real download name.
            if (!string.IsNullOrWhiteSpace(name) && Path.HasExtension(name))
            {
                return name;
            }
        }
        return null;
    }

    /// <summary>
    /// Strips any directory component (defends against an embedded path) and
    /// replaces characters that are invalid in a Windows filename.
    /// </summary>
    public static string Sanitize(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Trim();
    }

    private static string SafeUnescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }
}
