using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CustomBrowser.Services.Streams
{
    /// <summary>The request headers the page used. Every request of one stream carries the same ones.</summary>
    public sealed class StreamHttpContext
    {
        public string Referer { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        public string CookieHeader { get; set; } = string.Empty;
        public string CookieHost { get; set; } = string.Empty;

        public static StreamHttpContext From(StreamJobSpec spec)
        {
            return new StreamHttpContext
            {
                Referer = spec.Referer,
                Origin = spec.Origin,
                UserAgent = spec.UserAgent,
                CookieHeader = spec.CookieHeader,
                CookieHost = spec.CookieHost
            };
        }

        public static StreamHttpContext From(DetectedStream d, string cookieHeader)
        {
            string host = string.Empty;
            if (Uri.TryCreate(d.Url, UriKind.Absolute, out Uri? u)) host = u.Host;
            return new StreamHttpContext
            {
                Referer = string.IsNullOrEmpty(d.Referer) ? d.PageUrl : d.Referer,
                Origin = d.Origin,
                UserAgent = d.UserAgent,
                CookieHeader = cookieHeader,
                CookieHost = host
            };
        }
    }

    public static class StreamHttp
    {
        public const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";

        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false, // redirects are followed in SendAsync so the login cookie can be dropped when they leave the site
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                MaxConnectionsPerServer = 32
            };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public static HttpRequestMessage BuildRequest(string url, StreamHttpContext ctx, long rangeStart = -1, long rangeLength = -1, long openRangeFrom = -1)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(ctx.UserAgent) ? DefaultUserAgent : ctx.UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            if (Uri.TryCreate(ctx.Referer, UriKind.Absolute, out Uri? refUri) &&
                (refUri.Scheme == Uri.UriSchemeHttp || refUri.Scheme == Uri.UriSchemeHttps))
            {
                req.Headers.Referrer = refUri;
            }
            if (!string.IsNullOrWhiteSpace(ctx.Origin)) req.Headers.TryAddWithoutValidation("Origin", ctx.Origin);
            if (!string.IsNullOrWhiteSpace(ctx.CookieHeader) && SameSite(url, ctx.CookieHost))
            {
                req.Headers.TryAddWithoutValidation("Cookie", ctx.CookieHeader);
            }

            if (rangeStart >= 0 && rangeLength > 0)
            {
                req.Headers.Range = new RangeHeaderValue(rangeStart, rangeStart + rangeLength - 1);
            }
            else if (openRangeFrom > 0)
            {
                req.Headers.Range = new RangeHeaderValue(openRangeFrom, null);
            }
            return req;
        }

        private const int MaxRedirects = 10;

        /// <summary>
        /// Sends the request and follows redirects by hand. The Cookie header is removed as soon as a redirect leaves the site the
        /// cookies belong to (or downgrades https to http), so a login cookie never reaches a CDN or a third party.
        /// </summary>
        public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Uri? first = req.RequestUri;
            HttpRequestMessage current = req;
            for (int hop = 0; ; hop++)
            {
                HttpResponseMessage resp = await Client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                int code = (int)resp.StatusCode;
                Uri? location = resp.Headers.Location;
                bool isRedirect = code is 301 or 302 or 303 or 307 or 308;
                if (!isRedirect || location == null || current.RequestUri == null) return resp;

                Uri next = location.IsAbsoluteUri ? location : new Uri(current.RequestUri, location);
                resp.Dispose();
                if (hop >= MaxRedirects) throw new HttpRequestException("Too many redirects");
                if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                {
                    throw new HttpRequestException("The server redirected to an unsupported address");
                }

                var follow = new HttpRequestMessage(HttpMethod.Get, next);
                foreach (var header in current.Headers) follow.Headers.TryAddWithoutValidation(header.Key, header.Value);
                bool downgrade = current.RequestUri.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp;
                if (downgrade || first == null || !SameSite(next.AbsoluteUri, first.Host))
                {
                    follow.Headers.Remove("Cookie");
                }
                if (!ReferenceEquals(current, req)) current.Dispose();
                current = follow;
            }
        }

        public static void EnsureOk(HttpResponseMessage resp)
        {
            if (resp.IsSuccessStatusCode) return;
            int code = (int)resp.StatusCode;
            throw new StreamHttpException(code, "HTTP " + code);
        }

        public static async Task<byte[]> GetBytesAsync(string url, StreamHttpContext ctx, CancellationToken ct,
            long rangeStart = -1, long rangeLength = -1, int maxBytes = 64 * 1024 * 1024)
        {
            using HttpRequestMessage req = BuildRequest(url, ctx, rangeStart, rangeLength);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                using HttpResponseMessage resp = await SendAsync(req, linked.Token).ConfigureAwait(false);
                EnsureOk(resp);
                using Stream body = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                var ms = new MemoryStream();
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), linked.Token).ConfigureAwait(false)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length > maxBytes) throw new IOException("The response is too large");
                    linked.CancelAfter(TimeSpan.FromSeconds(45));
                }
                return ms.ToArray();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The server did not answer in time");
            }
        }

        public static async Task<string> GetStringAsync(string url, StreamHttpContext ctx, CancellationToken ct)
        {
            byte[] bytes = await GetBytesAsync(url, ctx, ct, -1, -1, 16 * 1024 * 1024).ConfigureAwait(false);
            int skip = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
        }

        /// <summary>Cookies are only sent to the site they belong to (same host or the same registrable domain).</summary>
        private static bool SameSite(string url, string cookieHost)
        {
            if (string.IsNullOrEmpty(cookieHost)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u)) return false;
            return CustomBrowser.Helpers.SiteDomain.IsSameSite(u.Host, cookieHost);
        }
    }
}

