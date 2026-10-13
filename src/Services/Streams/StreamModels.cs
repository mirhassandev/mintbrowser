using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace CustomBrowser.Services.Streams
{
    public enum StreamProtocol { Hls, Dash, Progressive }

    /// <summary>Muxed = picture and sound in one stream, Video = picture only, Audio = sound only.</summary>
    public enum TrackKind { Muxed, Video, Audio }

    /// <summary>One media request that was seen on a page (playlist, manifest or plain media file).</summary>
    public sealed class DetectedStream
    {
        public string Url { get; set; } = string.Empty;
        public StreamProtocol Protocol { get; set; }
        public string ContentType { get; set; } = string.Empty;
        public long ContentLength { get; set; } = -1;
        /// <summary>HLS: the playlist that lists the qualities (child playlists are hidden once a master is known).</summary>
        public bool IsMaster { get; set; }
        public string Referer { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        public string PageUrl { get; set; } = string.Empty;
        public DateTime DetectedAt { get; set; } = DateTime.Now;
        /// <summary>Used (on the UI thread) to read the cookies for this request.</summary>
        public Microsoft.Web.WebView2.Core.CoreWebView2? Core { get; set; }
    }

    public sealed class SegmentSpec
    {
        public string Url { get; set; } = string.Empty;
        public long RangeStart { get; set; } = -1;
        public long RangeLength { get; set; } = -1;
        public double Duration { get; set; }
        /// <summary>AES-128 key address; empty = not encrypted.</summary>
        public string KeyUrl { get; set; } = string.Empty;
        public string Iv { get; set; } = string.Empty;
        public long Sequence { get; set; }
    }

    /// <summary>One downloadable rendition (a quality, or an audio track).</summary>
    public sealed class TrackSpec
    {
        public string Id { get; set; } = string.Empty;
        public TrackKind Kind { get; set; }
        public string Label { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public double FrameRate { get; set; }
        public long Bandwidth { get; set; }
        public string Codecs { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
        public string PlaylistUrl { get; set; } = string.Empty;
        /// <summary>Extension of the segments glued together (".ts", ".mp4", ".m4a", ".aac", ".mp3", ".webm").</summary>
        public string RawExt { get; set; } = ".ts";
        public double DurationSeconds { get; set; }
        public string InitUrl { get; set; } = string.Empty;
        public long InitRangeStart { get; set; } = -1;
        public long InitRangeLength { get; set; } = -1;
        public bool NeedsSeparateAudio { get; set; }
        public string AudioGroup { get; set; } = string.Empty;
        public bool SingleFile { get; set; }
        public long KnownBytes { get; set; } = -1;
        public string Error { get; set; } = string.Empty;
        public List<SegmentSpec> Segments { get; set; } = new List<SegmentSpec>();

        [JsonIgnore]
        public long EstimatedBytes
        {
            get
            {
                if (KnownBytes > 0) return KnownBytes;
                if (Bandwidth > 0 && DurationSeconds > 0) return (long)(Bandwidth / 8.0 * DurationSeconds);
                return 0;
            }
        }
    }

    public sealed class StreamManifest
    {
        public StreamProtocol Protocol { get; set; }
        public string SourceUrl { get; set; } = string.Empty;
        public bool IsMaster { get; set; }
        public bool IsLive { get; set; }
        public bool Protected { get; set; }
        public string Error { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
        public List<TrackSpec> Tracks { get; set; } = new List<TrackSpec>();
        public List<string> Notes { get; set; } = new List<string>();
    }

    /// <summary>Everything the download engine needs. Saved next to the target file so a paused download survives a restart.</summary>
    public sealed class StreamJobSpec
    {
        public string Title { get; set; } = string.Empty;
        public string Referer { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        public string CookieHeader { get; set; } = string.Empty;
        public string CookieHost { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        /// <summary>One track = plain download. Two tracks (video, audio) = merged with FFmpeg.</summary>
        public List<TrackSpec> Tracks { get; set; } = new List<TrackSpec>();
        public int Concurrency { get; set; } = 6;
        /// <summary>Turn a plain TS download into MP4 / M4A when FFmpeg is available.</summary>
        public bool ConvertToMp4 { get; set; } = true;
    }

    /// <summary>What the finished file will look like for a given selection.</summary>
    public sealed class StreamPlan
    {
        public string Ext { get; set; } = ".ts";
        public bool UseFfmpeg { get; set; }
        /// <summary>No FFmpeg: video and audio are kept as two separate files.</summary>
        public bool Separate { get; set; }
        public string Format { get; set; } = string.Empty;
    }

    public sealed class StreamProgress
    {
        public int DoneSegments { get; set; }
        public int TotalSegments { get; set; }
        public long Bytes { get; set; }
        public long EstimatedTotalBytes { get; set; }
        public double SpeedBytesPerSecond { get; set; }
        public string Phase { get; set; } = "Downloading";
        /// <summary>0..1</summary>
        public double Fraction { get; set; }
    }

    public sealed class StreamResult
    {
        public string FinalPath { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
    }

    public sealed class StreamHttpException : Exception
    {
        public int StatusCode { get; }
        public StreamHttpException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
    }

    /// <summary>Texts shown for qualities and codecs.</summary>
    public static class StreamLabels
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Quality(int height, double fps, long bandwidth)
        {
            string s;
            if (height > 0) s = height.ToString(Inv) + "p";
            else if (bandwidth > 0) s = (bandwidth / 1000000.0).ToString("0.0", Inv) + " Mbps";
            else s = "Default";
            if (height > 0 && fps >= 50) s += ((int)Math.Round(fps)).ToString(Inv);
            return s;
        }

        public static string Bitrate(long bandwidth)
        {
            if (bandwidth <= 0) return "-";
            if (bandwidth >= 1000000) return (bandwidth / 1000000.0).ToString("0.0", Inv) + " Mbps";
            return (bandwidth / 1000).ToString(Inv) + " kbps";
        }

        public static string Audio(string name, string language, long bandwidth, string codecs)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(name)) parts.Add(name.Trim());
            else if (!string.IsNullOrWhiteSpace(language)) parts.Add(language.Trim());
            else parts.Add("Audio");
            if (bandwidth > 0) parts.Add(Bitrate(bandwidth));
            string c = CodecSummary(codecs);
            if (c.Length > 0) parts.Add(c);
            return string.Join(" - ", parts);
        }

        public static bool HasVideoCodec(string codecs)
        {
            string c = (codecs ?? string.Empty).ToLowerInvariant();
            return c.Contains("avc") || c.Contains("hvc") || c.Contains("hev") || c.Contains("vp09") || c.Contains("vp9") ||
                   c.Contains("vp8") || c.Contains("av01") || c.Contains("dvh") || c.Contains("mp4v");
        }

        public static bool HasAudioCodec(string codecs)
        {
            string c = (codecs ?? string.Empty).ToLowerInvariant();
            return c.Contains("mp4a") || c.Contains("opus") || c.Contains("ac-3") || c.Contains("ec-3") ||
                   c.Contains("flac") || c.Contains("vorbis") || c.Contains("mp3");
        }

        public static string CodecSummary(string codecs)
        {
            if (string.IsNullOrWhiteSpace(codecs)) return string.Empty;
            var names = new List<string>();
            foreach (string raw in codecs.Split(','))
            {
                string c = raw.Trim().ToLowerInvariant();
                string n = string.Empty;
                if (c.StartsWith("avc", StringComparison.Ordinal)) n = "H.264";
                else if (c.StartsWith("hvc", StringComparison.Ordinal) || c.StartsWith("hev", StringComparison.Ordinal)) n = "H.265";
                else if (c.StartsWith("vp09", StringComparison.Ordinal) || c.StartsWith("vp9", StringComparison.Ordinal)) n = "VP9";
                else if (c.StartsWith("vp8", StringComparison.Ordinal)) n = "VP8";
                else if (c.StartsWith("av01", StringComparison.Ordinal)) n = "AV1";
                else if (c.StartsWith("mp4a", StringComparison.Ordinal)) n = "AAC";
                else if (c.StartsWith("opus", StringComparison.Ordinal)) n = "Opus";
                else if (c.StartsWith("ac-3", StringComparison.Ordinal)) n = "AC-3";
                else if (c.StartsWith("ec-3", StringComparison.Ordinal)) n = "E-AC-3";
                else if (c.StartsWith("flac", StringComparison.Ordinal)) n = "FLAC";
                else if (c.StartsWith("vorbis", StringComparison.Ordinal)) n = "Vorbis";
                else if (c.Length > 0) n = c;
                if (n.Length > 0 && !names.Contains(n)) names.Add(n);
            }
            return string.Join(" + ", names);
        }
    }
}

