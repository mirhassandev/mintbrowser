using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using CustomBrowser.Helpers;
using CustomBrowser.Models;
using CustomBrowser.Services;

namespace CustomBrowser
{
    /// <summary>
    /// Download feedback outside the downloads panel: the taskbar button shows overall progress, and flashes when a download
    /// finishes (or fails) while the browser is in the background.
    /// </summary>
    public partial class MainWindow
    {
        private readonly HashSet<DownloadItem> _watchedDownloads = new();
        private readonly Dictionary<string, DownloadStatus> _lastDownloadStatus = new();
        private DispatcherTimer? _taskbarTimer;
        private static MainWindow? _lastActiveWindow;   // only this window flashes, so several windows do not all flash at once
        private static string? _lastFlashKey;           // one flash per finished download, whichever window handles it

        private void InitDownloadFeedback()
        {
            TaskbarItemInfo = new TaskbarItemInfo();
            Activated += (_, _) => _lastActiveWindow = this;
            DownloadStore.Items.CollectionChanged += DownloadItems_CollectionChanged;
            foreach (DownloadItem item in DownloadStore.Items.ToList()) WatchDownload(item);
            UpdateTaskbarProgress();
        }

        private void ShutdownDownloadFeedback()
        {
            DownloadStore.Items.CollectionChanged -= DownloadItems_CollectionChanged;
            foreach (DownloadItem item in _watchedDownloads.ToList()) UnwatchDownload(item);
            _taskbarTimer?.Stop();
            if (ReferenceEquals(_lastActiveWindow, this)) _lastActiveWindow = null;
        }

        private void WatchDownload(DownloadItem item)
        {
            if (!_watchedDownloads.Add(item)) return;
            _lastDownloadStatus[item.Id] = item.Status;
            item.PropertyChanged += DownloadItem_PropertyChanged;
        }

        private void UnwatchDownload(DownloadItem item)
        {
            if (!_watchedDownloads.Remove(item)) return;
            item.PropertyChanged -= DownloadItem_PropertyChanged;
            _lastDownloadStatus.Remove(item.Id);
        }

        private void DownloadItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (DownloadItem item in _watchedDownloads.ToList()) UnwatchDownload(item);
                foreach (DownloadItem item in DownloadStore.Items.ToList()) WatchDownload(item);
            }
            else
            {
                if (e.OldItems != null) foreach (DownloadItem item in e.OldItems.OfType<DownloadItem>()) UnwatchDownload(item);
                if (e.NewItems != null) foreach (DownloadItem item in e.NewItems.OfType<DownloadItem>()) WatchDownload(item);
            }
            EnsureTaskbarTimer();
            UpdateTaskbarProgress();
        }

        // Downloads report from worker threads: only the status is of interest here, and it is handled on the UI thread.
        private void DownloadItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DownloadItem.Status) || sender is not DownloadItem item) return;
            Dispatcher.BeginInvoke(new Action(() => OnDownloadStatusChanged(item)), DispatcherPriority.Background);
        }

        private void OnDownloadStatusChanged(DownloadItem item)
        {
            _lastDownloadStatus.TryGetValue(item.Id, out DownloadStatus previous);
            bool known = _lastDownloadStatus.ContainsKey(item.Id);
            _lastDownloadStatus[item.Id] = item.Status;

            EnsureTaskbarTimer();
            UpdateTaskbarProgress();

            // Only a download that was really running counts: finished ones loaded from disk at startup must not flash.
            if (known && previous == DownloadStatus.Downloading &&
                (item.Status == DownloadStatus.Completed || item.Status == DownloadStatus.Failed))
            {
                NotifyDownloadFinished(item);
            }
        }

        private void NotifyDownloadFinished(DownloadItem item)
        {
            if (IsActive) return; // the user is looking at the browser already
            if (_lastActiveWindow != null && _lastActiveWindow.IsLoaded && !ReferenceEquals(_lastActiveWindow, this)) return;
            string key = item.Id + ":" + item.Status;
            if (_lastFlashKey == key) return;
            _lastFlashKey = key;
            TaskbarFlash.Flash(this);
        }

        private static bool IsActiveDownload(DownloadItem d) =>
            d.Status == DownloadStatus.Downloading || d.Status == DownloadStatus.Queued;

        private void EnsureTaskbarTimer()
        {
            if (!DownloadStore.Items.Any(IsActiveDownload))
            {
                _taskbarTimer?.Stop();
                return;
            }
            if (_taskbarTimer == null)
            {
                _taskbarTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
                _taskbarTimer.Tick += (_, _) =>
                {
                    UpdateTaskbarProgress();
                    if (!DownloadStore.Items.Any(IsActiveDownload)) _taskbarTimer?.Stop();
                };
            }
            if (!_taskbarTimer.IsEnabled) _taskbarTimer.Start();
        }

        /// <summary>One bar for all downloads: bytes received so far out of the bytes expected, over every download that knows its size.</summary>
        private void UpdateTaskbarProgress()
        {
            if (TaskbarItemInfo == null) return;
            long received = 0, total = 0;
            int running = 0, paused = 0;
            foreach (DownloadItem d in DownloadStore.Items)
            {
                bool isRunning = IsActiveDownload(d);
                bool isPaused = d.Status == DownloadStatus.Paused;
                if (!isRunning && !isPaused) continue;
                if (isRunning) running++; else paused++;
                if (d.TotalBytes > 0)
                {
                    received += Math.Min(d.ReceivedBytes, d.TotalBytes);
                    total += d.TotalBytes;
                }
            }

            if (running == 0 && paused == 0)
            {
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
                return;
            }
            if (total <= 0)
            {
                TaskbarItemInfo.ProgressState = running > 0 ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.Paused;
                return;
            }
            TaskbarItemInfo.ProgressValue = Math.Clamp(received / (double)total, 0, 1);
            TaskbarItemInfo.ProgressState = running > 0 ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Paused;
        }
    }
}
