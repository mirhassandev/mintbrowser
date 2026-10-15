using System;
using System.Net;
using System.Text;

namespace CustomBrowser.Helpers
{
    /// <summary>The kinds of "page could not be loaded" failures that get the browser's own page.</summary>
    public enum ErrorPageKind
    {
        NameNotResolved,
        CannotConnect,
        Timeout,
        ConnectionReset,
        Offline
    }

    /// <summary>Builds the dark "this page can't be reached" page that replaces the engine's stock error page.</summary>
    public static class ErrorPageHtml
    {
        /// <summary>The short technical code shown in small print (the same wording other browsers use, so it can be searched for).</summary>
        public static string ErrorCode(ErrorPageKind kind) => kind switch
        {
            ErrorPageKind.NameNotResolved => "ERR_NAME_NOT_RESOLVED",
            ErrorPageKind.Timeout => "ERR_TIMED_OUT",
            ErrorPageKind.ConnectionReset => "ERR_CONNECTION_RESET",
            ErrorPageKind.Offline => "ERR_INTERNET_DISCONNECTED",
            _ => "ERR_CONNECTION_FAILED"
        };

        public static string Title(ErrorPageKind kind, string host) => kind switch
        {
            ErrorPageKind.NameNotResolved => "We can't find that site",
            ErrorPageKind.Timeout => $"{host} took too long to respond",
            ErrorPageKind.ConnectionReset => "The connection was interrupted",
            ErrorPageKind.Offline => "You're offline",
            _ => $"Can't connect to {host}"
        };

        public static string Lead(ErrorPageKind kind, string host) => kind switch
        {
            ErrorPageKind.NameNotResolved => $"{host} could not be found. The address may be misspelled, or the site may no longer exist.",
            ErrorPageKind.Timeout => "The site did not answer in time. It may be busy or your connection may be slow.",
            ErrorPageKind.ConnectionReset => "The connection to the site was closed before the page could load.",
            ErrorPageKind.Offline => "This computer is not connected to the internet.",
            _ => "The site refused the connection or could not be reached."
        };

        private static string[] Tips(ErrorPageKind kind) => kind switch
        {
            ErrorPageKind.NameNotResolved => new[]
            {
                "Check the address for typing mistakes.",
                "Make sure you are connected to the internet.",
                "If a VPN or custom DNS is on, try turning it off."
            },
            ErrorPageKind.Offline => new[]
            {
                "Check the Wi-Fi or network cable.",
                "Restart your router if other devices are offline too.",
                "Then try again."
            },
            ErrorPageKind.Timeout => new[]
            {
                "Wait a moment and try again.",
                "Check that other sites load.",
                "If a VPN or proxy is on, try turning it off."
            },
            ErrorPageKind.ConnectionReset => new[]
            {
                "Try again in a moment.",
                "Check your firewall, VPN or proxy settings."
            },
            _ => new[]
            {
                "The site may be down. Try again in a few minutes.",
                "Check that other sites load.",
                "Check your firewall, VPN or proxy settings."
            }
        };

        private const string Css =
            "html,body{margin:0;min-height:100%;background:#222;color:#f2f2f2;font:15px/1.55 'Segoe UI Variable Text','Segoe UI',sans-serif}" +
            "main{box-sizing:border-box;max-width:560px;margin:0 auto;padding:12vh 28px 48px}" +
            ".ico{width:44px;height:44px;border-radius:50%;border:1px solid #4a4a4a;display:flex;align-items:center;justify-content:center;" +
            "color:#cfcfcf;font-size:22px;font-weight:700;margin-bottom:22px}" +
            "h1{font-size:25px;font-weight:600;margin:0 0 10px;line-height:1.25;word-break:break-word}" +
            "p{margin:0 0 18px;color:#bdbdbd}" +
            "ul{margin:0 0 26px;padding-left:20px;color:#bdbdbd}li{margin:4px 0}" +
            "a.retry{display:inline-block;background:#f2f2f2;color:#111;text-decoration:none;font-weight:600;padding:10px 22px;border-radius:9px}" +
            "a.retry:hover{background:#fff}a.retry:focus-visible{outline:2px solid #fff;outline-offset:3px}" +
            ".code{margin-top:34px;color:#8a8a8a;font-size:12px;word-break:break-all}";

        /// <summary>The complete page. Everything taken from the address is HTML-encoded; the page runs no script.</summary>
        public static string Build(string url, ErrorPageKind kind)
        {
            string host = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0 ? uri.Host : url;
            string safeHost = WebUtility.HtmlEncode(host);

            var sb = new StringBuilder(2048);
            sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>").Append(safeHost).Append("</title><style>").Append(Css).Append("</style></head><body><main>");
            sb.Append("<div class=\"ico\" aria-hidden=\"true\">!</div>");
            sb.Append("<h1>").Append(WebUtility.HtmlEncode(Title(kind, host))).Append("</h1>");
            sb.Append("<p>").Append(WebUtility.HtmlEncode(Lead(kind, host))).Append("</p><ul>");
            foreach (string tip in Tips(kind)) sb.Append("<li>").Append(WebUtility.HtmlEncode(tip)).Append("</li>");
            sb.Append("</ul><a class=\"retry\" href=\"").Append(WebUtility.HtmlEncode(url)).Append("\">Try again</a>");
            sb.Append("<div class=\"code\">").Append(ErrorCode(kind)).Append("</div>");
            sb.Append("</main></body></html>");
            return sb.ToString();
        }
    }
}
