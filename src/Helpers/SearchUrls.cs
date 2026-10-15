using System;
using System.Collections.Generic;

namespace CustomBrowser.Helpers
{
    /// <summary>Recognises the address of a search results page.</summary>
    public static class SearchUrls
    {
        /// <summary>
        /// True when <paramref name="url"/> starts with the search part of one of the templates ("https://host/search?q={0}").
        /// Only the search part counts: another page of the same site (an article, a docs page) is not a search.
        /// </summary>
        public static bool Matches(string? url, IEnumerable<string> searchTemplates)
        {
            if (string.IsNullOrEmpty(url)) return false;
            foreach (string template in searchTemplates)
            {
                int slot = template.IndexOf("{0}", StringComparison.Ordinal);
                string prefix = slot > 0 ? template[..slot] : template;
                if (prefix.Length > 0 && url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
