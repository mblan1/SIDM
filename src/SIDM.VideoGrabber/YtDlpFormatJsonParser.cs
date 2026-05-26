using System.Text.Json;

namespace SIDM.VideoGrabber;

/// <summary>
/// One row in the picker output. Mirrors the IPC <c>YtDlpFormatOption</c> shape
/// but lives in this layer so VideoGrabber stays independent of SIDM.Ipc
/// (Core/Data/Ipc/VideoGrabber don't reference each other; the App layer
/// maps between this and the wire type).
/// </summary>
public sealed record YtDlpFormatOption(
    string FormatId,
    string Kind,
    string Label,
    string Ext,
    string? Vcodec = null,
    string? Acodec = null,
    int? Height = null,
    int? Fps = null,
    int? AudioBitrateKbps = null,
    long? FileSize = null);

/// <summary>
/// Parses the JSON yt-dlp emits with <c>-J</c> (info-dump mode) and reduces
/// the full format list to the few rows the browser-overlay picker actually
/// renders: best video format per resolution, plus a few audio-only options.
///
/// Pure function over a JSON string — no process spawning, no I/O. Spawning
/// yt-dlp itself is <see cref="YtDlpProcessRunner.ListFormatsAsync"/>'s job.
/// </summary>
public static class YtDlpFormatJsonParser
{
    /// <summary>
    /// Parse the raw stdout of <c>yt-dlp -J &lt;url&gt;</c>. Returns the
    /// video title plus a curated <see cref="YtDlpFormatOption"/> list. Throws
    /// <see cref="JsonException"/> when the payload isn't a yt-dlp info
    /// dump (e.g. an error message slipped through).
    /// </summary>
    public static (string? Title, YtDlpFormatOption[] Formats) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, Array.Empty<YtDlpFormatOption>());
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, Array.Empty<YtDlpFormatOption>());
        }

        var title = root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
            ? titleEl.GetString()
            : null;

        if (!root.TryGetProperty("formats", out var formatsEl) || formatsEl.ValueKind != JsonValueKind.Array)
        {
            return (title, Array.Empty<YtDlpFormatOption>());
        }

        // Stage 1: pull every format into a flat in-memory shape.
        var raws = new List<RawFormat>();
        foreach (var el in formatsEl.EnumerateArray())
        {
            var raw = ReadRaw(el);
            if (raw is not null) raws.Add(raw);
        }

        // Stage 2: split into video-with-(or-could-have)-audio rows by
        // resolution, and audio-only rows by codec/bitrate.
        var videos = BuildVideoOptions(raws);
        var audios = BuildAudioOptions(raws);

        // Order: videos first (highest → lowest), then audio (highest → lowest).
        var combined = videos.Concat(audios).ToArray();
        return (title, combined);
    }

    // ---- internals ----

    private sealed record RawFormat(
        string FormatId,
        string? Ext,
        string? Vcodec,
        string? Acodec,
        int? Height,
        int? Width,
        int? Fps,
        double? Tbr,   // total bitrate kbps
        double? Abr,   // audio bitrate kbps
        long? FileSize,
        long? FileSizeApprox);

    private static RawFormat? ReadRaw(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var id = el.TryGetProperty("format_id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;

        return new RawFormat(
            FormatId: id,
            Ext: ReadString(el, "ext"),
            Vcodec: NormalizeCodec(ReadString(el, "vcodec")),
            Acodec: NormalizeCodec(ReadString(el, "acodec")),
            Height: ReadInt(el, "height"),
            Width: ReadInt(el, "width"),
            Fps: ReadInt(el, "fps"),
            Tbr: ReadDouble(el, "tbr"),
            Abr: ReadDouble(el, "abr"),
            FileSize: ReadLong(el, "filesize"),
            FileSizeApprox: ReadLong(el, "filesize_approx"));
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? ReadInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    private static long? ReadLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : null;

    private static double? ReadDouble(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)
            ? n
            : null;

    /// <summary>yt-dlp uses the literal "none" for missing codec — flatten to null.</summary>
    private static string? NormalizeCodec(string? codec) =>
        string.IsNullOrEmpty(codec) || codec.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null
            : codec;

    private static IEnumerable<YtDlpFormatOption> BuildVideoOptions(IReadOnlyList<RawFormat> raws)
    {
        // A "video" candidate is any format with a vcodec. We group by height
        // and pick one representative per resolution. Preference:
        //   1. Format that also has an acodec (single-file MP4) — playable
        //      out of the box without ffmpeg merging.
        //   2. Otherwise the highest tbr at that height.
        // The picker shows that one row labeled "<height>p"; yt-dlp's "+bestaudio"
        // selector at download time handles the audio merge for video-only formats.
        var videoCandidates = raws.Where(r => r.Vcodec is not null && r.Height is { } h && h > 0).ToList();

        var groupedByHeight = videoCandidates
            .GroupBy(r => r.Height!.Value)
            .OrderByDescending(g => g.Key);

        foreach (var group in groupedByHeight)
        {
            var pick = group
                .OrderByDescending(r => r.Acodec is not null) // muxed first
                .ThenByDescending(r => r.Tbr ?? 0)
                .First();

            var formatSelector = pick.Acodec is not null
                ? pick.FormatId
                // Video-only — pair with best audio via yt-dlp's "+bestaudio"
                // operator. yt-dlp will pick a compatible audio track and ffmpeg
                // will mux. If ffmpeg isn't available the download will still
                // succeed but the audio will be a separate file — acceptable
                // fallback (the engine already handles that case today).
                : $"{pick.FormatId}+bestaudio/best";

            yield return new YtDlpFormatOption(
                FormatId: formatSelector,
                Kind: "video",
                Label: FormatVideoLabel(pick.Height!.Value, pick.Fps),
                Ext: pick.Ext ?? "",
                Vcodec: pick.Vcodec,
                Acodec: pick.Acodec,
                Height: pick.Height,
                Fps: pick.Fps,
                AudioBitrateKbps: null,
                FileSize: pick.FileSize ?? pick.FileSizeApprox);
        }
    }

    private static IEnumerable<YtDlpFormatOption> BuildAudioOptions(IReadOnlyList<RawFormat> raws)
    {
        // Audio-only: vcodec is null AND acodec is present.
        var audioCandidates = raws
            .Where(r => r.Vcodec is null && r.Acodec is not null)
            .OrderByDescending(r => r.Abr ?? r.Tbr ?? 0)
            .ToList();

        // Cap at 4 audio rows — more is just noise for the typical user.
        foreach (var r in audioCandidates.Take(4))
        {
            var bitrate = (int?)(r.Abr ?? r.Tbr);
            yield return new YtDlpFormatOption(
                FormatId: r.FormatId,
                Kind: "audio",
                Label: FormatAudioLabel(r.Acodec!, bitrate),
                Ext: r.Ext ?? "",
                Vcodec: null,
                Acodec: r.Acodec,
                Height: null,
                Fps: null,
                AudioBitrateKbps: bitrate,
                FileSize: r.FileSize ?? r.FileSizeApprox);
        }
    }

    private static string FormatVideoLabel(int height, int? fps)
    {
        // Friendly resolution names users actually recognize.
        var pretty = height switch
        {
            >= 4320 => "8K",
            >= 2160 => "4K",
            >= 1440 => "1440p",
            >= 1080 => "1080p",
            >= 720 => "720p",
            >= 480 => "480p",
            >= 360 => "360p",
            >= 240 => "240p",
            _ => $"{height}p",
        };
        return fps is > 30 ? $"{pretty}{fps}" : pretty;
    }

    private static string FormatAudioLabel(string acodec, int? bitrateKbps)
    {
        var codec = acodec switch
        {
            var c when c.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) => "M4A (AAC)",
            var c when c.StartsWith("opus", StringComparison.OrdinalIgnoreCase) => "Opus",
            var c when c.StartsWith("vorbis", StringComparison.OrdinalIgnoreCase) => "Vorbis",
            var c when c.StartsWith("mp3", StringComparison.OrdinalIgnoreCase) => "MP3",
            _ => acodec,
        };
        return bitrateKbps is > 0 ? $"{codec} · {bitrateKbps}k" : codec;
    }
}
