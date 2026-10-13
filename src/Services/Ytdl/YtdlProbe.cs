using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CustomBrowser.Helpers;

namespace CustomBrowser.Services.Ytdl
{
    /// <summary>Asks yt-dlp what is on a page (title, formats) without downloading anything.</summary>
    public static class YtdlProbe
    {
        public static async Task<YtdlInfo> RunAsync(string exe, string pageUrl, string? cookieFile, CancellationToken ct)
        {
            var args = new List<string> { "-J", "--no-playlist", "--no-colors", "--socket-timeout", "20" };
            args.AddRange(YtdlTool.JsRuntimeArgsFor(pageUrl));
            if (!string.IsNullOrEmpty(cookieFile) && File.Exists(cookieFile))
            {
                args.Add("--cookies");
                args.Add(cookieFile);
            }
            args.Add("--");
            args.Add(pageUrl);

            var result = await YtdlProcess.RunCaptureAsync(exe, args, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            {
                AppLog.Debug("yt-dlp probe failed: " + result.Error);
                throw new YtdlException(YtdlErrors.Explain(result.Error));
            }

            try
            {
                return Parse(result.Output);
            }
            catch (YtdlException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not read the yt-dlp answer", ex);
                throw new YtdlException("yt-dlp answered, but the answer could not be read.");
            }
        }

        private static YtdlInfo Parse(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new YtdlException("yt-dlp answered, but the answer could not be read.");

            if (root.TryGetProperty("entries", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0)
            {
                throw new YtdlException("This page holds several videos. Open the one you want, then try again.");
            }

            var info = new YtdlInfo
            {
                Title = Str(root, "title"),
                Uploader = Str(root, "uploader"),
                Site = Str(root, "extractor_key"),
                DurationSeconds = Num(root, "duration"),
                IsLive = Flag(root, "is_live")
            };
            if (info.Uploader.Length == 0) info.Uploader = Str(root, "channel");
            if (info.Title.Length == 0) info.Title = Str(root, "id");
            if (info.IsLive) throw new YtdlException("This is a live stream. Live streams cannot be downloaded here.");

            if (root.TryGetProperty("formats", out JsonElement formats) && formats.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement f in formats.EnumerateArray()) AddFormat(info, f);
            }
            else
            {
                AddFormat(info, root); // a direct file: the page itself is the only format
            }

            if (info.Formats.Count == 0) throw new YtdlException("No downloadable video or audio was found on this page.");
            return info;
        }

        private static void AddFormat(YtdlInfo info, JsonElement f)
        {
            if (f.ValueKind != JsonValueKind.Object) return;
            string vcodec = Str(f, "vcodec");
            string acodec = Str(f, "acodec");
            bool hasVideo = !string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase);
            bool hasAudio = !string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase);
            if (!hasVideo && !hasAudio) return; // storyboards / thumbnails

            long bytes = (long)Num(f, "filesize");
            if (bytes <= 0) bytes = (long)Num(f, "filesize_approx");
            info.Formats.Add(new YtdlFormatInfo
            {
                Id = Str(f, "format_id"),
                Ext = Str(f, "ext"),
                Height = (int)Num(f, "height"),
                Fps = Num(f, "fps"),
                HasVideo = hasVideo,
                HasAudio = hasAudio,
                Bytes = bytes,
                Tbr = Num(f, "tbr")
            });
        }

        private static string Str(JsonElement e, string name)
        {
            if (e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String) return p.GetString() ?? string.Empty;
            return string.Empty;
        }

        private static double Num(JsonElement e, string name)
        {
            if (e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double d)) return d;
            return 0;
        }

        private static bool Flag(JsonElement e, string name)
        {
            return e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.True;
        }
    }
}

