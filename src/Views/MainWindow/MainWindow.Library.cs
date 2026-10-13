using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using CustomBrowser.Helpers;
using CustomBrowser.Models;
using CustomBrowser.Services;
using CustomBrowser.Views;

namespace CustomBrowser
{
    /// <summary>
    /// The "library" page (https://local.browser/library.html, shown as mint://history, mint://bookmarks and mint://passwords):
    /// one dedicated page to manage history, bookmarks and saved logins. It is separate from the Downloads page.
    /// Every destructive action asks for confirmation here, in the same dialog the rest of the browser uses, so the page itself
    /// can never delete anything without the user answering that dialog.
    /// </summary>
    public partial class MainWindow
    {
        private const string LibraryUrl = "https://local.browser/library.html";
        private static readonly string[] LibrarySections = { "history", "bookmarks", "passwords" };

        /// <summary>Messages the library page may send through the web-message bridge.</summary>
        private static readonly HashSet<string> LibraryMessageTypes = new(StringComparer.Ordinal)
        {
            "get-history", "delete-history", "clear-history",
            "get-bookmarks", "delete-bookmarks", "clear-bookmarks",
            "clear-browsing-data", "open-url"
        };

        private static bool IsLibraryPage(string? url) =>
            !string.IsNullOrEmpty(url) && url.StartsWith(LibraryUrl, StringComparison.OrdinalIgnoreCase);

        /// <summary>"history", "bookmarks" or "passwords", read from the #fragment of a library address (history when missing).</summary>
        private static string LibrarySectionOf(string? url)
        {
            if (!IsLibraryPage(url)) return "history";
            int hash = url!.IndexOf('#');
            string fragment = hash >= 0 ? url[(hash + 1)..].Trim().ToLowerInvariant() : string.Empty;
            return LibrarySections.Contains(fragment) ? fragment : "history";
        }

        private static string LibraryTitle(string section) => section switch
        {
            "bookmarks" => "Bookmarks",
            "passwords" => "Passwords",
            _ => "History"
        };

        /// <summary>mint://history, chrome://history, about:bookmarks ... -> the library address, or null for anything else.</summary>
        private static string? ResolveLibraryAlias(string input)
        {
            input = input.Trim().TrimEnd('/');
            foreach (string prefix in new[] { "mint://", "chrome://", "edge://", "about:" })
            {
                if (!input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string name = input[prefix.Length..].Trim().ToLowerInvariant();
                if (name is "library" or "manage") name = "history";
                if (LibrarySections.Contains(name)) return LibraryUrl + "#" + name;
            }
            return null;
        }

        /// <summary>Ctrl+Shift+Delete, the sidebar's "open full page" button and the address bar: show the library tab on a section.</summary>
        private void OpenLibraryTab(string section) => _ = OpenLibraryTabAsync(section);

        private async Task OpenLibraryTabAsync(string section)
        {
            try
            {
                string url = LibraryUrl + "#" + section;
                BrowserTab? existing = Tabs.FirstOrDefault(tab => IsLibraryPage(tab.Url));
                if (existing != null)
                {
                    SelectedTab = existing;
                    if (!string.Equals(LibrarySectionOf(existing.Url), section, StringComparison.Ordinal))
                    {
                        existing.WebView?.CoreWebView2?.Navigate(url); // same document: the page switches section on hashchange
                    }
                    return;
                }
                await AddNewTabAsync(url);
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not open the library tab", ex);
                ThemedDialog.Show($"Could not open the {LibraryTitle(section)} page: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSidebarOpenFullPage_Click(object sender, RoutedEventArgs e)
        {
            string section = _currentSidebarView switch
            {
                "Bookmarks" => "bookmarks",
                "Passwords" => "passwords",
                _ => "history"
            };
            HideSidebarPanel();
            OpenLibraryTab(section);
        }

        /// <summary>The one confirmation popup every clear / bulk delete goes through (sidebar and library page alike). "No" is the default.</summary>
        private bool ConfirmDestructive(string title, string message) =>
            ThemedDialog.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

        // ---- Data sent to the page ----

        /// <summary>One icon per site (a data: address, because the built-in pages cannot load file:// pictures), shared by all rows of that site.</summary>
        private static Dictionary<string, string> CollectIcons(IEnumerable<string> urls, Func<string, string> hostOf)
        {
            var icons = new Dictionary<string, string>();
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string url in urls)
            {
                string host = hostOf(url);
                if (string.IsNullOrEmpty(host) || !tried.Add(host)) continue;
                string? data = FaviconCache.TryGetDataUri(url);
                if (data != null) icons[host] = data;
            }
            return icons;
        }

        private static string HostOfUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return url;
            return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        }

        private static long ToUnixMs(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();

        private static string BuildHistoryMessage()
        {
            List<HistoryEntry> entries = _dbService.GetHistoryList();
            var icons = CollectIcons(entries.Select(h => h.Url), HostOfUrl);
            var rows = entries.Select(h => new
            {
                url = h.Url,
                title = string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title,
                host = HostOfUrl(h.Url),
                visitCount = h.VisitCount,
                visitedAt = ToUnixMs(h.VisitedAt)
            });
            return JsonSerializer.Serialize(new { type = "history-data", data = rows, icons }, BridgeJsonOptions);
        }

        private static string BuildBookmarksMessage()
        {
            List<BookmarkEntry> entries = _dbService.GetAllBookmarks().OrderByDescending(b => b.AddedAt).ToList();
            var icons = CollectIcons(entries.Select(b => b.Url), HostOfUrl);
            var rows = entries.Select(b => new
            {
                url = b.Url,
                title = string.IsNullOrWhiteSpace(b.Title) ? b.Url : b.Title,
                host = HostOfUrl(b.Url),
                folder = b.Folder,
                addedAt = ToUnixMs(b.AddedAt)
            });
            return JsonSerializer.Serialize(new { type = "bookmarks-data", data = rows, icons }, BridgeJsonOptions);
        }

        private static void PostLibraryResult(CoreWebView2 core, string section, bool ok, string message, bool cancelled = false)
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { type = "library-result", section, ok, cancelled, message }, BridgeJsonOptions));
        }

        // ---- Messages from the page ----

        private static List<string> ReadUrls(JsonElement root)
        {
            var urls = new List<string>();
            if (root.TryGetProperty("urls", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    string? url = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url)) urls.Add(url);
                    if (urls.Count >= 50000) break;
                }
            }
            return urls;
        }

        /// <summary>The time range a "clear" button asked for: when it starts (null = everything) and how it reads in the confirmation.</summary>
        private static (DateTime? Since, string Label) ReadRange(JsonElement root)
        {
            string range = root.TryGetProperty("range", out JsonElement el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? "all" : "all";
            DateTime now = DateTime.Now;
            return range switch
            {
                "hour" => (now.AddHours(-1), "the last hour"),
                "day" => (now.AddDays(-1), "the last 24 hours"),
                "week" => (now.AddDays(-7), "the last 7 days"),
                "month" => (now.AddDays(-28), "the last 4 weeks"),
                _ => (null, "all time")
            };
        }

        private static string Plural(int count, string one, string many) => count == 1 ? one : many;

        private async Task HandleLibraryMessageAsync(CoreWebView2 core, string msgType, JsonElement root)
        {
            string section = msgType.Contains("bookmark", StringComparison.Ordinal) ? "bookmarks"
                           : msgType == "clear-browsing-data" ? "passwords" : "history";
            // Leave the WebView2 event callback first: the confirmation below is a modal dialog.
            await Task.Yield();
            try
            {
                switch (msgType)
                {
                    case "get-history":
                        core.PostWebMessageAsJson(BuildHistoryMessage());
                        break;

                    case "get-bookmarks":
                        core.PostWebMessageAsJson(BuildBookmarksMessage());
                        break;

                    case "open-url":
                    {
                        string? target = root.TryGetProperty("url", out JsonElement urlEl) ? urlEl.GetString() : null;
                        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) &&
                            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        {
                            _ = OpenTabSafeAsync(uri.AbsoluteUri);
                        }
                        break;
                    }

                    case "delete-history":
                    {
                        List<string> urls = ReadUrls(root);
                        if (urls.Count == 0) { PostLibraryResult(core, section, false, "Nothing is selected."); break; }
                        // One row's own delete button needs no question (same as the sidebar); a selection does.
                        if (urls.Count > 1 && !ConfirmDestructive("Delete history",
                                $"Delete the {urls.Count} selected history entries?\n\nThis cannot be undone."))
                        {
                            PostLibraryResult(core, section, false, string.Empty, cancelled: true);
                            break;
                        }
                        int removed = _dbService.RemoveHistoryMany(urls);
                        PostLibraryResult(core, section, true, $"Removed {removed} {Plural(removed, "entry", "entries")} from history.");
                        core.PostWebMessageAsJson(BuildHistoryMessage());
                        break;
                    }

                    case "clear-history":
                    {
                        (DateTime? since, string label) = ReadRange(root);
                        string question = since == null
                            ? "Clear ALL browsing history?\n\nThis cannot be undone."
                            : $"Clear the browsing history from {label}?\n\nThis cannot be undone.";
                        if (!ConfirmDestructive("Clear History", question))
                        {
                            PostLibraryResult(core, section, false, string.Empty, cancelled: true);
                            break;
                        }
                        string done;
                        if (since == null)
                        {
                            _dbService.ClearHistory();
                            done = "Cleared all browsing history.";
                        }
                        else
                        {
                            int removed = _dbService.ClearHistorySince(since.Value);
                            done = $"Cleared history from {label} ({removed} {Plural(removed, "entry", "entries")}).";
                        }
                        PostLibraryResult(core, section, true, done);
                        core.PostWebMessageAsJson(BuildHistoryMessage());
                        break;
                    }

                    case "delete-bookmarks":
                    {
                        List<string> urls = ReadUrls(root);
                        if (urls.Count == 0) { PostLibraryResult(core, section, false, "Nothing is selected."); break; }
                        if (urls.Count > 1 && !ConfirmDestructive("Delete bookmarks",
                                $"Delete the {urls.Count} selected bookmarks?\n\nThis cannot be undone."))
                        {
                            PostLibraryResult(core, section, false, string.Empty, cancelled: true);
                            break;
                        }
                        int removed = _dbService.RemoveBookmarks(urls);
                        PostLibraryResult(core, section, true, $"Removed {removed} {Plural(removed, "bookmark", "bookmarks")}.");
                        core.PostWebMessageAsJson(BuildBookmarksMessage());
                        break;
                    }

                    case "clear-bookmarks":
                    {
                        if (!ConfirmDestructive("Delete all bookmarks", "Delete ALL bookmarks?\n\nThis cannot be undone."))
                        {
                            PostLibraryResult(core, section, false, string.Empty, cancelled: true);
                            break;
                        }
                        int removed = _dbService.ClearBookmarks();
                        PostLibraryResult(core, section, true, $"Removed {removed} {Plural(removed, "bookmark", "bookmarks")}.");
                        core.PostWebMessageAsJson(BuildBookmarksMessage());
                        break;
                    }

                    case "clear-browsing-data":
                        await ClearProfileDataAsync(core, root);
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Library action '{msgType}' failed", ex);
                PostLibraryResult(core, section, false, "That did not work: " + ex.Message);
            }
        }

        /// <summary>
        /// Saved passwords and autofill data live inside the WebView2 profile, which can only remove them by kind and by time range;
        /// it cannot list or delete a single login. Nothing here reads, shows or logs a password.
        /// </summary>
        private async Task ClearProfileDataAsync(CoreWebView2 core, JsonElement root)
        {
            var kinds = (CoreWebView2BrowsingDataKinds)0;
            var names = new List<string>();
            if (root.TryGetProperty("kinds", out JsonElement kindList) && kindList.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in kindList.EnumerateArray())
                {
                    switch (item.GetString())
                    {
                        case "passwords":
                            kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave;
                            if (!names.Contains("saved passwords")) names.Add("saved passwords");
                            break;
                        case "autofill":
                            kinds |= CoreWebView2BrowsingDataKinds.GeneralAutofill;
                            if (!names.Contains("autofill data")) names.Add("autofill data");
                            break;
                    }
                }
            }
            if ((int)kinds == 0)
            {
                PostLibraryResult(core, "passwords", false, "Tick what you want to clear first.");
                return;
            }

            (DateTime? since, string label) = ReadRange(root);
            string what = string.Join(" and ", names);
            string question = since == null
                ? $"Remove ALL {what} from Mint Browser?\n\nThis cannot be undone. Sites will offer to save your logins again the next time you sign in."
                : $"Remove the {what} saved in {label}?\n\nThis cannot be undone.";
            if (!ConfirmDestructive("Clear " + what, question))
            {
                PostLibraryResult(core, "passwords", false, string.Empty, cancelled: true);
                return;
            }

            if (since == null) await core.Profile.ClearBrowsingDataAsync(kinds);
            else await core.Profile.ClearBrowsingDataAsync(kinds, since.Value, DateTime.Now.AddMinutes(1));

            AppLog.Info($"Cleared {what} ({label}) from the WebView2 profile");
            PostLibraryResult(core, "passwords", true, $"Removed {what} from {(since == null ? "all time" : label)}.");
        }
    }
}

