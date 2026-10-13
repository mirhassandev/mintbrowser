using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using CustomBrowser.Helpers;
using CustomBrowser.Models;

namespace CustomBrowser.Services
{
    /// <summary>
    /// Implements the Places storage engine (similar to Mozilla Places / places.sqlite)
    /// providing indexed history tracking, frecency scoring, and bookmark management.
    /// </summary>
    public class DatabaseService : IDisposable
    {
        private static DatabaseService? _instance;
        private static readonly object _instanceLock = new();

        /// <summary>Global shared DatabaseService instance.</summary>
        public static DatabaseService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= Create();
                    }
                }
                return _instance;
            }
        }

        private readonly LiteDatabase _db;
        private readonly ILiteCollection<HistoryEntry> _history;
        private readonly ILiteCollection<BookmarkEntry> _bookmarks;
        private readonly ILiteCollection<DownloadEntry> _downloads;
        public event Action? OnHistoryChanged;
        public event Action? OnBookmarksChanged;
        private readonly object _lock = new();
        private bool _disposed;

        /// <summary>
        /// False when the on-disk database could not be opened and a temporary in-memory
        /// database is being used instead (history/bookmarks will not survive a restart).
        /// </summary>
        public bool IsPersistent { get; }

        /// <summary>
        /// Opens the Places database. Never throws: if the file cannot be opened (locked by another
        /// running copy, corrupt, no permission...) the problem is logged and the browser still starts
        /// with a temporary in-memory database.
        /// </summary>
        public static DatabaseService Create()
        {
            LiteDatabase? db = null;
            try
            {
                string dbFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CustomBrowser");
                Directory.CreateDirectory(dbFolder);
                string dbPath = Path.Combine(dbFolder, "MintPlaces.db");

                // Shared mode lets two browser processes use the same file without "file in use" errors.
                db = new LiteDatabase(new ConnectionString
                {
                    Filename = dbPath,
                    Connection = ConnectionType.Shared
                });
                return new DatabaseService(db, persistent: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not open MintPlaces.db - using a temporary in-memory database instead", ex);
                try { db?.Dispose(); }
                catch (Exception disposeEx) { AppLog.Debug("Disposing failed database handle", disposeEx); }
                return new DatabaseService(new LiteDatabase(new MemoryStream()), persistent: false);
            }
        }

        private DatabaseService(LiteDatabase db, bool persistent)
        {
            _db = db;
            IsPersistent = persistent;

            _history = _db.GetCollection<HistoryEntry>("history");
            _bookmarks = _db.GetCollection<BookmarkEntry>("bookmarks");
            _downloads = _db.GetCollection<DownloadEntry>("downloads");

            // Indexes for fast frecency, URL lookup, and downloads
            _history.EnsureIndex(x => x.Url, unique: true);
            _history.EnsureIndex(x => x.VisitedAt);
            _history.EnsureIndex(x => x.Frecency);
            _bookmarks.EnsureIndex(x => x.Url, unique: true);
            _downloads.EnsureIndex(x => x.Id, unique: true);
            _downloads.EnsureIndex(x => x.StartedAt);

            // Remove internal pages that older versions saved into history.
            try
            {
                _history.DeleteMany(x => x.Url.StartsWith("chrome-extension:") || x.Url.StartsWith("chrome:") || x.Url.StartsWith("edge:") || x.Url.StartsWith("devtools:") || x.Url.StartsWith("view-source:"));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not clean internal pages out of history", ex);
            }

            // Finished downloads no longer need their resume data (it can be large).
            try
            {
                foreach (DownloadEntry done in _downloads.Find(x => x.Status == DownloadStatus.Completed && (x.PackageJson != null || x.CookieHeader != "")).ToList())
                {
                    done.PackageJson = null;
                    done.CookieHeader = string.Empty;
                    _downloads.Update(done);
                }

                // Unfinished downloads keep their cookies so they can resume, but not for ever: old session cookies are useless and sensitive.
                DateTime cookieCutoff = DateTime.Now.AddDays(-7);
                foreach (DownloadEntry stale in _downloads.Find(x => x.Status != DownloadStatus.Completed && x.CookieHeader != "" && x.StartedAt < cookieCutoff).ToList())
                {
                    stale.CookieHeader = string.Empty;
                    _downloads.Update(stale);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not clear resume data of finished downloads", ex);
            }

            // Older versions stored a Google favicon-service address that contains the full page URL. Showing it in the sidebar
            // would send that URL to Google, so such addresses are replaced with the icon address of the site itself.
            try
            {
                const string OldFaviconPrefix = "https://www.google.com/s2/favicons";
                List<HistoryEntry> staleHistory = _history.Find(x => x.FaviconUrl.StartsWith(OldFaviconPrefix)).ToList();
                foreach (HistoryEntry staleEntry in staleHistory) staleEntry.FaviconUrl = OriginFaviconOf(staleEntry.Url);
                if (staleHistory.Count > 0) _history.Update(staleHistory);
                List<BookmarkEntry> staleBookmarks = _bookmarks.Find(x => x.FaviconUrl.StartsWith(OldFaviconPrefix)).ToList();
                foreach (BookmarkEntry staleMark in staleBookmarks) staleMark.FaviconUrl = OriginFaviconOf(staleMark.Url);
                if (staleBookmarks.Count > 0) _bookmarks.Update(staleBookmarks);
                int replaced = staleHistory.Count + staleBookmarks.Count;
                if (replaced > 0) AppLog.Info($"Replaced {replaced} old favicon address(es) that contained full page URLs");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not clean old favicon addresses", ex);
            }

            // Stored scores only change on a visit or a bookmark change, so refresh them once per start:
            // pages that were popular long ago then fade out of address-bar suggestions and Top Sites.
            try
            {
                RecalculateFrecency();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not refresh frecency scores", ex);
            }
        }

        // ------------------------------------------------------------------
        // History & Frecency Engine (Firefox Places Architecture)
        // ------------------------------------------------------------------

        private static string OriginFaviconOf(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? u) &&
                (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(u.Host))
            {
                return u.GetLeftPart(UriPartial.Authority) + "/favicon.ico";
            }
            return string.Empty;
        }

        private static bool IsUrlTooLongForIndex(string? url)
        {
            return string.IsNullOrWhiteSpace(url) || System.Text.Encoding.UTF8.GetByteCount(url) > 900;
        }

        // A repeat visit to the same URL inside this window is the same visit: SourceChanged and NavigationCompleted both
        // report it, and a slow page can take longer than a few seconds between the two. It is not counted twice.
        private const int VisitThrottleSeconds = 30;

        /// <summary>Records a visit. Internal and non-web addresses are ignored; a repeat visit within 30 seconds is not counted twice.</summary>
        /// <param name="title">Page title; ignored while it is empty or a placeholder.</param>
        /// <param name="url">Full page address (also the unique key).</param>
        /// <param name="faviconUrl">Locally cached icon address, or empty to keep the stored one.</param>
        public void AddHistory(string title, string url, string faviconUrl = "")
        {
            if (string.IsNullOrWhiteSpace(url) ||
                IsUrlTooLongForIndex(url) ||
                url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("chrome-extension:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("chrome:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("edge:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("devtools:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://local.browser", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (_lock)
            {
                if (_disposed) return;
                try
                {
                    bool isBookmarked = _bookmarks.Exists(b => b.Url == url);
                    var existing = _history.FindOne(x => x.Url == url);

                    if (existing != null)
                    {
                        // Throttling: only count a new visit if the last one is at least VisitThrottleSeconds old
                        if ((DateTime.Now - existing.VisitedAt).TotalSeconds >= VisitThrottleSeconds)
                        {
                            existing.VisitCount++;
                        }
                        existing.VisitedAt = DateTime.Now;
                        if (!string.IsNullOrWhiteSpace(title) && title != url && title != "Loading..." && title != "New Tab")
                        {
                            existing.Title = title;
                        }
                        if (!string.IsNullOrWhiteSpace(faviconUrl))
                        {
                            existing.FaviconUrl = faviconUrl;
                        }
                        existing.Frecency = CalculateFrecency(existing.VisitCount, existing.VisitedAt, isBookmarked);
                        _history.Update(existing);
                        OnHistoryChanged?.Invoke();
                    }
                    else
                    {
                        var entry = new HistoryEntry
                        {
                            Url = url,
                            Title = string.IsNullOrWhiteSpace(title) || title == "Loading..." ? url : title,
                            FaviconUrl = faviconUrl,
                            VisitCount = 1,
                            VisitedAt = DateTime.Now,
                            FirstVisitedAt = DateTime.Now,
                            Frecency = CalculateFrecency(1, DateTime.Now, isBookmarked),
                            IsHidden = false
                        };
                        _history.Insert(entry);
                        OnHistoryChanged?.Invoke();
                    }
                }
                catch (Exception ex) { AppLog.Error("DatabaseService.AddHistory", ex); }
            }
        }

        /// <summary>
        /// Updates the title of a visited page asynchronously (e.g. SPAs changing title after load).
        /// </summary>
        public void UpdateTitle(string url, string title)
        {
            if (string.IsNullOrWhiteSpace(url) || IsUrlTooLongForIndex(url) || string.IsNullOrWhiteSpace(title) || title == "Loading..." || title == "New Tab") return;

            lock (_lock)
            {
                if (_disposed) return;
                try
                {
                    var existing = _history.FindOne(x => x.Url == url);
                    if (existing != null)
                    {
                        if (existing.Title != title)
                        {
                            existing.Title = title;
                            _history.Update(existing);
                        OnHistoryChanged?.Invoke();
                        }
                    }
                    else
                    {
                        AddHistory(title, url);
                    }
                }
                catch (Exception ex) { AppLog.Error("DatabaseService.UpdateTitle", ex); }
            }
        }

        /// <summary>
        /// Computes Mozilla Firefox Frecency score:
        /// Frecency = (VisitCount * RecencyWeight) + (isBookmarked ? 140 : 0)
        /// </summary>
        public static int CalculateFrecency(int visitCount, DateTime visitedAt, bool isBookmarked)
        {
            double daysAgo = (DateTime.Now - visitedAt).TotalDays;
            int recencyWeight = daysAgo <= 4 ? 100 :
                                daysAgo <= 14 ? 70 :
                                daysAgo <= 31 ? 50 :
                                daysAgo <= 90 ? 30 : 10;

            int score = (Math.Min(visitCount, 50) * recencyWeight) + (isBookmarked ? 140 : 0);
            return score;
        }

        /// <summary>
        /// Re-scores every history entry against today. The stored score only changes when a page is visited or its bookmark
        /// state changes, so without this an old favourite keeps its high score for ever. Only entries whose score actually
        /// changed are written back. Called once at startup.
        /// </summary>
        private void RecalculateFrecency()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var bookmarkedUrls = new HashSet<string>(_bookmarks.FindAll().Select(b => b.Url));
            var changed = new List<HistoryEntry>();
            int total = 0;
            foreach (HistoryEntry entry in _history.FindAll())
            {
                total++;
                int score = CalculateFrecency(entry.VisitCount, entry.VisitedAt, bookmarkedUrls.Contains(entry.Url));
                if (score == entry.Frecency) continue;
                entry.Frecency = score;
                changed.Add(entry);
            }
            if (changed.Count > 0)
            {
                _history.Update(changed);
                AppLog.Info($"Frecency refreshed: {changed.Count} of {total} history entries changed in {watch.ElapsedMilliseconds} ms");
            }
        }

        public enum HistorySortMode
        {
            Date,
            Site,
            DateAndSite,
            LastVisited,
            MostVisited
        }

        public List<HistoryGroup> GetGroupedHistory(string filterQuery = "", HistorySortMode sortMode = HistorySortMode.Date, int limit = 200)
        {
            lock (_lock)
            {
                if (_disposed) return new List<HistoryGroup>();
                try
                {
                    var query = _history.Query();
                    if (!string.IsNullOrWhiteSpace(filterQuery))
                    {
                        string q = filterQuery.Trim();
                        query = query.Where(x => x.Title.Contains(q) || x.Url.Contains(q));
                    }

                    List<HistoryEntry> rawList;
                    switch (sortMode)
                    {
                        case HistorySortMode.MostVisited:
                            rawList = query.OrderByDescending(x => x.VisitCount).Limit(limit).ToList();
                            return new List<HistoryGroup>
                            {
                                new HistoryGroup { Header = "Most Visited", Items = rawList }
                            };
                        case HistorySortMode.Site:
                            rawList = query.OrderBy(x => x.Url).Limit(limit).ToList();
                            return rawList.GroupBy(x => x.Host)
                                          .OrderBy(g => g.Key)
                                          .Select(g => new HistoryGroup { Header = g.Key, Items = g.ToList() })
                                          .ToList();
                        case HistorySortMode.LastVisited:
                        case HistorySortMode.Date:
                        default:
                            rawList = query.OrderByDescending(x => x.VisitedAt).Limit(limit).ToList();
                            var today = DateTime.Today;
                            var yesterday = today.AddDays(-1);
                            var last7Days = today.AddDays(-7);
                            var thisMonth = new DateTime(today.Year, today.Month, 1);

                            var groups = new List<HistoryGroup>();
                            var todayItems = rawList.Where(x => x.VisitedAt.Date == today).ToList();
                            if (todayItems.Count > 0)
                            {
                                groups.Add(new HistoryGroup
                                {
                                    Header = $"Today - {DateTime.Now:dddd, MMMM d, yyyy}",
                                    Items = todayItems
                                });
                            }

                            var yesterdayItems = rawList.Where(x => x.VisitedAt.Date == yesterday).ToList();
                            if (yesterdayItems.Count > 0)
                            {
                                groups.Add(new HistoryGroup { Header = "Yesterday", Items = yesterdayItems });
                            }

                            var last7Items = rawList.Where(x => x.VisitedAt.Date < yesterday && x.VisitedAt.Date >= last7Days).ToList();
                            if (last7Items.Count > 0)
                            {
                                groups.Add(new HistoryGroup { Header = "Last 7 days", Items = last7Items });
                            }

                            var monthItems = rawList.Where(x => x.VisitedAt.Date < last7Days && x.VisitedAt.Date >= thisMonth).ToList();
                            if (monthItems.Count > 0)
                            {
                                groups.Add(new HistoryGroup { Header = "This month", Items = monthItems });
                            }

                            var olderItems = rawList.Where(x => x.VisitedAt.Date < thisMonth).ToList();
                            if (olderItems.Count > 0)
                            {
                                groups.Add(new HistoryGroup { Header = "Older", Items = olderItems });
                            }

                            if (groups.Count == 0 && rawList.Count > 0)
                            {
                                groups.Add(new HistoryGroup { Header = "History", Items = rawList });
                            }

                            return groups;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.GetGroupedHistory", ex);
                    return new List<HistoryGroup>();
                }
            }
        }

        public IEnumerable<HistoryEntry> GetRecentHistory(int limit = 100)
        {
            lock (_lock)
            {
                if (_disposed) return Enumerable.Empty<HistoryEntry>();
                try
                {
                    return _history.Query()
                                   .OrderByDescending(x => x.VisitedAt)
                                   .Limit(limit)
                                   .ToList();
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.GetRecentHistory", ex);
                    return Enumerable.Empty<HistoryEntry>();
                }
            }
        }

        /// <summary>
        /// Returns frecency-ranked history entries for omnibox autocomplete.
        /// </summary>
        public IEnumerable<HistoryEntry> GetFrecentHistory(int limit = 50)
        {
            lock (_lock)
            {
                if (_disposed) return Enumerable.Empty<HistoryEntry>();
                try
                {
                    return _history.Query()
                                   .OrderByDescending(x => x.Frecency)
                                   .Limit(limit)
                                   .ToList();
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.GetFrecentHistory", ex);
                    return Enumerable.Empty<HistoryEntry>();
                }
            }
        }

        public bool RemoveHistory(string url)
        {
            lock (_lock)
            {
                if (_disposed) return false;
                try
                {
                    bool res = _history.DeleteMany(x => x.Url == url) > 0; if (res) OnHistoryChanged?.Invoke(); return res;
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.RemoveHistory", ex);
                    return false;
                }
            }
        }

        /// <summary>Stores the locally cached icon of a page that is already in history / bookmarks.</summary>
        public void UpdateFavicon(string url, string faviconUrl)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(faviconUrl) || IsUrlTooLongForIndex(url)) return;
            lock (_lock)
            {
                if (_disposed) return;
                try
                {
                    var historyEntry = _history.FindOne(x => x.Url == url);
                    bool historyChanged = historyEntry != null && historyEntry.FaviconUrl != faviconUrl;
                    if (historyEntry != null && historyChanged)
                    {
                        historyEntry.FaviconUrl = faviconUrl;
                        _history.Update(historyEntry);
                    }
                    var bookmarkEntry = _bookmarks.FindOne(x => x.Url == url);
                    bool bookmarkChanged = bookmarkEntry != null && bookmarkEntry.FaviconUrl != faviconUrl;
                    if (bookmarkEntry != null && bookmarkChanged)
                    {
                        bookmarkEntry.FaviconUrl = faviconUrl;
                        _bookmarks.Update(bookmarkEntry);
                    }
                    if (historyChanged) OnHistoryChanged?.Invoke();
                    if (bookmarkChanged) OnBookmarksChanged?.Invoke();
                }
                catch (Exception ex) { AppLog.Error("DatabaseService.UpdateFavicon", ex); }
            }
        }

        public void ClearHistory()
        {
            lock (_lock)
            {
                if (_disposed) return;
                try
                {
                    _history.DeleteAll();
                    OnHistoryChanged?.Invoke();
                }
                catch (Exception ex) { AppLog.Error("DatabaseService.ClearHistory", ex); }
            }
        }

        /// <summary>Every history entry, newest visit first, for the full-page manager (which groups, searches and selects on its own).</summary>
        public List<HistoryEntry> GetHistoryList(int limit = 5000)
        {
            lock (_lock)
            {
                if (_disposed) return new List<HistoryEntry>();
                try
                {
                    return _history.Query().OrderByDescending(x => x.VisitedAt).Limit(limit).ToList();
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.GetHistoryList", ex);
                    return new List<HistoryEntry>();
                }
            }
        }

        /// <summary>Removes the history entries for these addresses in one go. Returns how many were removed.</summary>
        public int RemoveHistoryMany(IEnumerable<string> urls)
        {
            lock (_lock)
            {
                if (_disposed) return 0;
                int removed = 0;
                try
                {
                    foreach (string url in urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct())
                    {
                        removed += _history.DeleteMany(x => x.Url == url);
                    }
                    if (removed > 0) OnHistoryChanged?.Invoke();
                }
                catch (Exception ex) { AppLog.Error("DatabaseService.RemoveHistoryMany", ex); }
                return removed;
            }
        }

        /// <summary>Removes every history entry last visited at or after <paramref name="since"/>. Returns how many were removed.</summary>
        public int ClearHistorySince(DateTime since)
        {
            lock (_lock)
            {
                if (_disposed) return 0;
                try
                {
                    int removed = _history.DeleteMany(x => x.VisitedAt >= since);
                    if (removed > 0) OnHistoryChanged?.Invoke();
                    return removed;
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.ClearHistorySince", ex);
                    return 0;
                }
            }
        }

        // ------------------------------------------------------------------
        // Bookmarks Management (Firefox Bookmarks Architecture)
        // ------------------------------------------------------------------

        /// <summary>Adds a bookmark or updates the existing one for the same address. Returns false for addresses that cannot be bookmarked.</summary>
        public bool AddOrUpdateBookmark(string title, string url, string folder = "Bookmarks Toolbar", string tags = "", string faviconUrl = "")
        {
            if (string.IsNullOrWhiteSpace(url) ||
                IsUrlTooLongForIndex(url) ||
                url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://local.browser", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            lock (_lock)
            {
                if (_disposed) return false;
                try
                {
                    var existing = _bookmarks.FindOne(x => x.Url == url);
                    if (existing != null)
                    {
                        existing.Title = string.IsNullOrWhiteSpace(title) ? existing.Title : title;
                        existing.Folder = string.IsNullOrWhiteSpace(folder) ? existing.Folder : folder;
                        existing.Tags = tags ?? existing.Tags;
                        if (!string.IsNullOrWhiteSpace(faviconUrl)) existing.FaviconUrl = faviconUrl;
                        existing.LastModifiedAt = DateTime.Now;
                        _bookmarks.Update(existing);
                    }
                    else
                    {
                        _bookmarks.Insert(new BookmarkEntry
                        {
                            Title = string.IsNullOrWhiteSpace(title) ? url : title,
                            Url = url,
                            Folder = string.IsNullOrWhiteSpace(folder) ? "Bookmarks Toolbar" : folder,
                            Tags = tags ?? string.Empty,
                            FaviconUrl = faviconUrl,
                            AddedAt = DateTime.Now,
                            LastModifiedAt = DateTime.Now
                        });
                    }

                    // Re-score frecency in history
                    var h = _history.FindOne(x => x.Url == url);
                    if (h != null)
                    {
                        h.Frecency = CalculateFrecency(h.VisitCount, h.VisitedAt, isBookmarked: true);
                        _history.Update(h);
                    }

                    OnBookmarksChanged?.Invoke();
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.AddOrUpdateBookmark", ex);
                    return false;
                }
            }
        }

        public bool AddBookmark(string title, string url) => AddOrUpdateBookmark(title, url);

        public bool IsBookmarked(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            lock (_lock)
            {
                if (_disposed) return false;
                try
                {
                    return _bookmarks.Exists(x => x.Url == url);
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.IsBookmarked", ex);
                    return false;
                }
            }
        }

        public BookmarkEntry? GetBookmark(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            lock (_lock)
            {
                if (_disposed) return null;
                try
                {
                    return _bookmarks.FindOne(x => x.Url == url);
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.GetBookmark", ex);
                    return null;
                }
            }
        }

        public IEnumerable<BookmarkEntry> GetAllBookmarks()
        {
            lock (_lock)
            {
                if (_disposed) return Enumerable.Empty<BookmarkEntry>();
                try
                {
                    return _bookmarks.FindAll().ToList();
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.GetAllBookmarks", ex);
                    return Enumerable.Empty<BookmarkEntry>();
                }
            }
        }

        public bool RemoveBookmark(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            lock (_lock)
            {
                if (_disposed) return false;
                try
                {
                    bool removed = _bookmarks.DeleteMany(x => x.Url == url) > 0;
                    if (removed)
                    {
                        var h = _history.FindOne(x => x.Url == url);
                        if (h != null)
                        {
                            h.Frecency = CalculateFrecency(h.VisitCount, h.VisitedAt, isBookmarked: false);
                            _history.Update(h);
                        }
                    }
                    if (removed) OnBookmarksChanged?.Invoke();
                    return removed;
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.RemoveBookmark", ex);
                    return false;
                }
            }
        }

        /// <summary>Removes the bookmarks for these addresses in one go. Returns how many were removed.</summary>
        public int RemoveBookmarks(IEnumerable<string> urls)
        {
            lock (_lock)
            {
                if (_disposed) return 0;
                int removed = 0;
                try
                {
                    foreach (string url in urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct())
                    {
                        if (_bookmarks.DeleteMany(x => x.Url == url) == 0) continue;
                        removed++;
                        var h = _history.FindOne(x => x.Url == url);
                        if (h != null)
                        {
                            h.Frecency = CalculateFrecency(h.VisitCount, h.VisitedAt, isBookmarked: false);
                            _history.Update(h);
                        }
                    }
                    if (removed > 0) OnBookmarksChanged?.Invoke();
                }
                catch (Exception ex) { AppLog.Error("DatabaseService.RemoveBookmarks", ex); }
                return removed;
            }
        }

        /// <summary>Removes every bookmark. Returns how many were removed.</summary>
        public int ClearBookmarks()
        {
            lock (_lock)
            {
                if (_disposed) return 0;
                try
                {
                    int removed = _bookmarks.DeleteAll();
                    foreach (HistoryEntry h in _history.FindAll().ToList())
                    {
                        h.Frecency = CalculateFrecency(h.VisitCount, h.VisitedAt, isBookmarked: false);
                        _history.Update(h);
                    }
                    if (removed > 0) OnBookmarksChanged?.Invoke();
                    return removed;
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.ClearBookmarks", ex);
                    return 0;
                }
            }
        }

        // ------------------------------------------------------------------
        // Downloads Management (IDM-Style Persistent Database)
        // ------------------------------------------------------------------

        public void SaveDownload(DownloadItem item, string? packageJson = null)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) return;
            lock (_lock)
            {
                if (_disposed) return;
                try
                {
                    var existing = _downloads.FindOne(x => x.Id == item.Id);
                    if (existing != null)
                    {
                        existing.FileName = item.FileName;
                        existing.Url = item.Url;
                        existing.SavePath = item.SavePath;
                        existing.CookieHeader = item.Status == DownloadStatus.Completed ? string.Empty : item.CookieHeader;
                        existing.Referer = item.Referer;
                        existing.UserAgent = item.UserAgent;
                        existing.Status = item.Status;
                        existing.Progress = item.Progress;
                        existing.Speed = item.Speed;
                        existing.DownloadedSize = item.DownloadedSize;
                        existing.TotalSize = item.TotalSize;
                        existing.ReceivedBytes = item.ReceivedBytes;
                        existing.TotalBytes = item.TotalBytes;
                        existing.CompletedAt = item.CompletedAt;
                        existing.ErrorMessage = item.ErrorMessage;
                        if (packageJson != null)
                        {
                            existing.PackageJson = packageJson.Length == 0 ? null : packageJson; // "" clears the saved resume data
                        }
                        _downloads.Update(existing);
                    }
                    else
                    {
                        _downloads.Insert(new DownloadEntry
                        {
                            Id = item.Id,
                            FileName = item.FileName,
                            Url = item.Url,
                            SavePath = item.SavePath,
                            CookieHeader = item.Status == DownloadStatus.Completed ? string.Empty : item.CookieHeader,
                            Referer = item.Referer,
                            UserAgent = item.UserAgent,
                            Type = item.Type,
                            Status = item.Status,
                            Progress = item.Progress,
                            Speed = item.Speed,
                            DownloadedSize = item.DownloadedSize,
                            TotalSize = item.TotalSize,
                            ReceivedBytes = item.ReceivedBytes,
                            TotalBytes = item.TotalBytes,
                            StartedAt = item.StartedAt,
                            CompletedAt = item.CompletedAt,
                            ErrorMessage = item.ErrorMessage,
                            PackageJson = packageJson
                        });
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.SaveDownload", ex);
                }
            }
        }

        public List<DownloadEntry> GetAllDownloads()
        {
            lock (_lock)
            {
                if (_disposed) return new List<DownloadEntry>();
                try
                {
                    return _downloads.Query().OrderByDescending(x => x.StartedAt).ToList();
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.GetAllDownloads", ex);
                    return new List<DownloadEntry>();
                }
            }
        }

        public string? GetDownloadPackageJson(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (_lock)
            {
                if (_disposed) return null;
                try
                {
                    var entry = _downloads.FindOne(x => x.Id == id);
                    return entry?.PackageJson;
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.GetDownloadPackageJson", ex);
                    return null;
                }
            }
        }

        public bool RemoveDownload(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            lock (_lock)
            {
                if (_disposed) return false;
                try
                {
                    return _downloads.DeleteMany(x => x.Id == id) > 0;
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.RemoveDownload", ex);
                    return false;
                }
            }
        }


        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                GC.SuppressFinalize(this);
                try
                {
                    _db.Dispose();
                }
                catch (Exception ex)
                {
                    AppLog.Error("DatabaseService.Dispose", ex);
                }
            }
        }
    }
}

