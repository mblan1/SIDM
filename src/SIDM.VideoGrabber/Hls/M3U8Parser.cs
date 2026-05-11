using System.Globalization;

namespace SIDM.VideoGrabber.Hls;

/// <summary>
/// Line-oriented parser for HLS playlists (RFC 8216, partial). Implements the
/// tags SIDM v1 cares about:
///   #EXTM3U, #EXT-X-VERSION (ignored), #EXT-X-TARGETDURATION,
///   #EXT-X-MEDIA-SEQUENCE, #EXT-X-ENDLIST, #EXT-X-MAP (detection only),
///   #EXT-X-STREAM-INF (master), #EXT-X-KEY, #EXTINF, plain URI lines.
///
/// Unknown tags are silently skipped. The parser does not resolve relative
/// URIs on its own — that's the caller's job via <see cref="ResolveUri"/>.
/// </summary>
public static class M3U8Parser
{
    /// <summary>True if the playlist contains any variant declarations (master playlist).</summary>
    public static bool IsMasterPlaylist(string text) =>
        text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    /// <summary>
    /// Parses a master playlist. The base URI is required so that variant URIs
    /// declared relatively (e.g. "1080p/index.m3u8") become absolute.
    /// </summary>
    public static HlsMasterPlaylist ParseMaster(string text, Uri baseUri)
    {
        var variants = new List<HlsVariant>();
        HlsStreamInf? pendingInf = null;

        foreach (var rawLine in EnumerateLines(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                var attrs = ParseAttributes(line["#EXT-X-STREAM-INF:".Length..]);
                attrs.TryGetValue("BANDWIDTH", out var bwRaw);
                attrs.TryGetValue("RESOLUTION", out var resolution);
                attrs.TryGetValue("CODECS", out var codecs);
                long bandwidth = 0;
                if (bwRaw is not null) long.TryParse(bwRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out bandwidth);
                pendingInf = new HlsStreamInf(bandwidth, resolution, codecs);
                continue;
            }

            // Comment / unknown tag — skip without resetting pendingInf so a
            // STREAM-INF immediately followed by a tag line still pairs with
            // the next URI.
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;

            if (pendingInf is { } inf)
            {
                variants.Add(new HlsVariant(
                    Url: ResolveUri(baseUri, line),
                    Bandwidth: inf.Bandwidth,
                    Resolution: inf.Resolution,
                    Codecs: inf.Codecs));
                pendingInf = null;
            }
        }

        return new HlsMasterPlaylist(variants);
    }

    /// <summary>Parses a media playlist (segments + encryption + EXTINF).</summary>
    public static HlsMediaPlaylist ParseMedia(string text, Uri baseUri)
    {
        var segments = new List<HlsSegment>();
        int targetDuration = 0;
        long mediaSequence = 0;
        bool hasEndList = false;
        bool hasMap = false;

        HlsKey? currentKey = null;
        double pendingDuration = 0;
        bool extInfSeen = false;
        long nextMsn = 0;
        bool nextMsnInitialized = false;

        foreach (var rawLine in EnumerateLines(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                int.TryParse(line["#EXT-X-TARGETDURATION:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out targetDuration);
                continue;
            }
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out mediaSequence);
                continue;
            }
            if (line.Equals("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                hasEndList = true;
                continue;
            }
            if (line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal))
            {
                hasMap = true;
                continue;
            }
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                currentKey = ParseKey(line["#EXT-X-KEY:".Length..], baseUri);
                continue;
            }
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var payload = line["#EXTINF:".Length..];
                var comma = payload.IndexOf(',');
                var durStr = comma >= 0 ? payload[..comma] : payload;
                double.TryParse(durStr, NumberStyles.Float, CultureInfo.InvariantCulture, out pendingDuration);
                extInfSeen = true;
                continue;
            }
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue; // Unknown tag — skip.
            }

            // Bare URI line — pair with the pending EXTINF.
            if (!extInfSeen) continue; // Out-of-order URI without EXTINF; skip.

            if (!nextMsnInitialized)
            {
                nextMsn = mediaSequence;
                nextMsnInitialized = true;
            }

            segments.Add(new HlsSegment(
                Url: ResolveUri(baseUri, line),
                DurationSeconds: pendingDuration,
                MediaSequenceNumber: nextMsn,
                Key: currentKey));
            nextMsn++;
            pendingDuration = 0;
            extInfSeen = false;
        }

        return new HlsMediaPlaylist(
            TargetDuration: targetDuration,
            MediaSequence: mediaSequence,
            IsLive: !hasEndList,
            IsFmp4: hasMap,
            Segments: segments);
    }

    /// <summary>
    /// Resolves a URI line (which may be absolute or relative) against the
    /// playlist's own URI. Made public so callers / tests can verify behavior.
    /// </summary>
    public static Uri ResolveUri(Uri baseUri, string line)
    {
        if (Uri.TryCreate(line, UriKind.Absolute, out var absolute)) return absolute;
        return new Uri(baseUri, line);
    }

    private static HlsKey? ParseKey(string attrs, Uri baseUri)
    {
        var parsed = ParseAttributes(attrs);
        if (!parsed.TryGetValue("METHOD", out var method)) return null;
        if (method.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return null;

        if (!parsed.TryGetValue("URI", out var uriStr) || string.IsNullOrEmpty(uriStr)) return null;
        var keyUrl = ResolveUri(baseUri, uriStr);

        byte[]? iv = null;
        if (parsed.TryGetValue("IV", out var ivStr) && !string.IsNullOrEmpty(ivStr))
        {
            iv = ParseHexIv(ivStr);
        }
        return new HlsKey(method, keyUrl, iv);
    }

    /// <summary>Parses an HLS hex IV ("0x..." form, 32 hex chars after prefix). Returns 16 bytes or null on malformed input.</summary>
    public static byte[]? ParseHexIv(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }
        if (s.Length != 32) return null;
        var bytes = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return null;
            }
        }
        return bytes;
    }

    /// <summary>
    /// Parses an HLS attribute list: <c>KEY1=value,KEY2="quoted value",KEY3=0xABCD</c>.
    /// Handles quoted strings (so commas inside quotes don't split). Keys are
    /// upper-cased; quotes around values are stripped.
    /// </summary>
    public static Dictionary<string, string> ParseAttributes(string attrs)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        var len = attrs.Length;

        while (i < len)
        {
            // Skip leading whitespace / commas.
            while (i < len && (attrs[i] == ',' || char.IsWhiteSpace(attrs[i]))) i++;
            if (i >= len) break;

            var keyStart = i;
            while (i < len && attrs[i] != '=') i++;
            if (i >= len) break;
            var key = attrs[keyStart..i].Trim().ToUpperInvariant();
            i++; // Past '='.

            string value;
            if (i < len && attrs[i] == '"')
            {
                i++;
                var valueStart = i;
                while (i < len && attrs[i] != '"') i++;
                value = attrs[valueStart..i];
                if (i < len) i++; // Past closing quote.
            }
            else
            {
                var valueStart = i;
                while (i < len && attrs[i] != ',') i++;
                value = attrs[valueStart..i].Trim();
            }
            dict[key] = value;
        }

        return dict;
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        // Tolerate CRLF, LF, and CR line endings.
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n' || c == '\r')
            {
                yield return text[start..i];
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
        if (start < text.Length) yield return text[start..];
    }

    private sealed record HlsStreamInf(long Bandwidth, string? Resolution, string? Codecs);
}
