using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CustomBrowser.Helpers;

namespace CustomBrowser.Services
{
    /// <summary>
    /// Small user choices that must survive a restart and be the same in every window: the selected search engine and the
    /// recent searches. Stored as JSON. Safe to call from any thread.
    /// </summary>
    public sealed class BrowserSettingsStore
    {
        /// <summary>How many recent searches are kept.</summary>
        public const int MaxRecentSearches = 30;

        private sealed class FileModel
        {
            public string SearchEngine { get; set; } = string.Empty;
            public List<string> RecentSearches { get; set; } = new();
            public string HistorySort { get; set; } = "Date";
            public double SidebarWidth { get; set; } = 300;
        }

        private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
        private readonly object _gate = new();
        private readonly string? _path;
        private readonly FileModel _data;

        /// <summary>Raised (on the calling thread) after the selected search engine changed.</summary>
        public event Action? SearchEngineChanged;

        /// <param name="filePath">JSON file to use, or null to keep everything in memory only.</param>
        public BrowserSettingsStore(string? filePath)
        {
            _path = filePath;
            _data = Load();
        }

        /// <summary>Name of the selected search engine, or empty for the default (Google).</summary>
        public string SearchEngineName
        {
            get { lock (_gate) return _data.SearchEngine; }
        }

        public void SetSearchEngine(string? name)
        {
            name ??= string.Empty;
            bool changed;
            lock (_gate)
            {
                changed = !string.Equals(_data.SearchEngine, name, StringComparison.Ordinal);
                if (changed)
                {
                    _data.SearchEngine = name;
                    Save();
                }
            }
            if (changed) SearchEngineChanged?.Invoke();
        }

        /// <summary>Recent searches, oldest first (a copy).</summary>
        public List<string> GetRecentSearches()
        {
            lock (_gate) return new List<string>(_data.RecentSearches);
        }

        public void AddRecentSearch(string? query)
        {
            string normalized = System.Text.RegularExpressions.Regex.Replace(query ?? string.Empty, @"\s+", " ").Trim();
            if (normalized.Length < 2) return;
            lock (_gate)
            {
                _data.RecentSearches.RemoveAll(x => x.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                _data.RecentSearches.Add(normalized);
                while (_data.RecentSearches.Count > MaxRecentSearches) _data.RecentSearches.RemoveAt(0);
                Save();
            }
        }

        public void ClearRecentSearches()
        {
            lock (_gate)
            {
                if (_data.RecentSearches.Count == 0) return;
                _data.RecentSearches.Clear();
                Save();
            }
        }

        /// <summary>How the history sidebar is sorted (Date, Site, LastVisited, MostVisited). New windows start with the last choice.</summary>
        public string HistorySort
        {
            get { lock (_gate) return _data.HistorySort; }
        }

        public void SetHistorySort(string? sort)
        {
            if (string.IsNullOrWhiteSpace(sort)) return;
            lock (_gate)
            {
                if (_data.HistorySort == sort) return;
                _data.HistorySort = sort;
                Save();
            }
        }

        /// <summary>Width of the sidebar the last time it was closed, kept between 240 and 600.</summary>
        public double SidebarWidth
        {
            get { lock (_gate) return _data.SidebarWidth; }
        }

        public void SetSidebarWidth(double width)
        {
            if (double.IsNaN(width) || double.IsInfinity(width)) return;
            width = Math.Clamp(width, 240, 600);
            lock (_gate)
            {
                if (Math.Abs(_data.SidebarWidth - width) < 1) return;
                _data.SidebarWidth = width;
                Save();
            }
        }

        private FileModel Load()
        {
            if (_path == null) return new FileModel();
            try
            {
                if (File.Exists(_path))
                {
                    FileModel? model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path));
                    if (model != null)
                    {
                        model.SearchEngine ??= string.Empty;
                        if (string.IsNullOrWhiteSpace(model.HistorySort)) model.HistorySort = "Date";
                        model.SidebarWidth = model.SidebarWidth is >= 240 and <= 600 ? model.SidebarWidth : 300;
                        model.RecentSearches = (model.RecentSearches ?? new List<string>())
                            .Where(s => !string.IsNullOrWhiteSpace(s)).TakeLast(MaxRecentSearches).ToList();
                        return model;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not read browser-settings.json - starting with defaults", ex);
            }
            return new FileModel();
        }

        /// <summary>Must be called with the gate held.</summary>
        private void Save()
        {
            if (_path == null) return;
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_data, IndentedJson));
                File.Move(tmp, _path, overwrite: true); // a crash mid-write can no longer truncate the file
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not save browser-settings.json", ex);
            }
        }
    }

    /// <summary>The app-wide settings, shared by every window: %LocalAppData%\CustomBrowser\browser-settings.json.</summary>
    public static class BrowserSettings
    {
        private static readonly BrowserSettingsStore Store = new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomBrowser", "browser-settings.json"));

        public static event Action? SearchEngineChanged
        {
            add => Store.SearchEngineChanged += value;
            remove => Store.SearchEngineChanged -= value;
        }

        public static string SearchEngineName => Store.SearchEngineName;
        public static void SetSearchEngine(string? name) => Store.SetSearchEngine(name);
        public static List<string> GetRecentSearches() => Store.GetRecentSearches();
        public static void AddRecentSearch(string? query) => Store.AddRecentSearch(query);
        public static void ClearRecentSearches() => Store.ClearRecentSearches();
        public static string HistorySort => Store.HistorySort;
        public static void SetHistorySort(string? sort) => Store.SetHistorySort(sort);
        public static double SidebarWidth => Store.SidebarWidth;
        public static void SetSidebarWidth(double width) => Store.SetSidebarWidth(width);
    }
}


