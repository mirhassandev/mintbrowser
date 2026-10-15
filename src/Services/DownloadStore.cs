using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using CustomBrowser.Helpers;
using CustomBrowser.Models;

namespace CustomBrowser.Services
{
    /// <summary>
    /// The one shared list of downloads and the download service (toolbar dropdown + the Downloads tab both use it).
    /// All download records, progress, and chunk metadata are persisted in the Places database (MintPlaces.db).
    /// </summary>
    public static class DownloadStore
    {
        public static ObservableCollection<DownloadItem> Items { get; } = new ObservableCollection<DownloadItem>();
        public static DirectDownloadService Service { get; } = new DirectDownloadService();

        private sealed class DownloadRecord
        {
            public string Id { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string SavePath { get; set; } = string.Empty;
            public string Referer { get; set; } = string.Empty;
            public string UserAgent { get; set; } = string.Empty;
            public string Status { get; set; } = nameof(DownloadStatus.Paused);
            public long ReceivedBytes { get; set; }
            public long TotalBytes { get; set; }
            public double Progress { get; set; }
            public DateTime StartedAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public string ErrorMessage { get; set; } = string.Empty;
        }

        private static DispatcherTimer? _saveTimer;
        private static volatile bool _dirty;
        private static bool _loaded;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomBrowser", "downloads.json");

        /// <summary>Call once at startup, on the UI thread, before any window is created.</summary>
        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                var dbEntries = DatabaseService.Instance.GetAllDownloads();
                if (dbEntries.Count > 0)
                {
                    foreach (var entry in dbEntries)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Url) || string.IsNullOrWhiteSpace(entry.SavePath)) continue;
                        Items.Add(ToItem(entry));
                    }
                    AppLog.Info($"Restored {Items.Count} download(s) from Places database");
                }
                else if (File.Exists(FilePath))
                {
                    // Fallback migration from legacy downloads.json
                    var records = JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(FilePath)) ?? new List<DownloadRecord>();
                    foreach (DownloadRecord r in records)
                    {
                        if (string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Url) || string.IsNullOrWhiteSpace(r.SavePath)) continue;
                        var itm = ToItem(r);
                        Items.Add(itm);
                        DatabaseService.Instance.SaveDownload(itm);
                    }
                    AppLog.Info($"Migrated {Items.Count} download(s) from legacy json into database");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not restore the download list", ex);
            }

            RetireLegacyJson();

            foreach (DownloadItem item in Items) item.PropertyChanged += OnItemChanged;
            Items.CollectionChanged += OnItemsChanged;

            // Progress changes constantly, so changes are written in batches (at most once every 3 seconds).
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
            _saveTimer.Tick += (_, _) => { if (_dirty) SaveNow(); };
            _saveTimer.Start();
        }

        private static DownloadItem ToItem(DownloadEntry entry)
        {
            var item = new DownloadItem
            {
                Id = entry.Id,
                FileName = entry.FileName,
                Url = entry.Url,
                SavePath = entry.SavePath,
                CookieHeader = SecretProtector.Unprotect(entry.CookieHeader), // stored protected; the item holds the plain header
                Referer = entry.Referer,
                UserAgent = entry.UserAgent,
                StartedAt = entry.StartedAt,
                ReceivedBytes = entry.ReceivedBytes,
                TotalBytes = entry.TotalBytes,
                Progress = entry.Progress,
                ErrorMessage = entry.ErrorMessage,
                Type = entry.Type,
                Speed = "-"
            };

            // Anything that was downloading or queued when the browser was closed is restored as Paused with progress intact
            var status = entry.Status;
            if (status == DownloadStatus.Queued || status == DownloadStatus.Downloading)
            {
                status = DownloadStatus.Paused;
            }
            item.Status = status;
            if (status == DownloadStatus.Completed)
            {
                item.CompletedAt = entry.CompletedAt ?? entry.StartedAt;
            }
            return item;
        }

        private static DownloadItem ToItem(DownloadRecord r)
        {
            var item = new DownloadItem
            {
                Id = r.Id,
                FileName = r.FileName,
                Url = r.Url,
                SavePath = r.SavePath,
                Referer = r.Referer,
                UserAgent = r.UserAgent,
                StartedAt = r.StartedAt,
                ReceivedBytes = r.ReceivedBytes,
                TotalBytes = r.TotalBytes,
                Progress = r.Progress,
                ErrorMessage = r.ErrorMessage,
                Type = DownloadType.Direct,
                Speed = "-"
            };

            DownloadStatus status = Enum.TryParse(r.Status, out DownloadStatus parsed) ? parsed : DownloadStatus.Paused;
            if (status == DownloadStatus.Queued || status == DownloadStatus.Downloading) status = DownloadStatus.Paused;
            item.Status = status;
            if (status == DownloadStatus.Completed) item.CompletedAt = r.CompletedAt ?? r.StartedAt;
            return item;
        }

        private static void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (DownloadItem item in e.OldItems)
                {
                    item.PropertyChanged -= OnItemChanged;
                    DatabaseService.Instance.RemoveDownload(item.Id);
                }
            }
            if (e.NewItems != null)
            {
                foreach (DownloadItem item in e.NewItems)
                {
                    item.PropertyChanged += OnItemChanged;
                    DatabaseService.Instance.SaveDownload(item);
                }
            }
            _dirty = true;
        }

        private static void OnItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(DownloadItem.Status):
                case nameof(DownloadItem.FileName):
                case nameof(DownloadItem.Url):
                case nameof(DownloadItem.SavePath):
                case nameof(DownloadItem.ErrorMessage):
                case nameof(DownloadItem.ReceivedBytes):
                case nameof(DownloadItem.TotalBytes):
                case nameof(DownloadItem.Progress):
                    _dirty = true;
                    if (sender is DownloadItem itm && e.PropertyName == nameof(DownloadItem.Status))
                    {
                        // Immediately persist important state transitions (Paused, Completed, Failed)
                        DatabaseService.Instance.SaveDownload(itm);
                    }
                    break;
            }
        }

        /// <summary>
        /// Marks a download whose file is complete on disk as Completed: takes the real size from the file, stamps the Mark of the Web
        /// (before the status flips, so the UI never offers "Open" for an unmarked file) and saves the record. Its resume data is no longer needed.
        /// </summary>
        internal static void CompleteFromDisk(DownloadItem item)
        {
            try
            {
                if (File.Exists(item.SavePath))
                {
                    long length = new FileInfo(item.SavePath).Length;
                    item.ReceivedBytes = length;
                    item.TotalBytes = length;
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not read size of finished download", ex);
            }
            item.Speed = "-";
            item.Progress = 100;
            MarkOfTheWeb.Apply(item.SavePath, item.Url, item.Referer);
            item.Status = DownloadStatus.Completed;
            DatabaseService.Instance.SaveDownload(item, packageJson: string.Empty);
        }

        /// <summary>Saves the in-progress downloads to the database now. Must run on the UI thread (it reads the collection).</summary>
        public static void SaveNow()
        {
            if (!_loaded) return;
            try
            {
                _dirty = false;
                foreach (var item in Items.Where(d => d.Status == DownloadStatus.Downloading || d.Status == DownloadStatus.Queued).ToList())
                {
                    DatabaseService.Instance.SaveDownload(item);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not save the download list", ex);
            }
        }

        /// <summary>downloads.json was the old storage. The database is the only source now, so the file is renamed once
        /// and never read again; otherwise a stale copy could bring deleted downloads back.</summary>
        private static void RetireLegacyJson()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                File.Move(FilePath, FilePath + ".migrated", overwrite: true);
            }
            catch (Exception ex) { AppLog.Debug("Could not retire legacy downloads.json", ex); }
        }
    }
}



