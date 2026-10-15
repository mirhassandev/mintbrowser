using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CustomBrowser.Helpers
{
    /// <summary>Checks and cleans an address typed for a new-tab shortcut, and says in plain words what is wrong with it.</summary>
    public static class ShortcutUrls
    {
        private static readonly Regex SchemeWithoutSlashes = new(@"^[A-Za-z][A-Za-z0-9+.\-]*:(?!\d+(/|$))", RegexOptions.Compiled);
        private static readonly Regex ExplicitPort = new(@"^[^/?#]*:\d+(/|\?|#|$)", RegexOptions.Compiled);

        /// <summary>
        /// Accepts "github.com", "https://github.com/x", "localhost:3000", "http://intranet/". A single-word name such as
        /// "intranet" is only accepted when the scheme or a port makes the intent clear (a bare "github" is almost always a typo).
        /// </summary>
        public static bool TryNormalize(string? input, out string url, out string error)
        {
            url = string.Empty;
            error = string.Empty;
            string text = (input ?? string.Empty).Trim();

            if (text.Length == 0) { error = "Enter a web address."; return false; }
            if (text.Any(char.IsWhiteSpace)) { error = "A web address can't contain spaces."; return false; }

            bool hasScheme = text.Contains("://", StringComparison.Ordinal);
            if (!hasScheme && SchemeWithoutSlashes.IsMatch(text))
            {
                error = "Only http:// and https:// addresses can be added.";
                return false;
            }

            if (!Uri.TryCreate(hasScheme ? text : "https://" + text, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host))
            {
                error = "That doesn't look like a web address.";
                return false;
            }
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                error = "Only http:// and https:// addresses can be added.";
                return false;
            }

            string host = uri.Host;
            bool singleWord = !host.Contains('.') && !host.Contains(':') && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            if (singleWord && !hasScheme && !ExplicitPort.IsMatch(text))
            {
                error = $"\"{host}\" isn't a full web address. Add the ending (for example {host}.com), or start with http:// for a name on your own network.";
                return false;
            }

            url = uri.AbsoluteUri;
            return true;
        }
    }
}
