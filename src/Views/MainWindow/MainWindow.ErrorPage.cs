using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using CustomBrowser.Helpers;
using CustomBrowser.Models;

namespace CustomBrowser
{
    /// <summary>
    /// A page that could not be loaded gets the browser's own dark page instead of the engine's stock one. It is written into the
    /// error document itself, so the address, the history and the Back button are untouched, and nothing can loop. If the engine's
    /// error document looks different from what is expected, nothing is replaced and the stock page simply stays.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Only plain network failures. Certificate warnings, cancelled loads and downloads are never touched.</summary>
        private static ErrorPageKind? ErrorKindFor(CoreWebView2WebErrorStatus status) => status switch
        {
            CoreWebView2WebErrorStatus.HostNameNotResolved => ErrorPageKind.NameNotResolved,
            CoreWebView2WebErrorStatus.CannotConnect => ErrorPageKind.CannotConnect,
            CoreWebView2WebErrorStatus.ServerUnreachable => ErrorPageKind.CannotConnect,
            CoreWebView2WebErrorStatus.Timeout => ErrorPageKind.Timeout,
            CoreWebView2WebErrorStatus.ConnectionReset => ErrorPageKind.ConnectionReset,
            CoreWebView2WebErrorStatus.Disconnected => ErrorPageKind.Offline,
            _ => null
        };

        private static async Task ShowFriendlyErrorPageAsync(BrowserTab tab, CoreWebView2 core, CoreWebView2WebErrorStatus status)
        {
            try
            {
                ErrorPageKind? kind = ErrorKindFor(status);
                if (kind == null) return;

                string url = !string.IsNullOrEmpty(tab.Url) ? tab.Url : core.Source;
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return;
                }

                string html = ErrorPageHtml.Build(uri.AbsoluteUri, kind.Value);
                // JSON turns the page into a safe script string ("<" and quotes are escaped).
                string script = "(function(){if(!document.getElementById('main-frame-error')) return 'skip';" +
                                "document.open();document.write(" + JsonSerializer.Serialize(html) + ");document.close();return 'ok';})()";
                string result = await core.ExecuteScriptAsync(script);
                AppLog.Debug($"Friendly error page for {uri.Host} ({status}): {result}");
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not show the friendly error page - the stock page stays", ex);
            }
        }
    }
}
