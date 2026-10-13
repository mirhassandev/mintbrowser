using System;
using System.Linq;

namespace CustomBrowser.Services.Ytdl
{
    /// <summary>Turns yt-dlp's error output into one short sentence the user can act on.</summary>
    public static class YtdlErrors
    {
        public static string Explain(string stderr)
        {
            string text = stderr ?? string.Empty;
            string low = text.ToLowerInvariant();

            if (low.Contains("not a bot")) return "The site wants a sign-in check. Tick \"Use my browser login\" (while signed in on this site) and try again.";
            if (low.Contains("confirm your age") || low.Contains("age-restricted") || low.Contains("age restricted")) return "Age-restricted video. Tick \"Use my browser login\" (while signed in on this site) and try again.";
            if (low.Contains("private video")) return "This video is private.";
            if (low.Contains("login required") || low.Contains("requires login") || low.Contains("log in") || low.Contains("sign in")) return "This needs a login. Sign in on the site, tick \"Use my browser login\" and try again.";
            if (low.Contains("not available in your country") || low.Contains("geo restrict") || low.Contains("geo-restrict")) return "This video is not available in your country.";
            if (low.Contains("video unavailable") || low.Contains("has been removed") || low.Contains("no longer available")) return "This video is unavailable or was removed.";
            if (low.Contains("drm")) return "This video is DRM-protected and cannot be downloaded.";
            if (low.Contains("ffmpeg") && (low.Contains("not found") || low.Contains("not installed") || low.Contains("is not available"))) return "FFmpeg is needed for this. Install it with:  winget install Gyan.FFmpeg  then reopen the browser.";
            if (low.Contains("ffprobe") && low.Contains("not found")) return "FFmpeg (ffprobe) is needed for this. Install it with:  winget install Gyan.FFmpeg";
            if (low.Contains("http error 429") || low.Contains("too many requests")) return "Too many requests (429). Wait a few minutes and try again.";
            if (low.Contains("http error 403")) return "Access denied (403). Try again, or update yt-dlp.";
            if (low.Contains("unsupported url")) return "yt-dlp does not support this page.";
            if (low.Contains("no such option")) return "This copy of yt-dlp is too old for the browser. Click \"Update yt-dlp\" and try again.";
            if (low.Contains("javascript runtime") || low.Contains("n challenge") || low.Contains("js runtime"))
                return "YouTube needs a JavaScript runtime to be read. Install Deno with:  winget install DenoLand.Deno  then reopen the browser.";
            if (low.Contains("unable to extract") || low.Contains("nsig") || low.Contains("signature extraction") || low.Contains("please report this issue"))
                return "yt-dlp could not read this site. Update yt-dlp (\"Update yt-dlp\" in the picker) and try again.";
            if (low.Contains("no space left") || low.Contains("disk full")) return "The disk is full.";
            if (low.Contains("requested format is not available")) return "That quality is not available for this video. Pick another quality.";

            string? errorLine = text.Split('\n')
                .Select(l => l.Trim())
                .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
            if (errorLine != null)
            {
                string msg = errorLine.Substring(6).Trim();
                int bracket = msg.IndexOf("] ", StringComparison.Ordinal);
                if (msg.StartsWith('[') && bracket > 0) msg = msg.Substring(bracket + 2).Trim();
                return msg.Length > 140 ? msg.Substring(0, 140) + "..." : msg;
            }

            string last = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "yt-dlp stopped without a message.";
            return last.Length > 140 ? last.Substring(0, 140) + "..." : last;
        }
    }
}

