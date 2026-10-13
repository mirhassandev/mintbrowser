using System;
using System.IO;
using System.Text.Json;
using CustomBrowser.Helpers;

namespace CustomBrowser.Services
{
    /// <summary>Queue + speed-limit settings, persisted to %LocalAppData%\CustomBrowser\download-settings.json.</summary>
    public static class DownloadSettings
    {
        private sealed class SettingsFile
        {
            public int MaxConcurrent { get; set; } = 3;
            public long SpeedLimitKBps { get; set; }
        }

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
        private static int _maxConcurrent = 3;
        private static long _speedLimitKBps; // 0 = unlimited

        /// <summary>Raised after Apply(). The download service reacts by starting waiting items / re-splitting the speed cap.</summary>
        public static event Action? Changed;

        static DownloadSettings() { Load(); }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomBrowser", "download-settings.json");

        public static int MaxConcurrent { get { lock (Gate) return _maxConcurrent; } }
        public static long SpeedLimitKBps { get { lock (Gate) return _speedLimitKBps; } }
        public static long SpeedLimitBytesPerSecond => SpeedLimitKBps * 1024;

        public static void Apply(int maxConcurrent, long speedLimitKBps)
        {
            lock (Gate)
            {
                _maxConcurrent = Math.Clamp(maxConcurrent, 1, 16);
                _speedLimitKBps = Math.Clamp(speedLimitKBps, 0, 10_000_000);
            }
            Save();
            Changed?.Invoke();
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var s = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(FilePath));
                if (s == null) return;
                _maxConcurrent = Math.Clamp(s.MaxConcurrent, 1, 16);
                _speedLimitKBps = Math.Clamp(s.SpeedLimitKBps, 0, 10_000_000);
            }
            catch (Exception ex) { AppLog.Warn("Could not read download settings", ex); }
        }

        private static void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var s = new SettingsFile { MaxConcurrent = MaxConcurrent, SpeedLimitKBps = SpeedLimitKBps };
                File.WriteAllText(FilePath, JsonSerializer.Serialize(s, IndentedJson));
            }
            catch (Exception ex) { AppLog.Warn("Could not save download settings", ex); }
        }
    }
}
