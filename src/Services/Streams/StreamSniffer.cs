using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using CustomBrowser.Helpers;
using CustomBrowser.Models;
using Microsoft.Web.WebView2.Core;

namespace CustomBrowser.Services.Streams
{
    /// <summary>
    /// Watches the network traffic of every tab (like Internet Download Manager does) and remembers the playlists,
    /// manifests and media files the page asked for. Nothing is downloaded here.
    /// </summary>
    public static class StreamSniffer
    {
        private sealed class Bucket
        {
            public readonly List<DetectedStream> Items = new List<DetectedStream>();
        }

        private static readonly ConditionalWeakTable<BrowserTab, Bucket> Buckets = new ConditionalWeakTable<BrowserTab, Bucket>();
        private const int MaxPerTab = 30;
        private const long MinProgressiveBytes = 512 * 1024;

        private static readonly HashSet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".mkv", ".mov", ".m4v", ".mp3", ".m4a", ".ogg", ".opus", ".flac", ".wav", ".avi", ".flv"
        };

        private static readonly HashSet<string> SegmentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ts", ".m4s", ".m4f", ".cmfv", ".cmfa", ".fmp4", ".aac"
        };

        /// <summary>Starts watching the network traffic of one tab. Call once per tab, after its WebView2 core exists.</summary>
        public static void Attach(BrowserTab tab, CoreWebView2 core)
        {
            core.WebResourceResponseReceived += async (s, e) =>
            {
                try
                {
                    await OnResponseAsync(tab, core, e);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("The stream sniffer ignored a response", ex);
                }
            };
            core.NavigationStarting += (s, e) =>
            {
                try
                {
                    if (!e.IsRedirected) Clear(tab);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("The stream sniffer could not reset a tab", ex);
                }
            };
        }

        public static int Count(BrowserTab tab)
        {
            if (!Buckets.TryGetValue(tab, out Bucket? b)) return 0;
            lock (b) return b.Items.Count;
        }

        /// <summary>Newest first.</summary>
        public static List<DetectedStream> GetStreams(BrowserTab tab)
        {
            if (!Buckets.TryGetValue(tab, out Bucket? b)) return new List<DetectedStream>();
            lock (b) return b.Items.OrderByDescending(x => x.DetectedAt).ToList();
        }

        public static void Clear(BrowserTab tab)
        {
            if (!Buckets.TryGetValue(tab, out Bucket? b)) return;
            lock (b) b.Items.Clear();
        }

        private static string Header(CoreWebView2HttpResponseHeaders headers, string name)
        {
            try
            {
                return headers.Contains(name) ? (headers.GetHeader(name) ?? string.Empty) : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string RequestHeader(CoreWebView2HttpRequestHeaders headers, string name)
        {
            try
            {
                return headers.Contains(name) ? (headers.GetHeader(name) ?? string.Empty) : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>URLs that ask for one slice of a file (range=0-1048575, bytestart=...) are pieces of a player's buffering, not a file to save.</summary>
        private static bool IsChunkUrl(string url)
        {
            int q = url.IndexOf('?');
            if (q < 0) return false;
            string query = "&" + url.Substring(q + 1).ToLowerInvariant();
            return query.Contains("&range=") || query.Contains("&bytestart=") || query.Contains("&byteend=") || query.Contains("&rn=");
        }

        private static string StripQuery(string url)
        {
            int q = url.AsSpan().IndexOfAny(HlsParser.QueryOrFragment);
            return q >= 0 ? url.Substring(0, q) : url;
        }

        private static long TotalLength(CoreWebView2HttpResponseHeaders headers)
        {
            // 206 answers carry the real size after the slash: "bytes 0-1023/5242880"
            string range = Header(headers, "Content-Range");
            int slash = range.LastIndexOf('/');
            if (slash >= 0 && long.TryParse(range.Substring(slash + 1).Trim(), out long total)) return total;
            string length = Header(headers, "Content-Length");
            return long.TryParse(length, out long len) ? len : -1;
        }

        private static async System.Threading.Tasks.Task OnResponseAsync(BrowserTab tab, CoreWebView2 core, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            CoreWebView2WebResourceRequest request = e.Request;
            CoreWebView2WebResourceResponseView response = e.Response;

            string url = request.Uri ?? string.Empty;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)) return;
            int status = response.StatusCode;
            if (status != 200 && status != 206) return;

            string contentType = Header(response.Headers, "Content-Type");
            string lowerType = contentType.ToLowerInvariant();
            string path = StripQuery(url).ToLowerInvariant();
            string ext = HlsParser.ExtensionOf(path);

            StreamProtocol protocol;
            long length = -1;
            bool isMaster = false;
            System.Threading.Tasks.Task<bool>? masterCheck = null;

            if (path.EndsWith(".m3u8", StringComparison.Ordinal) || lowerType.Contains("mpegurl"))
            {
                protocol = StreamProtocol.Hls;
                masterCheck = LooksLikeMasterAsync(response); // the read starts now, while the event is still being handled
            }
            else if (path.EndsWith(".mpd", StringComparison.Ordinal) || lowerType.Contains("dash+xml"))
            {
                protocol = StreamProtocol.Dash;
                isMaster = true;
            }
            else
            {
                bool mediaType = lowerType.StartsWith("video/", StringComparison.Ordinal) ||
                                 lowerType.StartsWith("audio/", StringComparison.Ordinal) ||
                                 lowerType.Contains("application/ogg");
                bool genericType = lowerType.Length == 0 || lowerType.Contains("octet-stream");
                if (!(mediaType || (genericType && MediaExtensions.Contains(ext)))) return;
                if (lowerType.Contains("mp2t")) return;                // HLS pieces
                if (SegmentExtensions.Contains(ext)) return;            // HLS / DASH pieces
                if (IsChunkUrl(url)) return;                            // range-sliced pieces (YouTube, Facebook ...): yt-dlp handles those sites
                length = TotalLength(response.Headers);
                if (length < MinProgressiveBytes) return;               // small or of unknown size: not worth listing
                protocol = StreamProtocol.Progressive;
            }

            string pageUrl = string.Empty;
            string userAgent = string.Empty;
            try
            {
                pageUrl = core.Source ?? string.Empty;
                userAgent = core.Settings.UserAgent ?? string.Empty;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not read the page address for a detected stream", ex);
            }

            string referer = RequestHeader(request.Headers, "Referer");
            if (referer.Length == 0) referer = pageUrl;
            string origin = RequestHeader(request.Headers, "Origin");
            if (origin.Length == 0 && Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? pageUri) &&
                (pageUri.Scheme == Uri.UriSchemeHttp || pageUri.Scheme == Uri.UriSchemeHttps))
            {
                origin = pageUri.GetLeftPart(UriPartial.Authority);
            }

            if (masterCheck != null) isMaster = await masterCheck;

            var detected = new DetectedStream
            {
                Url = url,
                Protocol = protocol,
                ContentType = contentType,
                ContentLength = length,
                IsMaster = isMaster,
                Referer = referer,
                Origin = origin,
                UserAgent = userAgent,
                PageUrl = pageUrl,
                DetectedAt = DateTime.Now,
                Core = core
            };

            Bucket bucket = Buckets.GetValue(tab, _ => new Bucket());
            string key = StripQuery(url);
            lock (bucket)
            {
                if (protocol == StreamProtocol.Hls && !isMaster)
                {
                    // A quality playlist of a stream that is already listed: hide it.
                    if (bucket.Items.Any(x => x.Protocol == StreamProtocol.Hls && x.IsMaster)) return;
                }
                if (protocol == StreamProtocol.Hls && isMaster)
                {
                    bucket.Items.RemoveAll(x => x.Protocol == StreamProtocol.Hls && !x.IsMaster);
                }

                int existing = bucket.Items.FindIndex(x => StripQuery(x.Url) == key);
                if (existing >= 0)
                {
                    bucket.Items[existing] = detected; // keep the freshest link (tokens change)
                }
                else
                {
                    if (bucket.Items.Count >= MaxPerTab) bucket.Items.RemoveAt(0);
                    bucket.Items.Add(detected);
                }
            }
        }

        private static async System.Threading.Tasks.Task<bool> LooksLikeMasterAsync(CoreWebView2WebResourceResponseView response)
        {
            try
            {
                using Stream? content = await response.GetContentAsync();
                if (content == null) return false;
                byte[] buffer = new byte[64 * 1024];
                int read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length));
                string text = Encoding.UTF8.GetString(buffer, 0, read);
                return text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not read a playlist body to tell master from quality playlist", ex);
                return false;
            }
        }
    }
}

