using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CustomBrowser.Services.Streams
{
    /// <summary>Reads HLS (.m3u8) master and media playlists.</summary>
    public static class HlsParser
    {
        private static readonly Regex AttrRegex = new Regex("([A-Za-z0-9-]+)=(\"[^\"]*\"|[^,]*)", RegexOptions.Compiled);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private const string TagMedia = "#EXT-X-MEDIA:";
        private const string TagStreamInf = "#EXT-X-STREAM-INF:";

        private static readonly System.Buffers.SearchValues<char> ResolutionSeparators = System.Buffers.SearchValues.Create("xX");
        internal static readonly System.Buffers.SearchValues<char> QueryOrFragment = System.Buffers.SearchValues.Create("?#");

        public static bool IsMaster(string text)
        {
            return text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);
        }

        public static string[] SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        public static Dictionary<string, string> ParseAttributes(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in AttrRegex.Matches(s))
            {
                string key = m.Groups[1].Value;
                string val = m.Groups[2].Value.Trim();
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"') val = val.Substring(1, val.Length - 2);
                d[key] = val;
            }
            return d;
        }

        private static string Get(Dictionary<string, string> a, string key)
        {
            return a.TryGetValue(key, out string? v) && v != null ? v : string.Empty;
        }

        public static string Resolve(string baseUrl, string relative)
        {
            try
            {
                return new Uri(new Uri(baseUrl), relative).AbsoluteUri;
            }
            catch (UriFormatException)
            {
                return relative;
            }
        }

        public static long ParseLong(string s)
        {
            return long.TryParse(s, NumberStyles.Integer, Inv, out long v) ? v : -1;
        }

        public static double ParseDouble(string s)
        {
            return double.TryParse(s, NumberStyles.Float, Inv, out double v) ? v : 0;
        }

        public static string ExtensionOf(string url)
        {
            string path = url;
            int q = path.AsSpan().IndexOfAny(QueryOrFragment);
            if (q >= 0) path = path.Substring(0, q);
            int dot = path.LastIndexOf('.');
            int slash = path.LastIndexOf('/');
            if (dot < 0 || dot < slash) return string.Empty;
            return path.Substring(dot).ToLowerInvariant();
        }

        /// <summary>Master playlist: one track per quality plus one per separate audio rendition.</summary>
        public static StreamManifest ParseMaster(string text, string baseUrl)
        {
            var manifest = new StreamManifest { Protocol = StreamProtocol.Hls, SourceUrl = baseUrl, IsMaster = true };
            string[] lines = SplitLines(text);
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var audioGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int audioIndex = 0;

            // Separate audio renditions (#EXT-X-MEDIA with a URI)
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (!line.StartsWith(TagMedia, StringComparison.Ordinal)) continue;
                Dictionary<string, string> a = ParseAttributes(line.Substring(TagMedia.Length));
                if (!Get(a, "TYPE").Equals("AUDIO", StringComparison.OrdinalIgnoreCase)) continue;
                string uri = Get(a, "URI");
                if (uri.Length == 0) continue; // sound is inside the video stream
                string url = Resolve(baseUrl, uri);
                string group = Get(a, "GROUP-ID");
                if (group.Length > 0) audioGroups.Add(group);
                if (!seenUrls.Add(url)) continue;

                var t = new TrackSpec
                {
                    Id = "a" + audioIndex.ToString(Inv),
                    Kind = TrackKind.Audio,
                    PlaylistUrl = url,
                    Name = Get(a, "NAME"),
                    Language = Get(a, "LANGUAGE"),
                    AudioGroup = group,
                    IsDefault = Get(a, "DEFAULT").Equals("YES", StringComparison.OrdinalIgnoreCase)
                };
                t.Label = StreamLabels.Audio(t.Name, t.Language, 0, string.Empty);
                manifest.Tracks.Add(t);
                audioIndex++;
            }

            // Quality variants
            int videoIndex = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith(TagStreamInf, StringComparison.Ordinal)) continue;
                Dictionary<string, string> a = ParseAttributes(line.Substring(TagStreamInf.Length));

                int j = i + 1;
                while (j < lines.Length)
                {
                    string next = lines[j].Trim();
                    if (next.Length == 0 || next[0] == '#') { j++; continue; }
                    break;
                }
                if (j >= lines.Length) break;
                string url = Resolve(baseUrl, lines[j].Trim());
                i = j;
                if (!seenUrls.Add(url)) continue;

                long bandwidth = ParseLong(Get(a, "AVERAGE-BANDWIDTH"));
                if (bandwidth <= 0) bandwidth = ParseLong(Get(a, "BANDWIDTH"));
                if (bandwidth < 0) bandwidth = 0;
                int width = 0, height = 0;
                string res = Get(a, "RESOLUTION");
                int x = res.AsSpan().IndexOfAny(ResolutionSeparators);
                if (x > 0)
                {
                    int.TryParse(res.Substring(0, x), NumberStyles.Integer, Inv, out width);
                    int.TryParse(res.Substring(x + 1), NumberStyles.Integer, Inv, out height);
                }
                double fps = ParseDouble(Get(a, "FRAME-RATE"));
                string codecs = Get(a, "CODECS");
                string group = Get(a, "AUDIO");

                var t = new TrackSpec
                {
                    PlaylistUrl = url,
                    Width = width,
                    Height = height,
                    FrameRate = fps,
                    Bandwidth = bandwidth,
                    Codecs = codecs
                };

                bool audioOnly = height == 0 && codecs.Length > 0 && !StreamLabels.HasVideoCodec(codecs);
                if (audioOnly)
                {
                    t.Id = "a" + audioIndex.ToString(Inv);
                    audioIndex++;
                    t.Kind = TrackKind.Audio;
                    t.Label = StreamLabels.Audio(string.Empty, string.Empty, bandwidth, codecs);
                }
                else
                {
                    t.Id = "v" + videoIndex.ToString(Inv);
                    videoIndex++;
                    bool separateAudio = group.Length > 0 && audioGroups.Contains(group);
                    t.Kind = separateAudio ? TrackKind.Video : TrackKind.Muxed;
                    t.NeedsSeparateAudio = separateAudio;
                    t.AudioGroup = group;
                    t.Label = StreamLabels.Quality(height, fps, bandwidth);
                }
                manifest.Tracks.Add(t);
            }

            if (manifest.Tracks.Count == 0) manifest.Error = "The playlist does not list any streams";
            return manifest;
        }

        /// <summary>Media playlist: fills the segment list and the technical details of the track.</summary>
        public static void ParseMedia(string text, string baseUrl, TrackSpec track)
        {
            string[] lines = SplitLines(text);
            var segments = new List<SegmentSpec>();
            var lastEnd = new Dictionary<string, long>(StringComparer.Ordinal);
            long sequence = 0;
            double pendingDuration = -1;
            string keyUrl = string.Empty;
            string keyIv = string.Empty;
            long pendingRangeStart = -1;
            long pendingRangeLength = -1;
            bool endList = false;
            bool isProtected = false;
            double total = 0;
            string mapUrl = string.Empty;
            long mapStart = -1;
            long mapLength = -1;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line[0] == '#')
                {
                    if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                    {
                        long s = ParseLong(line.Substring("#EXT-X-MEDIA-SEQUENCE:".Length).Trim());
                        sequence = s < 0 ? 0 : s;
                    }
                    else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                    {
                        string rest = line.Substring("#EXTINF:".Length);
                        int comma = rest.IndexOf(',');
                        if (comma >= 0) rest = rest.Substring(0, comma);
                        pendingDuration = ParseDouble(rest.Trim());
                    }
                    else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                    {
                        Dictionary<string, string> a = ParseAttributes(line.Substring("#EXT-X-KEY:".Length));
                        string method = Get(a, "METHOD").ToUpperInvariant();
                        string format = Get(a, "KEYFORMAT");
                        if (method == "NONE" || method.Length == 0)
                        {
                            keyUrl = string.Empty;
                            keyIv = string.Empty;
                        }
                        else if (method == "AES-128" && (format.Length == 0 || format.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                        {
                            keyUrl = Resolve(baseUrl, Get(a, "URI"));
                            keyIv = Get(a, "IV");
                        }
                        else
                        {
                            isProtected = true; // SAMPLE-AES, Widevine, FairPlay, PlayReady ...
                        }
                    }
                    else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                    {
                        Dictionary<string, string> a = ParseAttributes(line.Substring("#EXT-X-MAP:".Length));
                        if (mapUrl.Length == 0 && Get(a, "URI").Length > 0)
                        {
                            mapUrl = Resolve(baseUrl, Get(a, "URI"));
                            string br = Get(a, "BYTERANGE");
                            if (br.Length > 0) ParseByteRange(br, -1, out mapStart, out mapLength);
                        }
                    }
                    else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                    {
                        ParseByteRange(line.Substring("#EXT-X-BYTERANGE:".Length).Trim(), -1, out pendingRangeStart, out pendingRangeLength);
                    }
                    else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
                    {
                        endList = true;
                    }
                    continue;
                }

                string segUrl = Resolve(baseUrl, line);
                var seg = new SegmentSpec
                {
                    Url = segUrl,
                    Duration = pendingDuration < 0 ? 0 : pendingDuration,
                    KeyUrl = keyUrl,
                    Iv = keyIv,
                    Sequence = sequence
                };
                if (pendingRangeLength > 0)
                {
                    long start = pendingRangeStart;
                    if (start < 0) start = lastEnd.TryGetValue(segUrl, out long end) ? end : 0;
                    seg.RangeStart = start;
                    seg.RangeLength = pendingRangeLength;
                    lastEnd[segUrl] = start + pendingRangeLength;
                }
                segments.Add(seg);
                total += seg.Duration;
                sequence++;
                pendingDuration = -1;
                pendingRangeStart = -1;
                pendingRangeLength = -1;
            }

            track.Segments = segments;
            track.DurationSeconds = total;
            track.InitUrl = mapUrl;
            track.InitRangeStart = mapStart;
            track.InitRangeLength = mapLength;

            string ext = segments.Count > 0 ? ExtensionOf(segments[0].Url) : string.Empty;
            if (mapUrl.Length > 0)
            {
                track.RawExt = track.Kind == TrackKind.Audio ? ".m4a" : ".mp4";
            }
            else
            {
                switch (ext)
                {
                    case ".aac": track.RawExt = ".aac"; break;
                    case ".mp3": track.RawExt = ".mp3"; break;
                    case ".m4a":
                    case ".cmfa": track.RawExt = ".m4a"; break;
                    case ".m4s":
                    case ".mp4":
                    case ".m4v":
                    case ".cmfv": track.RawExt = ".mp4"; break;
                    case ".webm": track.RawExt = ".webm"; break;
                    default: track.RawExt = ".ts"; break;
                }
            }

            if (isProtected) track.Error = "This stream is DRM-protected and cannot be downloaded";
            else if (!endList) track.Error = "This is a live stream (it has no end) - live recording is not supported";
            else if (segments.Count == 0) track.Error = "The playlist has no media segments";
        }

        private static void ParseByteRange(string value, long defaultStart, out long start, out long length)
        {
            start = defaultStart;
            length = -1;
            string[] parts = value.Split('@');
            long len = ParseLong(parts[0].Trim());
            if (len > 0) length = len;
            if (parts.Length > 1)
            {
                long off = ParseLong(parts[1].Trim());
                if (off >= 0) start = off;
            }
        }
    }
}

