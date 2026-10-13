using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CustomBrowser.Helpers;

namespace CustomBrowser.Models
{
    public class DownloadItem : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        private string _fileName = string.Empty;
        private string _url = string.Empty;
        private string _savePath = string.Empty;
        private double _progress;
        private string _speed = "0 KB/s";
        private string _downloadedSize = "0 MB";
        private string _totalSize = "Unknown";
        private long _receivedBytes;
        private long _totalBytes;
        private DateTime _startedAt = DateTime.Now;
        private DownloadType _type;
        private DownloadStatus _status = DownloadStatus.Queued;
        private string _cookieHeader = string.Empty;
        private int _queuePosition;

        /// <summary>1-based place in the waiting queue; 0 when the item is not waiting.</summary>
        public int QueuePosition { get => _queuePosition; set => SetField(ref _queuePosition, value); }

        public string Id { get => _id; set => SetField(ref _id, value); }
        public string FileName { get => _fileName; set => SetField(ref _fileName, value); }
        public string Url { get => _url; set => SetField(ref _url, value); }
        public string SavePath { get => _savePath; set => SetField(ref _savePath, value); }
        public string CookieHeader { get => _cookieHeader; set => SetField(ref _cookieHeader, value); }
        /// <summary>Page the download was started from (sent as Referer so sites that check it do not answer 403).</summary>
        public string Referer { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        private string _errorMessage = string.Empty;
        /// <summary>Short reason shown when a download fails.</summary>
        public string ErrorMessage { get => _errorMessage; set => SetField(ref _errorMessage, value); }
        public double Progress { get => _progress; set => SetField(ref _progress, value); }
        public string Speed { get => _speed; set => SetField(ref _speed, value); }
        public string DownloadedSize { get => _downloadedSize; set => SetField(ref _downloadedSize, value); }
        public string TotalSize { get => _totalSize; set => SetField(ref _totalSize, value); }
        public long ReceivedBytes { get => _receivedBytes; set => SetField(ref _receivedBytes, value); }
        public long TotalBytes { get => _totalBytes; set => SetField(ref _totalBytes, value); }
        public DateTime StartedAt { get => _startedAt; set => SetField(ref _startedAt, value); }
        public DateTime? CompletedAt { get; set; }
        public DownloadType Type { get => _type; set => SetField(ref _type, value); }

        public string Category
        {
            get
            {
                string ext = Path.GetExtension(FileName).TrimStart('.').ToLowerInvariant();
                string[] archives = { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "tgz", "7zip" };
                string[] apps = { "exe", "msi", "msix", "appx", "bat", "cmd", "apk", "dmg", "deb", "rpm", "bin" };
                string[] docs = { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "csv", "epub" };
                string[] audio = { "mp3", "wav", "flac", "m4a", "ogg", "aac", "wma", "opus" };
                string[] video = { "mp4", "mkv", "avi", "mov", "webm", "wmv", "flv", "m4v", "3gp" };
                string[] images = { "png", "jpg", "jpeg", "gif", "webp", "svg", "bmp", "ico", "tiff" };

                if (Array.IndexOf(archives, ext) >= 0) return "archive";
                if (Array.IndexOf(apps, ext) >= 0) return "app";
                if (Array.IndexOf(docs, ext) >= 0) return "doc";
                if (Array.IndexOf(audio, ext) >= 0) return "audio";
                if (Array.IndexOf(video, ext) >= 0) return "video";
                if (Array.IndexOf(images, ext) >= 0) return "image";
                return "other";
            }
        }

        public DownloadStatus Status
        {
            get => _status;
            set
            {
                // Stamp the time BEFORE the change notification so the status text already shows it.
                if (value == DownloadStatus.Completed && _status != DownloadStatus.Completed) CompletedAt = DateTime.Now;
                SetField(ref _status, value);
            }
        }

        // ---- Derived values used by the downloads dropdown ----
        public bool ShowProgress => Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused;
        public bool IsPaused => Status == DownloadStatus.Paused;
        public bool IsDownloading => Status == DownloadStatus.Downloading;
        public bool IsCompleted => Status == DownloadStatus.Completed;
        public bool IsFailed => Status == DownloadStatus.Failed;

        /// <summary>
        /// The icon Windows shows for this file: the real icon of a finished program, the file-type icon otherwise.
        /// Null only when the shell cannot provide one (the dropdown then shows a plain document outline).
        /// </summary>
        public ImageSource? Icon
        {
            get
            {
                string? finished = Status == DownloadStatus.Completed && !string.IsNullOrEmpty(SavePath) && FileExistsCached(SavePath)
                    ? SavePath : null;
                return ShellIcon.GetImageSourceForFile(FileName, finished);
            }
        }

        public bool HasIcon => Icon != null;

        /// <summary>Re-reads the icon (the file name changed, the program finished downloading, or the dropdown was opened).</summary>
        public void RefreshIcon()
        {
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(HasIcon));
        }

        /// <summary>A just-finished program can be locked for a moment (antivirus scan), so its own icon is asked for once more shortly after.</summary>
        private static async void ScheduleIconRefresh(DownloadItem item)
        {
            try
            {
                await Task.Delay(1500);
                Application.Current?.Dispatcher.BeginInvoke(new Action(item.RefreshIcon));
            }
            catch (Exception)
            {
                // Cosmetic only: a missed refresh just means the icon updates the next time the dropdown opens.
            }
        }

        /// <summary>One-line summary, e.g. "43.9 KB - 13 minutes ago" or "3.1 MB of 12.0 MB - 1.20 MB/s".</summary>
        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case DownloadStatus.Queued:
                        return QueuePosition > 0 ? $"Waiting in queue (#{QueuePosition})" : "Starting...";
                    case DownloadStatus.Downloading:
                    {
                        string done = SizeText(ReceivedBytes, DownloadedSize);
                        string speed = string.IsNullOrWhiteSpace(Speed) ? string.Empty : $" - {Speed}";
                        return TotalBytes > 0
                            ? $"{done} of {SizeText(TotalBytes, TotalSize)}{speed}"
                            : $"{done}{speed}";
                    }
                    case DownloadStatus.Paused:
                        return $"Paused - {SizeText(ReceivedBytes, DownloadedSize)}";
                    case DownloadStatus.Failed:
                        return string.IsNullOrWhiteSpace(ErrorMessage) ? "Failed" : $"Failed - {ErrorMessage}";
                    case DownloadStatus.Completed:
                    {
                        if (!string.IsNullOrEmpty(SavePath) && !FileExistsCached(SavePath)) return "File deleted";
                        long size = TotalBytes > 0 ? TotalBytes : ReceivedBytes;
                        return $"{SizeText(size, TotalSize)} - {FormatAgo(CompletedAt ?? StartedAt)}";
                    }
                    default:
                        return string.Empty;
                }
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Exists, long Tick)> FileExistsCache = new();

        /// <summary>File.Exists, remembered for 10 seconds per path so UI bindings do not hit the disk on every refresh.</summary>
        private static bool FileExistsCached(string path)
        {
            long now = Environment.TickCount64;
            if (FileExistsCache.TryGetValue(path, out var hit) && now - hit.Tick < 10000) return hit.Exists;
            if (FileExistsCache.Count > 2000) FileExistsCache.Clear();
            bool exists = File.Exists(path);
            FileExistsCache[path] = (exists, now);
            return exists;
        }

        /// <summary>Re-evaluates the text (the "x minutes ago" part changes with time, "File deleted" with the disk).</summary>
        public void RefreshStatusText() => OnPropertyChanged(nameof(StatusText));

        private static string SizeText(long bytes, string fallback) => bytes > 0 ? FormatBytes(bytes) : fallback;

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
        }

        private static string FormatAgo(DateTime when)
        {
            TimeSpan span = DateTime.Now - when;
            if (span.TotalSeconds < 60) return "Just now";
            if (span.TotalMinutes < 60) { int m = (int)span.TotalMinutes; return m == 1 ? "1 minute ago" : $"{m} minutes ago"; }
            if (span.TotalHours < 24 && when.Date == DateTime.Today) { int h = (int)span.TotalHours; return h == 1 ? "1 hour ago" : $"{h} hours ago"; }
            if (when.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
            return when.ToString(when.Year == DateTime.Today.Year ? "MMM d" : "MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            if (propertyName is nameof(FileName) or nameof(SavePath) or nameof(Status)) RefreshIcon();
            if (propertyName == nameof(Status) && _status == DownloadStatus.Completed) ScheduleIconRefresh(this);
            switch (propertyName)
            {
                case nameof(Status):
                    OnPropertyChanged(nameof(ShowProgress));
                    OnPropertyChanged(nameof(IsCompleted));
                    OnPropertyChanged(nameof(IsPaused));
                    OnPropertyChanged(nameof(IsDownloading));
                    OnPropertyChanged(nameof(IsFailed));
                    OnPropertyChanged(nameof(StatusText));
                    break;
                case nameof(QueuePosition):
                case nameof(Speed):
                case nameof(DownloadedSize):
                case nameof(TotalSize):
                case nameof(ReceivedBytes):
                case nameof(TotalBytes):
                case nameof(SavePath):
                case nameof(ErrorMessage):
                case nameof(StartedAt):
                    OnPropertyChanged(nameof(StatusText));
                    break;
            }
            return true;
        }
    }
}

