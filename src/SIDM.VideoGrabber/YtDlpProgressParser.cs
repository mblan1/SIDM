using System.Globalization;

namespace SIDM.VideoGrabber;

/// <summary>
/// Parses lines emitted by yt-dlp when invoked with the progress template
/// <c>SIDM:%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.fragment_index)s|%(progress.fragment_count)s|%(progress.eta)s|%(progress.speed)s</c>.
///
/// yt-dlp prints the literal string "NA" for fields it cannot fill (e.g. when
/// the total size is unknown until late, or for non-fragmented formats). The
/// parser treats "NA", empty, and missing as null. The "SIDM:" prefix lets
/// us distinguish progress lines from yt-dlp's other stdout chatter without
/// having to consume every line.
/// </summary>
public static class YtDlpProgressParser
{
    public const string LinePrefix = "SIDM:";

    /// <summary>
    /// The literal value of <c>--progress-template</c> we pass to yt-dlp. Kept
    /// next to the parser so the format and parser can't drift.
    /// </summary>
    public const string ProgressTemplate =
        LinePrefix + "%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.fragment_index)s|%(progress.fragment_count)s|%(progress.eta)s|%(progress.speed)s";

    public static bool TryParse(string line, out YtDlpProgress progress)
    {
        progress = default!;
        if (string.IsNullOrEmpty(line)) return false;
        if (!line.StartsWith(LinePrefix, StringComparison.Ordinal)) return false;

        var payload = line.AsSpan(LinePrefix.Length).Trim();
        // Fields: downloaded_bytes|total_bytes|fragment_index|fragment_count|eta|speed
        var parts = payload.ToString().Split('|');
        if (parts.Length < 6) return false;

        if (!TryParseLong(parts[0], out var downloadedBytes)) return false;
        progress = new YtDlpProgress(
            DownloadedBytes: downloadedBytes,
            TotalBytes: ParseOptionalLong(parts[1]),
            FragmentIndex: ParseOptionalInt(parts[2]),
            FragmentCount: ParseOptionalInt(parts[3]),
            EtaSeconds: ParseOptionalInt(parts[4]),
            SpeedBytesPerSecond: ParseOptionalLong(parts[5]));
        return true;
    }

    private static bool TryParseLong(string raw, out long value)
    {
        // Some yt-dlp builds emit decimals for byte counts (e.g. "12345.0").
        // Accept both forms.
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            value = (long)d;
            return true;
        }
        value = 0;
        return false;
    }

    private static long? ParseOptionalLong(string raw)
    {
        if (IsUnknown(raw)) return null;
        return TryParseLong(raw, out var v) ? v : null;
    }

    private static int? ParseOptionalInt(string raw)
    {
        if (IsUnknown(raw)) return null;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (int)d;
        return null;
    }

    private static bool IsUnknown(string raw) =>
        string.IsNullOrWhiteSpace(raw) || raw.Equals("NA", StringComparison.OrdinalIgnoreCase) || raw == "None";
}
