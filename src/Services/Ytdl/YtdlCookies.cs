using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CustomBrowser.Helpers;
using Microsoft.Web.WebView2.Core;

namespace CustomBrowser.Services.Ytdl
{
    /// <summary>
    /// Optional: hands the browser's login cookies for ONE site to yt-dlp (private, age-restricted or members-only videos).
    /// Only cookies that belong to the site of the page are written, into a temporary file that is deleted when the download is gone.
    /// Must be called on the UI thread (it talks to WebView2).
    /// </summary>
    public static class YtdlCookies
    {
        public static async Task<string?> ExportAsync(CoreWebView2 core, string pageUrl)
        {
            try
            {
                if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? page)) return null;
                List<string> keys = SiteKeys(page.Host);
                IReadOnlyList<CoreWebView2Cookie> all = await core.CookieManager.GetCookiesAsync(string.Empty);

                var sb = new StringBuilder();
                sb.Append("# Netscape HTTP Cookie File\n");
                long sessionExpiry = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
                int count = 0;
                char[] bad = { '\t', '\r', '\n' };

                foreach (CoreWebView2Cookie c in all)
                {
                    string domain = c.Domain ?? string.Empty;
                    string d = domain.TrimStart('.').ToLowerInvariant();
                    if (d.Length == 0) continue;
                    if (!keys.Any(k => d == k || d.EndsWith("." + k, StringComparison.Ordinal))) continue;

                    long expires;
                    if (c.IsSession)
                    {
                        expires = sessionExpiry;
                    }
                    else
                    {
                        DateTime utc = c.Expires.ToUniversalTime();
                        if (utc < DateTime.UtcNow) continue;
                        expires = new DateTimeOffset(utc).ToUnixTimeSeconds();
                    }

                    string name = c.Name ?? string.Empty;
                    string value = c.Value ?? string.Empty;
                    if (name.Length == 0 || name.IndexOfAny(bad) >= 0 || value.IndexOfAny(bad) >= 0) continue;

                    if (c.IsHttpOnly) sb.Append("#HttpOnly_");
                    sb.Append(domain).Append('\t');
                    sb.Append(domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t');
                    sb.Append(string.IsNullOrEmpty(c.Path) ? "/" : c.Path).Append('\t');
                    sb.Append(c.IsSecure ? "TRUE" : "FALSE").Append('\t');
                    sb.Append(expires.ToString(CultureInfo.InvariantCulture)).Append('\t');
                    sb.Append(name).Append('\t').Append(value).Append('\n');
                    count++;
                }

                if (count == 0) return null;
                string path = YtdlStore.NewCookiePath();
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not export the browser login for yt-dlp", ex);
                return null;
            }
        }

        /// <summary>The domains whose cookies belong to the site of this page (youtube.com also needs google.com, and so on).</summary>
        public static List<string> SiteKeys(string host)
        {
            string h = (host ?? string.Empty).ToLowerInvariant();
            if (h.StartsWith("www.", StringComparison.Ordinal)) h = h.Substring(4);
            string key = CustomBrowser.Helpers.SiteDomain.Registrable(h);

            var keys = new List<string> { key };
            if (key == "youtube.com" || key == "youtu.be" || key == "google.com")
            {
                keys.Add("youtube.com");
                keys.Add("google.com");
            }
            if (key == "facebook.com" || key == "fb.watch" || key == "fb.com")
            {
                keys.Add("facebook.com");
                keys.Add("fb.watch");
            }
            if (key == "x.com" || key == "twitter.com")
            {
                keys.Add("x.com");
                keys.Add("twitter.com");
            }
            return keys.Distinct().ToList();
        }
    }
}

