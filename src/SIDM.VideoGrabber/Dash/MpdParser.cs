using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SIDM.VideoGrabber.Dash;

/// <summary>
/// Parser for MPEG-DASH MPD documents. SIDM v1 supports the common slice:
///   - static manifests (refuses dynamic / live with a clear signal),
///   - the first Period only (multi-period flattening is uncommon for VOD),
///   - SegmentTemplate with $Number$ + @duration (constant cadence) and
///     SegmentTemplate + SegmentTimeline (variable durations),
///   - $RepresentationID$, $Bandwidth$, $Number$, $Time$ template variables,
///   - Initialization derived from SegmentTemplate@initialization,
///   - ContentProtection detection (refused — DRM is out of scope).
///
/// Out of scope (will be added when real users hit them):
///   - SegmentList,
///   - SegmentBase + byte ranges in indexRange/initRange,
///   - BaseURL elements (the document URL is used as the base),
///   - in-band cue and timeline-update logic for live streams.
/// </summary>
public static class MpdParser
{
    public static DashManifest Parse(string xml, Uri baseUri)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var root = doc.Root ?? throw new InvalidOperationException("MPD root element missing.");

        var isDynamic = (root.Attribute("type")?.Value ?? "static")
            .Equals("dynamic", StringComparison.OrdinalIgnoreCase);
        var totalDuration = ParseIso8601Duration(root.Attribute("mediaPresentationDuration")?.Value);

        // Resolve namespace once; MPD documents use the DASH 2011 schema.
        XName N(string local) => XName.Get(local, root.GetDefaultNamespace().NamespaceName);

        var hasDrm = root.Descendants(N("ContentProtection")).Any();

        var firstPeriod = root.Elements(N("Period")).FirstOrDefault();
        if (firstPeriod is null)
        {
            return new DashManifest(isDynamic, hasDrm, Array.Empty<DashRepresentation>());
        }

        var reps = new List<DashRepresentation>();
        foreach (var adaptationSet in firstPeriod.Elements(N("AdaptationSet")))
        {
            var asMime = adaptationSet.Attribute("mimeType")?.Value;
            var asContentType = adaptationSet.Attribute("contentType")?.Value;
            var asCodecs = adaptationSet.Attribute("codecs")?.Value;
            var asTemplate = adaptationSet.Element(N("SegmentTemplate"));

            foreach (var rep in adaptationSet.Elements(N("Representation")))
            {
                var id = rep.Attribute("id")?.Value ?? "rep";
                long.TryParse(rep.Attribute("bandwidth")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bandwidth);

                var mime = rep.Attribute("mimeType")?.Value ?? asMime;
                var contentType = rep.Attribute("contentType")?.Value ?? asContentType;
                var codecs = rep.Attribute("codecs")?.Value ?? asCodecs;

                int? width = int.TryParse(rep.Attribute("width")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : null;
                int? height = int.TryParse(rep.Attribute("height")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : null;

                var template = rep.Element(N("SegmentTemplate")) ?? asTemplate;
                if (template is null)
                {
                    // No SegmentTemplate at this level. SegmentList is not supported
                    // in v1 — emit a representation with no segments so the caller
                    // can surface "no playable segments" upstream.
                    reps.Add(new DashRepresentation(
                        id, ClassifyContentKind(contentType, mime), bandwidth, mime, codecs, width, height,
                        InitSegmentUrl: null, MediaSegmentUrls: Array.Empty<Uri>()));
                    continue;
                }

                var (init, segments) = ExpandSegmentTemplate(template, N, baseUri, id, bandwidth, totalDuration);
                reps.Add(new DashRepresentation(
                    id, ClassifyContentKind(contentType, mime), bandwidth, mime, codecs, width, height,
                    InitSegmentUrl: init, MediaSegmentUrls: segments));
            }
        }

        return new DashManifest(isDynamic, hasDrm, reps);
    }

    private static (Uri? Init, IReadOnlyList<Uri> Segments) ExpandSegmentTemplate(
        XElement template,
        Func<string, XName> N,
        Uri baseUri,
        string representationId,
        long bandwidth,
        TimeSpan? totalDuration)
    {
        var initTemplate = template.Attribute("initialization")?.Value;
        var mediaTemplate = template.Attribute("media")?.Value;
        long.TryParse(template.Attribute("timescale")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timescale);
        if (timescale <= 0) timescale = 1;
        long.TryParse(template.Attribute("startNumber")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startNumber);
        if (startNumber <= 0) startNumber = 1;
        long.TryParse(template.Attribute("duration")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var segDuration);

        Uri? initUri = null;
        if (!string.IsNullOrEmpty(initTemplate))
        {
            initUri = new Uri(baseUri, ApplyTemplate(initTemplate, representationId, bandwidth, number: 0, time: 0));
        }

        if (string.IsNullOrEmpty(mediaTemplate))
        {
            return (initUri, Array.Empty<Uri>());
        }

        var timeline = template.Element(N("SegmentTimeline"));
        var segments = new List<Uri>();

        if (timeline is not null)
        {
            // Walk <S t="..." d="..." r="..."/> entries. @t is optional after the
            // first entry (inherits from previous). @r means "repeat this many
            // additional times" (so r=2 yields 3 total).
            long currentTime = 0;
            long number = startNumber;
            bool first = true;
            foreach (var s in timeline.Elements(N("S")))
            {
                if (long.TryParse(s.Attribute("t")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
                {
                    currentTime = t;
                }
                else if (first)
                {
                    currentTime = 0;
                }
                first = false;
                long.TryParse(s.Attribute("d")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d);
                long.TryParse(s.Attribute("r")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r);
                var count = r + 1;
                for (var i = 0; i < count; i++)
                {
                    segments.Add(new Uri(baseUri, ApplyTemplate(mediaTemplate, representationId, bandwidth, number, currentTime)));
                    number++;
                    currentTime += d;
                }
            }
        }
        else if (segDuration > 0 && totalDuration is { } total)
        {
            // Constant-duration template. Number of segments = ceil(total / (segDuration / timescale)).
            var segSeconds = (double)segDuration / timescale;
            var count = (int)Math.Ceiling(total.TotalSeconds / segSeconds);
            for (var i = 0; i < count; i++)
            {
                var number = startNumber + i;
                var time = i * segDuration;
                segments.Add(new Uri(baseUri, ApplyTemplate(mediaTemplate, representationId, bandwidth, number, time)));
            }
        }

        return (initUri, segments);
    }

    /// <summary>
    /// Expands the four standard DASH template variables. Supports the
    /// width-suffix form like <c>$Number%05d$</c> per ISO/IEC 23009-1 §5.3.9.4.4.
    /// </summary>
    public static string ApplyTemplate(string template, string representationId, long bandwidth, long number, long time)
    {
        return ReplaceVar(
            ReplaceVar(
                ReplaceVar(
                    ReplaceVar(template, "RepresentationID", representationId),
                    "Bandwidth", bandwidth.ToString(CultureInfo.InvariantCulture)),
                "Number", number.ToString(CultureInfo.InvariantCulture), allowWidth: true, value: number),
            "Time", time.ToString(CultureInfo.InvariantCulture), allowWidth: true, value: time);
    }

    private static string ReplaceVar(string template, string name, string defaultValue, bool allowWidth = false, long value = 0)
    {
        // Match $Name$ and (optionally) $Name%0Nd$ where N is the zero-pad width.
        var s = template;
        var marker = "$" + name;
        int idx;
        while ((idx = s.IndexOf(marker, StringComparison.Ordinal)) >= 0)
        {
            var end = s.IndexOf('$', idx + marker.Length);
            if (end < 0) break;

            var fmt = s.Substring(idx + marker.Length, end - idx - marker.Length);
            string replacement;
            if (allowWidth && fmt.StartsWith("%0", StringComparison.Ordinal) && fmt.EndsWith("d", StringComparison.Ordinal))
            {
                if (int.TryParse(fmt.AsSpan(2, fmt.Length - 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width))
                {
                    replacement = value.ToString("D" + width, CultureInfo.InvariantCulture);
                }
                else
                {
                    replacement = defaultValue;
                }
            }
            else
            {
                replacement = defaultValue;
            }
            s = s[..idx] + replacement + s[(end + 1)..];
        }
        return s;
    }

    private static DashContentKind ClassifyContentKind(string? contentType, string? mimeType)
    {
        var hint = (contentType ?? mimeType ?? "").ToLowerInvariant();
        if (hint.Contains("video")) return DashContentKind.Video;
        if (hint.Contains("audio")) return DashContentKind.Audio;
        return DashContentKind.Other;
    }

    /// <summary>
    /// Parses the subset of ISO 8601 duration MPDs use:
    /// <c>PT[h]H[m]M[s][.fraction]S</c>. Returns null on missing/invalid input.
    /// </summary>
    public static TimeSpan? ParseIso8601Duration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return XmlConvert.ToTimeSpan(raw);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
