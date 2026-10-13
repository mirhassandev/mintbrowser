using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CustomBrowser.Helpers;
using Microsoft.Web.WebView2.Core;

namespace CustomBrowser.Services
{
    public class CookieManagerService
    {
        // Most servers reject request headers larger than ~8 KB.
        private const int WarnHeaderLength = 8000;

        /// <summary>
        /// Builds a valid "Cookie" request header value for <paramref name="url"/>.
        /// Skips expired cookies, Secure cookies on plain http, and cookies whose name/value
        /// contain characters that are illegal in an HTTP header (those would make the
        /// download request throw). Cookies are ordered most-specific path first (RFC 6265).
        /// </summary>
        public static async Task<string> GetCookieHeaderAsync(CoreWebView2 webView, string url)
        {
            if (webView == null || string.IsNullOrWhiteSpace(url)) return string.Empty;

            bool isHttps = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            DateTime nowUtc = DateTime.UtcNow;

            var cookies = await webView.CookieManager.GetCookiesAsync(url);
            var parts = new List<string>();

            foreach (var cookie in cookies
                         .OrderByDescending(c => c.Path?.Length ?? 0)
                         .ThenBy(c => c.Name, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(cookie.Name)) continue;
                if (cookie.IsSecure && !isHttps) continue;
                if (!cookie.IsSession && cookie.Expires.ToUniversalTime() <= nowUtc) continue;

                if (!IsValidHeaderText(cookie.Name) || !IsValidHeaderText(cookie.Value))
                {
                    AppLog.Warn($"Skipping cookie '{cookie.Name}' for download: name/value has characters not allowed in an HTTP header.");
                    continue;
                }

                parts.Add($"{cookie.Name}={cookie.Value}");
            }

            string header = string.Join("; ", parts);
            if (header.Length > WarnHeaderLength)
            {
                AppLog.Warn($"Cookie header for download is {header.Length} characters long; the server may reject it.");
            }
            return header;
        }

        private static bool IsValidHeaderText(string? text)
        {
            if (text == null) return true; // an empty value is allowed
            foreach (char c in text)
            {
                if (c < 0x20 || c == 0x7F || c > 0xFF) return false;
            }
            return true;
        }
    }
}

