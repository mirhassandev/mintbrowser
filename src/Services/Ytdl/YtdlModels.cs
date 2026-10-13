using System;
using System.Collections.Generic;

namespace CustomBrowser.Services.Ytdl
{
    /// <summary>What the user wants out of the page.</summary>
    public enum YtdlMode
    {
        VideoAudio = 0,
        AudioOriginal = 1,
        AudioMp3 = 2
    }

    /// <summary>One entry of the format list that yt-dlp reports for a page (only the fields the picker needs).</summary>
    public sealed class YtdlFormatInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Ext { get; set; } = string.Empty;
        public int Height { get; set; }
        public double Fps { get; set; }
        public bool HasVideo { get; set; }
        public bool HasAudio { get; set; }
        /// <summary>Exact or approximate size in bytes, 0 = unknown.</summary>
        public long Bytes { get; set; }
        /// <summary>Average bitrate in kbit/s, 0 = unknown.</summary>
        public double Tbr { get; set; }
    }

    /// <summary>What yt-dlp found on the page.</summary>
    public sealed class YtdlInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Uploader { get; set; } = string.Empty;
        public string Site { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
        public bool IsLive { get; set; }
        public List<YtdlFormatInfo> Formats { get; } = new List<YtdlFormatInfo>();
    }

    /// <summary>One line of the quality list in the picker.</summary>
    public sealed class YtdlQualityOption
    {
        /// <summary>0 = best available.</summary>
        public int Height { get; set; }
        public string Label { get; set; } = string.Empty;
        public long EstimatedBytes { get; set; }
        public override string ToString() => Label;
    }

    /// <summary>The answer the picker hands back to the main window.</summary>
    public sealed class YtdlChoice
    {
        public YtdlMode Mode { get; set; }
        public string Selector { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string FileBaseName { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public bool UseCookies { get; set; }
        public string? CookieFile { get; set; }
        public long EstimatedBytes { get; set; }
    }

    /// <summary>Everything needed to (re)start a download later, even after the browser was restarted. Saved as JSON.</summary>
    public sealed class YtdlJobSpec
    {
        public string PageUrl { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Selector { get; set; } = string.Empty;
        public bool AudioMp3 { get; set; }
        public string OutputFolder { get; set; } = string.Empty;
        /// <summary>File name without extension (yt-dlp adds the real one).</summary>
        public string BaseName { get; set; } = string.Empty;
        public string CookieFile { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        public long EstimatedBytes { get; set; }
    }

    public sealed class YtdlProgress
    {
        public long Bytes { get; set; }
        public long EstimatedTotalBytes { get; set; }
        public double Fraction { get; set; }
        public double SpeedBytesPerSecond { get; set; }
        public string Phase { get; set; } = "Downloading";
    }

    public sealed class YtdlResult
    {
        public string FinalPath { get; set; } = string.Empty;
    }

    /// <summary>An error whose message is already written for the user.</summary>
    public sealed class YtdlException : Exception
    {
        public YtdlException(string message) : base(message) { }
    }
}

