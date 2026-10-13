using System;
using System.Collections.Generic;
using System.Net;

namespace CustomBrowser.Helpers
{
    /// <summary>
    /// The "site" of a host name: the part cookies belong to (example.com, example.co.uk, or one tenant of a shared hosting domain
    /// such as user.github.io). There is no full public-suffix list here, only the common cases, and it errs towards treating
    /// two hosts as DIFFERENT sites, so a cookie is never sent further than intended.
    /// </summary>
    public static class SiteDomain
    {
        // Domains where every customer gets a sub-domain: those customers are different sites.
        private static readonly string[] SharedHosting =
        {
            "github.io", "gitlab.io", "blogspot.com", "herokuapp.com", "vercel.app", "netlify.app", "pages.dev", "workers.dev",
            "web.app", "firebaseapp.com", "appspot.com", "azurewebsites.net", "cloudfront.net", "amazonaws.com", "onrender.com",
            "fly.dev", "glitch.me", "repl.co", "wordpress.com", "myshopify.com", "weebly.com", "wixsite.com"
        };

        // Second-level names used under country codes: example.co.uk, example.com.au, example.ac.jp ...
        private static readonly HashSet<string> SecondLevel = new HashSet<string>(StringComparer.Ordinal)
        {
            "co", "com", "org", "net", "gov", "edu", "ac", "or", "ne", "go", "gob", "mil", "sch", "ltd", "plc", "nom", "gouv"
        };

        public static string Registrable(string? host)
        {
            string h = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            if (h.Length == 0) return h;
            if (IPAddress.TryParse(h.Trim('[', ']'), out _)) return h;

            string[] labels = h.Split('.');
            if (labels.Length <= 2) return h;

            foreach (string suffix in SharedHosting)
            {
                if (h.EndsWith("." + suffix, StringComparison.Ordinal))
                {
                    return Last(labels, suffix.Split('.').Length + 1);
                }
            }

            string tld = labels[labels.Length - 1];
            string second = labels[labels.Length - 2];
            return Last(labels, tld.Length == 2 && SecondLevel.Contains(second) ? 3 : 2);
        }

        /// <summary>True when both hosts belong to the same site: the same host, or the same registrable domain. Decides where a login cookie may be sent.</summary>
        public static bool IsSameSite(string? host, string? siteHost)
        {
            string h = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            string s = (siteHost ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            if (h.Length == 0 || s.Length == 0) return false;
            if (h == s) return true;
            string baseDomain = Registrable(s);
            return h == baseDomain || h.EndsWith("." + baseDomain, StringComparison.Ordinal);
        }

private static string Last(string[] labels, int take)
        {
            return take >= labels.Length ? string.Join(".", labels) : string.Join(".", labels, labels.Length - take, take);
        }
    }
}
