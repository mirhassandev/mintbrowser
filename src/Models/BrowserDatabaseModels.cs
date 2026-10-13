
using System;
using System.ComponentModel;
using System.Collections.Generic;

namespace CustomBrowser.Models
{
    /// <summary>
    /// Represents a visited webpage record in the Places history database,
    /// tracking visits and Frecency (Frequency + Recency) score matching Firefox architecture.
    /// </summary>
    public class HistoryEntry
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string FaviconUrl { get; set; } = string.Empty;
        public int VisitCount { get; set; } = 1;
        public DateTime VisitedAt { get; set; } = DateTime.Now;
        public DateTime FirstVisitedAt { get; set; } = DateTime.Now;
        public int Frecency { get; set; } = 100;
        public bool IsHidden { get; set; } // For dismissed Top Sites

        public string Host
        {
            get
            {
                if (Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                {
                    string h = uri.Host;
                    return h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h.Substring(4) : h;
                }
                return Url;
            }
        }

    }

    /// <summary>
    /// Group of history items for Firefox sidebar date/site grouping.
    /// </summary>
    public class HistoryGroup : INotifyPropertyChanged
    {
        private bool _isExpanded = true;

        public string Header { get; set; } = string.Empty;
        public List<HistoryEntry> Items { get; set; } = new();

        /// <summary>Raises PropertyChanged so the sidebar can collapse / expand the group live.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Represents a saved bookmark record in the Places bookmarks collection.
    /// </summary>
    public class BookmarkEntry
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string FaviconUrl { get; set; } = string.Empty;
        public string Folder { get; set; } = "Bookmarks Toolbar";
        public string Tags { get; set; } = string.Empty;
        public DateTime AddedAt { get; set; } = DateTime.Now;
        public DateTime LastModifiedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Represents a persisted download record in the LiteDB database,
    /// enabling IDM-style persistence across browser restarts, resuming paused/interrupted downloads,
    /// and tracking complete download history.
    /// </summary>
    public class DownloadEntry
    {
        public string Id { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string SavePath { get; set; } = string.Empty;
        public string CookieHeader { get; set; } = string.Empty;
        public string Referer { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public DownloadType Type { get; set; } = DownloadType.Direct;
        public DownloadStatus Status { get; set; } = DownloadStatus.Paused;
        public double Progress { get; set; }
        public string Speed { get; set; } = "-";
        public string DownloadedSize { get; set; } = "0 MB";
        public string TotalSize { get; set; } = "Unknown";
        public long ReceivedBytes { get; set; }
        public long TotalBytes { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public DateTime? CompletedAt { get; set; }
        public string? PackageJson { get; set; }
    }
}


