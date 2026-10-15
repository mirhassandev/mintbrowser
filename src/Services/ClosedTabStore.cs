using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CustomBrowser.Helpers;

namespace CustomBrowser.Services
{
    /// <summary>
    /// The addresses of recently closed tabs for "Reopen closed tab" (Ctrl+Shift+T). Newest is reopened first.
    /// Capped, and saved to disk so it also works after the window (or the browser) was closed. Safe to call from any thread.
    /// </summary>
    public sealed class ClosedTabList
    {
        /// <summary>How many closed tabs are remembered.</summary>
        public const int MaxItems = 10;

        private readonly object _gate = new();
        private readonly string? _path;
        private readonly List<string> _urls; // oldest first

        /// <param name="filePath">JSON file to use, or null to keep everything in memory only.</param>
        public ClosedTabList(string? filePath)
        {
            _path = filePath;
            _urls = Load();
        }

        public int Count
        {
            get { lock (_gate) return _urls.Count; }
        }

        public void Push(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            lock (_gate)
            {
                _urls.RemoveAll(x => string.Equals(x, url, StringComparison.OrdinalIgnoreCase)); // one entry per address
                _urls.Add(url);
                while (_urls.Count > MaxItems) _urls.RemoveAt(0);
                Save();
            }
        }

        /// <summary>Removes and returns the most recently closed address, or null when there is none.</summary>
        public string? Pop()
        {
            lock (_gate)
            {
                if (_urls.Count == 0) return null;
                string url = _urls[^1];
                _urls.RemoveAt(_urls.Count - 1);
                Save();
                return url;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                if (_urls.Count == 0) return;
                _urls.Clear();
                Save();
            }
        }

        private List<string> Load()
        {
            if (_path == null) return new List<string>();
            try
            {
                if (File.Exists(_path))
                {
                    List<string>? list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path));
                    if (list != null) return list.Where(s => !string.IsNullOrWhiteSpace(s)).TakeLast(MaxItems).ToList();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not read closed-tabs.json - starting empty", ex);
            }
            return new List<string>();
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
                File.WriteAllText(tmp, JsonSerializer.Serialize(_urls));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not save closed-tabs.json", ex);
            }
        }
    }

    /// <summary>The closed tabs shared by every window: %LocalAppData%\CustomBrowser\closed-tabs.json.</summary>
    public static class ClosedTabStore
    {
        private static readonly ClosedTabList List = new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomBrowser", "closed-tabs.json"));

        public static void Push(string? url) => List.Push(url);
        public static string? Pop() => List.Pop();
        public static void Clear() => List.Clear();
    }
}
