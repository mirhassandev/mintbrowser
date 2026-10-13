using System;
using System.Collections.Generic;
using System.Linq;
using CustomBrowser.Models;

namespace CustomBrowser.Services.Ytdl
{
    /// <summary>Turns the format list into the quality choices of the picker and into a yt-dlp format selector.</summary>
    public static class YtdlPlanner
    {
        public static List<YtdlQualityOption> BuildQualities(YtdlInfo info, bool canMerge)
        {
            var list = new List<YtdlQualityOption>();
            List<int> heights = info.Formats
                .Where(f => f.HasVideo && f.Height > 0 && (canMerge || f.HasAudio))
                .Select(f => f.Height)
                .Distinct()
                .OrderByDescending(h => h)
                .ToList();

            foreach (int h in heights)
            {
                double fps = info.Formats.Where(f => f.HasVideo && f.Height == h).Select(f => f.Fps).DefaultIfEmpty(0).Max();
                long size = EstimateVideo(info, h, canMerge);
                string label = h + "p";
                if (fps > 30.5) label += "  " + Math.Round(fps) + " fps";
                if (size > 0) label += "   (about " + DownloadItem.FormatBytes(size) + ")";
                list.Add(new YtdlQualityOption { Height = h, EstimatedBytes = size, Label = label });
            }

            if (list.Count == 0)
            {
                long size = EstimateVideo(info, 0, canMerge);
                string label = "Best available";
                if (size > 0) label += "   (about " + DownloadItem.FormatBytes(size) + ")";
                list.Add(new YtdlQualityOption { Height = 0, EstimatedBytes = size, Label = label });
            }
            return list;
        }

        /// <summary>The -f argument. Prefers H.264 + AAC (plays everywhere, ends up as MP4) and falls back to whatever exists.</summary>
        public static string BuildSelector(YtdlMode mode, int height, bool canMerge)
        {
            if (mode != YtdlMode.VideoAudio) return "ba/b";

            string h = height > 0 ? "[height<=" + height + "]" : string.Empty;
            if (!canMerge)
            {
                // Without FFmpeg only formats that already contain video AND sound can be used.
                return "b" + h + "/b";
            }
            return "bv*" + h + "[vcodec^=avc1]+ba[ext=m4a]/bv*" + h + "+ba/b" + h + "/b";
        }

        public static long EstimateVideo(YtdlInfo info, int height, bool canMerge)
        {
            IEnumerable<YtdlFormatInfo> videos = info.Formats.Where(f => f.HasVideo && (height <= 0 || (f.Height > 0 && f.Height <= height)));
            if (!canMerge) videos = videos.Where(f => f.HasAudio);
            YtdlFormatInfo? best = videos.OrderByDescending(f => f.Height).ThenByDescending(f => f.Tbr).FirstOrDefault();
            if (best == null) return 0;

            long size = SizeOf(info, best);
            if (canMerge && !best.HasAudio)
            {
                YtdlFormatInfo? audio = BestAudio(info);
                if (audio != null) size += SizeOf(info, audio);
            }
            return size;
        }

        public static long EstimateAudio(YtdlInfo info)
        {
            YtdlFormatInfo? audio = BestAudio(info);
            return audio == null ? 0 : SizeOf(info, audio);
        }

        private static YtdlFormatInfo? BestAudio(YtdlInfo info)
        {
            return info.Formats
                .Where(f => f.HasAudio && !f.HasVideo)
                .OrderByDescending(f => f.Tbr)
                .ThenByDescending(f => f.Bytes)
                .FirstOrDefault();
        }

        private static long SizeOf(YtdlInfo info, YtdlFormatInfo f)
        {
            if (f.Bytes > 0) return f.Bytes;
            if (info.DurationSeconds > 0 && f.Tbr > 0) return (long)(f.Tbr * 1000.0 / 8.0 * info.DurationSeconds);
            return 0;
        }
    }
}

