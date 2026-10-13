using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CustomBrowser.Services.Streams
{
    /// <summary>Reads DASH (.mpd) manifests: every video quality and every audio track becomes a TrackSpec with its full segment list.</summary>
    public static class DashParser
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Regex DurationRegex = new Regex(
            @"^P(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:([\d.]+)S)?)?$", RegexOptions.Compiled);
        private static readonly Regex TemplateVar = new Regex(
            @"\$(RepresentationID|Number|Bandwidth|Time)(?:%0(\d+)d)?\$", RegexOptions.Compiled);
        private const int MaxSegments = 100000;

        private static string A(XElement? e, string name)
        {
            if (e == null) return string.Empty;
            XAttribute? attr = e.Attribute(name);
            return attr == null ? string.Empty : attr.Value.Trim();
        }

        private static IEnumerable<XElement> Els(XElement? e, string name)
        {
            if (e == null) return Enumerable.Empty<XElement>();
            return e.Elements().Where(x => x.Name.LocalName == name);
        }

        private static XElement? First(XElement? e, string name)
        {
            return Els(e, name).FirstOrDefault();
        }

        private static string Pick(string a, string b)
        {
            return a.Length > 0 ? a : b;
        }

        private static string BaseUrlText(XElement? e)
        {
            XElement? b = First(e, "BaseURL");
            return b == null ? string.Empty : b.Value.Trim();
        }

        private static string ResolveBase(string parent, string relative)
        {
            return relative.Length == 0 ? parent : HlsParser.Resolve(parent, relative);
        }

        private static long LongOr(string s, long fallback)
        {
            return long.TryParse(s, NumberStyles.Integer, Inv, out long v) ? v : fallback;
        }

        private static double DoubleOr(string s, double fallback)
        {
            return double.TryParse(s, NumberStyles.Float, Inv, out double v) ? v : fallback;
        }

        public static double ParseDuration(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            Match m = DurationRegex.Match(s.Trim());
            if (!m.Success) return 0;
            return Part(m, 3) * 86400 + Part(m, 4) * 3600 + Part(m, 5) * 60 + Part(m, 6);
        }

        private static double Part(Match m, int index)
        {
            if (!m.Groups[index].Success) return 0;
            return double.TryParse(m.Groups[index].Value, NumberStyles.Float, Inv, out double v) ? v : 0;
        }

        private static double ParseFrameRate(string s)
        {
            if (s.Length == 0) return 0;
            int slash = s.IndexOf('/');
            if (slash > 0)
            {
                double n = DoubleOr(s.Substring(0, slash), 0);
                double d = DoubleOr(s.Substring(slash + 1), 0);
                return d > 0 ? n / d : 0;
            }
            return DoubleOr(s, 0);
        }

        private static string Subst(string template, string repId, long bandwidth, long number, long time)
        {
            string s = template.Replace("$$", "\u0001");
            s = TemplateVar.Replace(s, m =>
            {
                string format = m.Groups[2].Success ? "D" + m.Groups[2].Value : "D1";
                switch (m.Groups[1].Value)
                {
                    case "RepresentationID": return repId;
                    case "Number": return number.ToString(format, Inv);
                    case "Bandwidth": return bandwidth.ToString(format, Inv);
                    default: return time.ToString(format, Inv);
                }
            });
            return s.Replace("\u0001", "$");
        }

        public static StreamManifest Parse(string text, string mpdUrl)
        {
            var manifest = new StreamManifest { Protocol = StreamProtocol.Dash, SourceUrl = mpdUrl, IsMaster = true };
            XDocument doc;
            try
            {
                var xmlSettings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
                using (var xmlReader = System.Xml.XmlReader.Create(new System.IO.StringReader(text), xmlSettings))
                {
                    doc = XDocument.Load(xmlReader);
                }
            }
            catch (Exception)
            {
                manifest.Error = "The DASH manifest could not be read";
                return manifest;
            }

            XElement? mpd = doc.Root;
            if (mpd == null || mpd.Name.LocalName != "MPD")
            {
                manifest.Error = "This is not a DASH manifest";
                return manifest;
            }
            if (A(mpd, "type").Equals("dynamic", StringComparison.OrdinalIgnoreCase))
            {
                manifest.IsLive = true;
                manifest.Error = "This is a live stream - live recording is not supported";
                return manifest;
            }

            double mpdDuration = ParseDuration(A(mpd, "mediaPresentationDuration"));
            string mpdBase = ResolveBase(mpdUrl, BaseUrlText(mpd));
            List<XElement> periods = Els(mpd, "Period").ToList();
            if (periods.Count == 0)
            {
                manifest.Error = "The manifest has no content";
                return manifest;
            }

            // Several periods usually means ads between the parts: offer the longest one.
            XElement? best = null;
            double bestDuration = -1;
            foreach (XElement p in periods)
            {
                double d = ParseDuration(A(p, "duration"));
                if (d <= 0 && periods.Count == 1) d = mpdDuration;
                if (d > bestDuration) { best = p; bestDuration = d; }
            }
            if (best == null) best = periods[0];
            if (bestDuration < 0) bestDuration = mpdDuration;
            if (periods.Count > 1) manifest.Notes.Add("The manifest has several periods (often ads). Only the longest one is offered.");
            manifest.DurationSeconds = bestDuration;

            string periodBase = ResolveBase(mpdBase, BaseUrlText(best));
            int index = 0;

            foreach (XElement aset in Els(best, "AdaptationSet"))
            {
                string setBase = ResolveBase(periodBase, BaseUrlText(aset));
                string setMime = A(aset, "mimeType");
                string setType = A(aset, "contentType");
                string setCodecs = A(aset, "codecs");
                string language = A(aset, "lang");
                bool setProtected = Els(aset, "ContentProtection").Any();

                foreach (XElement rep in Els(aset, "Representation"))
                {
                    string mime = Pick(A(rep, "mimeType"), setMime);
                    string codecs = Pick(A(rep, "codecs"), setCodecs);
                    string type = Pick(A(rep, "contentType"), setType).ToLowerInvariant();
                    if (type.Length == 0)
                    {
                        string m = mime.ToLowerInvariant();
                        if (m.StartsWith("video/", StringComparison.Ordinal)) type = "video";
                        else if (m.StartsWith("audio/", StringComparison.Ordinal)) type = "audio";
                        else if (m.StartsWith("text/", StringComparison.Ordinal) || m.StartsWith("application/", StringComparison.Ordinal)) type = "text";
                    }
                    if (type == "text" || type == "image") continue;

                    bool hasVideo = StreamLabels.HasVideoCodec(codecs);
                    bool hasAudio = StreamLabels.HasAudioCodec(codecs);
                    TrackKind kind;
                    if (type == "audio") kind = TrackKind.Audio;
                    else if (type == "video") kind = hasAudio && hasVideo ? TrackKind.Muxed : TrackKind.Video;
                    else if (hasVideo && hasAudio) kind = TrackKind.Muxed;
                    else if (hasVideo) kind = TrackKind.Video;
                    else if (hasAudio) kind = TrackKind.Audio;
                    else continue;

                    string repId = A(rep, "id");
                    long bandwidth = LongOr(A(rep, "bandwidth"), 0);
                    int width = (int)LongOr(Pick(A(rep, "width"), A(aset, "width")), 0);
                    int height = (int)LongOr(Pick(A(rep, "height"), A(aset, "height")), 0);
                    double fps = ParseFrameRate(Pick(A(rep, "frameRate"), A(aset, "frameRate")));
                    string repBase = ResolveBase(setBase, BaseUrlText(rep));

                    var t = new TrackSpec
                    {
                        Id = "d" + index.ToString(Inv),
                        Kind = kind,
                        Width = width,
                        Height = height,
                        FrameRate = fps,
                        Bandwidth = bandwidth,
                        Codecs = codecs,
                        Language = language,
                        IsDefault = false
                    };
                    index++;
                    t.Label = kind == TrackKind.Audio
                        ? StreamLabels.Audio(string.Empty, language, bandwidth, codecs)
                        : StreamLabels.Quality(height, fps, bandwidth);
                    string mimeLower = mime.ToLowerInvariant();
                    t.RawExt = mimeLower.Contains("webm") ? ".webm" : (kind == TrackKind.Audio ? ".m4a" : ".mp4");

                    if (setProtected || Els(rep, "ContentProtection").Any())
                    {
                        manifest.Protected = true;
                        t.Error = "This stream is DRM-protected and cannot be downloaded";
                    }
                    else
                    {
                        BuildSegments(t, rep, aset, repBase, mpdUrl, bestDuration, repId, bandwidth);
                    }
                    manifest.Tracks.Add(t);
                }
            }

            if (manifest.Tracks.Count == 0) manifest.Error = "The manifest does not contain any video or audio";
            return manifest;
        }

        private static void BuildSegments(TrackSpec t, XElement rep, XElement aset, string repBase, string mpdUrl,
            double periodDuration, string repId, long bandwidth)
        {
            XElement? repTemplate = First(rep, "SegmentTemplate");
            XElement? setTemplate = First(aset, "SegmentTemplate");

            if (repTemplate != null || setTemplate != null)
            {
                string init = Pick(A(repTemplate, "initialization"), A(setTemplate, "initialization"));
                string media = Pick(A(repTemplate, "media"), A(setTemplate, "media"));
                long startNumber = LongOr(Pick(A(repTemplate, "startNumber"), A(setTemplate, "startNumber")), 1);
                double timescale = DoubleOr(Pick(A(repTemplate, "timescale"), A(setTemplate, "timescale")), 1);
                double durationAttr = DoubleOr(Pick(A(repTemplate, "duration"), A(setTemplate, "duration")), 0);
                XElement? timeline = First(repTemplate, "SegmentTimeline") ?? First(setTemplate, "SegmentTimeline");
                if (timescale <= 0) timescale = 1;

                if (media.Length == 0)
                {
                    t.Error = "The manifest does not give the media address";
                    return;
                }
                if (init.Length > 0) t.InitUrl = HlsParser.Resolve(repBase, Subst(init, repId, bandwidth, 0, 0));

                double total = 0;
                if (timeline != null)
                {
                    long time = 0;
                    long number = startNumber;
                    foreach (XElement s in Els(timeline, "S"))
                    {
                        string tAttr = A(s, "t");
                        if (tAttr.Length > 0) time = LongOr(tAttr, time);
                        long d = LongOr(A(s, "d"), 0);
                        if (d <= 0) continue;
                        long r = LongOr(A(s, "r"), 0);
                        if (r < 0)
                        {
                            double periodUnits = periodDuration * timescale;
                            r = periodUnits > time ? (long)Math.Ceiling((periodUnits - time) / d) - 1 : 0;
                            if (r < 0) r = 0;
                        }
                        for (long k = 0; k <= r; k++)
                        {
                            if (t.Segments.Count >= MaxSegments) break;
                            double seconds = d / timescale;
                            t.Segments.Add(new SegmentSpec
                            {
                                Url = HlsParser.Resolve(repBase, Subst(media, repId, bandwidth, number, time)),
                                Duration = seconds,
                                Sequence = number
                            });
                            total += seconds;
                            time += d;
                            number++;
                        }
                    }
                }
                else if (durationAttr > 0)
                {
                    if (periodDuration <= 0)
                    {
                        t.Error = "The manifest does not say how long the video is";
                        return;
                    }
                    double segSeconds = durationAttr / timescale;
                    long count = (long)Math.Ceiling(periodDuration / segSeconds);
                    if (count > MaxSegments) count = MaxSegments;
                    for (long i = 0; i < count; i++)
                    {
                        long number = startNumber + i;
                        long time = (long)(i * durationAttr);
                        t.Segments.Add(new SegmentSpec
                        {
                            Url = HlsParser.Resolve(repBase, Subst(media, repId, bandwidth, number, time)),
                            Duration = segSeconds,
                            Sequence = number
                        });
                        total += segSeconds;
                    }
                }
                else
                {
                    t.Error = "The manifest does not describe its segments";
                    return;
                }
                t.DurationSeconds = total > 0 ? total : periodDuration;
                if (t.Segments.Count == 0) t.Error = "The manifest has no media segments";
                return;
            }

            XElement? list = First(rep, "SegmentList") ?? First(aset, "SegmentList");
            if (list != null)
            {
                XElement? initEl = First(list, "Initialization");
                if (initEl != null && A(initEl, "sourceURL").Length > 0) t.InitUrl = HlsParser.Resolve(repBase, A(initEl, "sourceURL"));
                double listScale = DoubleOr(A(list, "timescale"), 1);
                if (listScale <= 0) listScale = 1;
                double listDuration = DoubleOr(A(list, "duration"), 0) / listScale;
                double sum = 0;
                foreach (XElement su in Els(list, "SegmentURL"))
                {
                    string mediaUrl = A(su, "media");
                    var seg = new SegmentSpec
                    {
                        Url = mediaUrl.Length > 0 ? HlsParser.Resolve(repBase, mediaUrl) : repBase,
                        Duration = listDuration
                    };
                    string range = A(su, "mediaRange");
                    int dash = range.IndexOf('-');
                    if (dash > 0)
                    {
                        long rangeFrom = LongOr(range.Substring(0, dash), -1);
                        long rangeTo = LongOr(range.Substring(dash + 1), -1);
                        if (rangeFrom >= 0 && rangeTo >= rangeFrom)
                        {
                            seg.RangeStart = rangeFrom;
                            seg.RangeLength = rangeTo - rangeFrom + 1;
                        }
                    }
                    t.Segments.Add(seg);
                    sum += listDuration;
                    if (t.Segments.Count >= MaxSegments) break;
                }
                t.DurationSeconds = sum > 0 ? sum : periodDuration;
                if (t.Segments.Count == 0) t.Error = "The manifest has no media segments";
                return;
            }

            // Only a BaseURL (on-demand profile): the whole file is one download.
            if (string.Equals(repBase, mpdUrl, StringComparison.Ordinal))
            {
                t.Error = "The manifest does not give the media address";
                return;
            }
            t.SingleFile = true;
            t.DurationSeconds = periodDuration;
            t.Segments.Add(new SegmentSpec { Url = repBase, Duration = periodDuration });
        }
    }
}

