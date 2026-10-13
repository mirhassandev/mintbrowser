using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using CustomBrowser.Helpers;
using CustomBrowser.Views;
using CustomBrowser.Models;
using CustomBrowser.Services;
namespace CustomBrowser
{
    public partial class MainWindow
    {
        // ------------------------------------------------------------------
        // Downloads
        // ------------------------------------------------------------------
        // async void is required for the event signature, so ALL work is delegated to a Task method
        // and every exception is caught here (an unhandled one would crash the whole app).
        private async void CoreWebView2_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            try
            {
                await HandleDownloadStartingAsync(sender, e);
            }
            catch (Exception ex)
            {
                AppLog.Error("Download interception failed", ex);
            }
        }
        /// <summary>How long the browser's own copy of a download stays open while our downloader tries to take over.</summary>
        private const int DownloaderStartWatchMs = 15000;

        /// <summary>Downloads the browser itself is transferring because our downloader was refused. Id -> WebView2 operation.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CoreWebView2DownloadOperation> BrowserDownloads = new();

        private async Task HandleDownloadStartingAsync(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            // Everything that touches 'e' must happen BEFORE the first await, or while the deferral below is held.
            string downloadUrl = e.DownloadOperation.Uri;
            string defaultPath = e.ResultFilePath ?? string.Empty;
            // Our own service can only fetch http(s) URLs. blob:/data: downloads only exist inside
            // the browser, so let WebView2 handle those itself.
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }
            // The deferral keeps this event open across the awaits below. That lets "our downloader or the browser" be decided
            // once we know whether the server accepted OUR request: sites that hand out signed / one-time links (e.g. download.it)
            // answer 403 to a second client, and then the transfer the browser already holds is the only one that works.
            CoreWebView2Deferral deferral = e.GetDeferral();
            try
            {
                CoreWebView2DownloadOperation browserOperation = e.DownloadOperation;
                // Handled only hides the default UI - WebView2 would STILL download the file itself.
                // Cancel makes our download service the only one doing the work. It stays set unless our service
                // is refused (see AdoptBrowserDownload), so any unexpected error below still ends with the old behaviour.
                e.Cancel = true;
                e.Handled = true;
                string suggestedFileName = SanitizeFileName(Path.GetFileName(defaultPath));
                if (string.IsNullOrEmpty(suggestedFileName))
                {
                    suggestedFileName = "download_" + DateTime.Now.Ticks;
                }
                string downloadsFolder = KnownFolders.GetDownloadsFolder();
                Directory.CreateDirectory(downloadsFolder);
                string cookieHeader = "";
                string referer = string.Empty;
                string userAgent = string.Empty;
                if (sender is CoreWebView2 originCore)
                {
                    try
                    {
                        referer = originCore.Source ?? string.Empty;
                        userAgent = originCore.Settings.UserAgent ?? string.Empty;
                    }
                    catch (Exception refEx) { AppLog.Debug("Could not read referer / user agent for download", refEx); }
                    if (!referer.StartsWith("http", StringComparison.OrdinalIgnoreCase)) referer = string.Empty;
                    try
                    {
                        cookieHeader = await CookieManagerService.GetCookieHeaderAsync(originCore, downloadUrl);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Could not read cookies for download - continuing without them", ex);
                    }
                }
                // Pick the unique file name only now, after the last await, and insert the item right away.
                // (Picked earlier, two same-name downloads that overlapped during the cookie lookup got the same path.)
                string savePath = GetUniqueFilePath(downloadsFolder, suggestedFileName);
                var item = new DownloadItem
                {
                    FileName = Path.GetFileName(savePath),
                    Url = downloadUrl,
                    SavePath = savePath,
                    Type = DownloadType.Direct,
                    Referer = referer,
                    UserAgent = userAgent,
                    Status = DownloadStatus.Queued
                };
                DownloadStore.Items.Insert(0, item); // newest first (dropdown + Download Manager)
                // Like Chrome: the dropdown opens when a download starts - except on the Downloads tab, which already shows it.
                MainWindow downloadWindow = GetWindowForCore(sender);
                if (!IsDownloadsPage(downloadWindow.SelectedTab?.Url)) downloadWindow.ShowDownloadsPopup();
                // The queue decides when this really starts (max simultaneous downloads + speed limit).
                Task<int> started = _directDownloadService.WatchStart(item.Id);
                _directDownloadService.Enqueue(item, cookieHeader);

                int refusedWith = await WaitForDownloaderStartAsync(started, item);
                if (refusedWith != 0)
                {
                    // Refused (403 / 401 / 410 ...) or could not start: finish the transfer the browser already holds.
                    // The blank popup tab hosting that transfer is closed when it ends, not now.
                    AdoptBrowserDownload(item, browserOperation, e, sender, refusedWith);
                    return;
                }
            }
            finally
            {
                deferral.Complete();
            }
            // The link may have opened a blank popup tab only to trigger this download: close it again.
            CloseSpawnedBlankTab(sender);
        }

        /// <summary>
        /// 0 = our downloader is receiving data (or the answer did not come in time: it stays in charge);
        /// otherwise the HTTP status our downloader was refused with (-1 = it failed for another reason).
        /// </summary>
        private static async Task<int> WaitForDownloaderStartAsync(Task<int> started, DownloadItem item)
        {
            // Waiting behind other downloads for a free slot can take arbitrarily long: keep our downloader in charge.
            if (item.Status == DownloadStatus.Queued && item.QueuePosition > 0) return 0;
            Task winner = await Task.WhenAny(started, Task.Delay(DownloaderStartWatchMs));
            return ReferenceEquals(winner, started) ? await started : 0;
        }

        /// <summary>
        /// Our downloader was refused, but the browser holds an open, already accepted transfer of the same file.
        /// Let the browser finish that one into the item's path and mirror its progress into the download list.
        /// </summary>
        private static void AdoptBrowserDownload(DownloadItem item, CoreWebView2DownloadOperation op, CoreWebView2DownloadStartingEventArgs e, object? sender, int refusedWith)
        {
            e.Cancel = false;
            e.ResultFilePath = item.SavePath;
            e.Handled = true; // still no default download UI: the list in this browser shows it
            AppLog.Info($"Our download service was refused (status {refusedWith}); the browser finishes this download itself.");

            BrowserDownloads[item.Id] = op;
            item.ErrorMessage = string.Empty;
            item.Speed = "-";
            item.Status = DownloadStatus.Downloading;

            long lastBytes = 0;
            long lastTick = Environment.TickCount64;
            bool IsCurrentOperation() => BrowserDownloads.TryGetValue(item.Id, out CoreWebView2DownloadOperation? current) && ReferenceEquals(current, op);

            op.BytesReceivedChanged += (_, _) =>
            {
                if (!IsCurrentOperation() || item.Status == DownloadStatus.Paused) return;
                long received = op.BytesReceived;
                ulong? expected = op.TotalBytesToReceive;
                long total = expected.HasValue && expected.Value <= long.MaxValue ? (long)expected.Value : 0;
                item.ReceivedBytes = received;
                item.TotalBytes = total > 0 ? total : 0;
                item.Progress = total > 0 ? Math.Min(100.0, received * 100.0 / total) : 0;
                item.DownloadedSize = $"{received / 1024.0 / 1024.0:F2} MB";
                item.TotalSize = total > 0 ? $"{total / 1024.0 / 1024.0:F2} MB" : "Unknown";
                long now = Environment.TickCount64;
                if (now - lastTick >= 500)
                {
                    double bytesPerSecond = (received - lastBytes) * 1000.0 / (now - lastTick);
                    item.Speed = $"{bytesPerSecond / 1024.0 / 1024.0:F2} MB/s";
                    lastBytes = received;
                    lastTick = now;
                }
                item.Status = DownloadStatus.Downloading;
            };

            op.StateChanged += (_, _) =>
            {
                if (!IsCurrentOperation()) return; // removed / restarted meanwhile
                switch (op.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        BrowserDownloads.TryRemove(item.Id, out _);
                        FinishBrowserDownload(item, op);
                        CloseSpawnedBlankTab(sender);
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        if (op.InterruptReason == CoreWebView2DownloadInterruptReason.UserPaused)
                        {
                            item.Status = DownloadStatus.Paused;
                            item.Speed = "-";
                            DatabaseService.Instance.SaveDownload(item);
                        }
                        else
                        {
                            BrowserDownloads.TryRemove(item.Id, out _);
                            item.ErrorMessage = "Interrupted (" + op.InterruptReason + ") - try Restart";
                            item.Status = DownloadStatus.Failed;
                            item.Speed = "-";
                            DatabaseService.Instance.SaveDownload(item);
                            CloseSpawnedBlankTab(sender);
                        }
                        break;
                }
            };
        }

        private static void FinishBrowserDownload(DownloadItem item, CoreWebView2DownloadOperation op)
        {
            string finalPath = op.ResultFilePath ?? string.Empty;
            if (finalPath.Length > 0)
            {
                item.SavePath = finalPath; // WebView2 may have picked another name if the file appeared meanwhile
                item.FileName = Path.GetFileName(finalPath);
            }
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
            MarkOfTheWeb.Apply(item.SavePath, item.Url, item.Referer); // before the UI offers "Open"
            item.Status = DownloadStatus.Completed;
            DatabaseService.Instance.SaveDownload(item, packageJson: string.Empty);
        }

        /// <summary>
        /// Pause / resume for a download the browser itself transfers. Returns true when the action was fully handled here;
        /// false lets the normal download service handle it (restart, new link, delete) after the browser's copy was cancelled.
        /// </summary>
        private static bool HandleBrowserDownloadAction(string action, DownloadItem item, CoreWebView2DownloadOperation op)
        {
            switch (action)
            {
                case "pause-download":
                    item.Status = DownloadStatus.Paused;
                    item.Speed = "-";
                    op.Pause();
                    DatabaseService.Instance.SaveDownload(item);
                    return true;
                case "resume-download":
                    if (op.CanResume)
                    {
                        item.Status = DownloadStatus.Downloading;
                        op.Resume();
                        return true;
                    }
                    goto case "restart-download";
                case "restart-download":
                case "refresh-download-link":
                case "remove-download":
                case "delete-download":
                    BrowserDownloads.TryRemove(item.Id, out _);
                    try { op.Cancel(); }
                    catch (Exception cancelEx) { AppLog.Debug("Cancelling the browser's copy of a download failed", cancelEx); }
                    return false;
            }
            return false;
        }
        // ---- Add download from a pasted link ----
        private static readonly HttpClient LinkProbeClient = CreateLinkProbeClient();

        private static HttpClient CreateLinkProbeClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
            return client;
        }

        /// <summary>Validates a pasted URL, works out a file name and puts it in the queue. Returns an error text, or null on success.</summary>
        private static async Task<string?> AddDownloadFromLinkAsync(string? rawUrl, CoreWebView2? core)
        {
            string text = (rawUrl ?? string.Empty).Trim();
            if (text.Length == 0) return "Paste a link first.";
            if (!text.Contains("://")) text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "Only http:// and https:// links are supported.";
            }

            bool duplicate = DownloadStore.Items.Any(d =>
                string.Equals(d.Url, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase) &&
                d.Status != DownloadStatus.Completed && d.Status != DownloadStatus.Failed);
            if (duplicate) return "That link is already in your downloads.";

            var (resolvedName, nameError) = await ResolveFileNameAsync(uri);
            if (nameError != null) return nameError;
            string fileName = resolvedName ?? ("download_" + DateTime.Now.Ticks);

            string cookieHeader = string.Empty;
            string userAgent = string.Empty;
            if (core != null)
            {
                try { userAgent = core.Settings.UserAgent ?? string.Empty; } catch (Exception uaEx) { AppLog.Debug("Could not read the WebView2 user agent for the pasted link", uaEx); }
                try { cookieHeader = await CookieManagerService.GetCookieHeaderAsync(core, uri.AbsoluteUri); }
                catch (Exception ex) { AppLog.Warn("Could not read cookies for pasted link - continuing without them", ex); }
            }

            string downloadsFolder = KnownFolders.GetDownloadsFolder();
            Directory.CreateDirectory(downloadsFolder);
            string savePath = GetUniqueFilePath(downloadsFolder, fileName);

            // Derive a Referer from the URL itself (scheme + host) when we don't have the originating page URL.
            // Most CDNs and download hosts only check that the Referer header is present and on their domain.
            string referer = $"{uri.Scheme}://{uri.Host}/";

            var item = new DownloadItem
            {
                FileName = Path.GetFileName(savePath),
                Url = uri.AbsoluteUri,
                SavePath = savePath,
                Type = DownloadType.Direct,
                UserAgent = userAgent,
                Referer = referer,
                Status = DownloadStatus.Queued
            };
            DownloadStore.Items.Insert(0, item);
            DownloadStore.Service.Enqueue(item, cookieHeader);
            return null;
        }

        /// <summary>Content-Disposition first, then the URL path (after redirects), then a generated name.</summary>
        private static async Task<(string? Name, string? Error)> ResolveFileNameAsync(Uri uri)
        {
            string? name = null;
            string? contentType = null;
            bool hasDisposition = false;
            // Use the URL origin as Referer — many download hosts reject headerless probes with 403.
            string probeReferer = $"{uri.Scheme}://{uri.Host}/";
            foreach (HttpMethod method in new[] { HttpMethod.Head, HttpMethod.Get })
            {
                try
                {
                    using var request = new HttpRequestMessage(method, uri);
                    request.Headers.Referrer = new Uri(probeReferer);
                    using var response = await LinkProbeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode) continue;
                    contentType = response.Content.Headers.ContentType?.MediaType;
                    var cd = response.Content.Headers.ContentDisposition;
                    name = cd?.FileNameStar ?? cd?.FileName;
                    hasDisposition = !string.IsNullOrWhiteSpace(name);
                    if (!hasDisposition)
                    {
                        Uri finalUri = response.RequestMessage?.RequestUri ?? uri;
                        name = Uri.UnescapeDataString(Path.GetFileName(finalUri.AbsolutePath));
                    }
                    break;
                }
                catch (Exception ex) { AppLog.Debug("File name probe failed", ex); }
            }

            if (string.IsNullOrWhiteSpace(name)) name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));

            // A web page, not a file (e.g. a YouTube or article link): saving its HTML would be useless.
            string pathExt = Path.GetExtension(name ?? string.Empty);
            if (!hasDisposition &&
                string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase) &&
                !pathExt.Equals(".html", StringComparison.OrdinalIgnoreCase) &&
                !pathExt.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                return (null, "That link opens a web page, not a file. Open it in a tab and use its download button.");
            }

            name = SanitizeFileName((name ?? string.Empty).Trim().Trim('"'));
            if (string.IsNullOrEmpty(name)) name = "download_" + DateTime.Now.Ticks;
            if (string.IsNullOrEmpty(Path.GetExtension(name)) && contentType != null &&
                ExtensionForContentType.TryGetValue(contentType, out string? guessedExt))
            {
                name += guessedExt;
            }
            return (name, null);
        }

        private static readonly Dictionary<string, string> ExtensionForContentType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = ".pdf",
            ["application/zip"] = ".zip",
            ["application/x-7z-compressed"] = ".7z",
            ["application/vnd.rar"] = ".rar",
            ["application/x-rar-compressed"] = ".rar",
            ["application/json"] = ".json",
            ["text/plain"] = ".txt",
            ["video/mp4"] = ".mp4",
            ["audio/mpeg"] = ".mp3",
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/gif"] = ".gif",
            ["image/webp"] = ".webp"
        };

        private static string BuildDownloadSettingsMessage() =>
            JsonSerializer.Serialize(new
            {
                type = "download-settings",
                maxConcurrent = DownloadSettings.MaxConcurrent,
                speedLimitKBps = DownloadSettings.SpeedLimitKBps
            }, BridgeJsonOptions);

        // ---- Blank tab left behind by a download link ----
        /// <summary>
        /// target=_blank download links open a new tab that navigates to the file; WebView2 turns that into a download
        /// and the tab stays empty. If the tab that started the download was opened by another tab and never showed a page,
        /// go back to the opener and close the leftover. The only tab of a window is never closed.
        /// </summary>
        private static void CloseSpawnedBlankTab(object? sender)
        {
            if (sender is not CoreWebView2 originCore) return;
            BrowserTab? target = null;
            MainWindow? win = null;
            foreach (var pair in _tabOwners)
            {
                if (pair.Key.WebView?.CoreWebView2 == originCore) { target = pair.Key; win = pair.Value; break; }
            }
            if (target == null || win == null || !target.IsPopupSpawned || target.HasLoadedPage) return;

            BrowserTab blankTab = target;
            MainWindow ownerWindow = win;
            ownerWindow.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                try
                {
                    if (!ownerWindow.Tabs.Contains(blankTab) || ownerWindow.Tabs.Count <= 1) return;
                    if (ReferenceEquals(ownerWindow.SelectedTab, blankTab))
                    {
                        BrowserTab? back = blankTab.Opener;
                        if (back != null && ownerWindow.Tabs.Contains(back)) ownerWindow.SelectedTab = back;
                    }
                    ownerWindow.CloseTab(blankTab);
                }
                catch (Exception ex) { AppLog.Warn("Could not close the blank download tab", ex); }
            }));
        }

        private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }
            fileName = fileName.Trim().TrimEnd('.', ' '); // Windows silently drops trailing dots/spaces
            if (fileName.Length == 0) return string.Empty;
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            if (ReservedFileNames.Contains(stem)) stem = "_" + stem; // CON.txt, NUL.zip ... cannot be created on Windows
            // Keep the full path well below MAX_PATH (260); the downloader also appends suffixes.
            if (stem.Length > 120) stem = stem.Substring(0, 120);
            if (ext.Length > 20) ext = ext.Substring(0, 20);
            return stem + ext;
        }
        /// <summary>Returns a path that doesn't exist yet ("file (1).zip", "file (2).zip", ...) so downloads never overwrite each other.</summary>
        private static string GetUniqueFilePath(string folder, string fileName)
        {
            string path = Path.Combine(folder, fileName);
            string name = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int counter = 1;
            while (File.Exists(path) || File.Exists(path + ".download") || IsDownloadPathReserved(path))
            {
                path = Path.Combine(folder, $"{name} ({counter++}){ext}");
            }
            return path;
        }
        /// <summary>A queued/in-progress download hasn't created its file yet, so File.Exists alone lets two same-named downloads collide.</summary>
        private static bool IsDownloadPathReserved(string path)
        {
            try
            {
                return DownloadStore.Items.Any(d =>
                    string.Equals(d.SavePath, path, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception reservedEx)
            {
                AppLog.Debug("Could not check the reserved download paths", reservedEx);
                return false;
            }
        }
        // ------------------------------------------------------------------
        // Downloads dropdown (toolbar button) + Downloads tab
        // ------------------------------------------------------------------
        private void BtnDownloadsManager_Click(object sender, RoutedEventArgs e) => ToggleDownloadsPopup();
        private void ToggleDownloadsPopup()
        {
            if (DownloadsPopup.IsOpen)
            {
                DownloadsPopup.IsOpen = false;
                return;
            }
            // Clicking the button while the popup is open first closes it (click-away); don't instantly re-open.
            if ((DateTime.UtcNow - _downloadsPopupClosedAt).TotalMilliseconds < 250) return;
            ShowDownloadsPopup();
        }
        private void ShowDownloadsPopup()
        {
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            HideTabPreview();
            // Right-align the card with the toolbar button so it never hangs off the window edge.
            DownloadsPopup.HorizontalOffset = BtnDownloadsManager.ActualWidth - DownloadsPopupWidth;
            DownloadsPopup.IsOpen = true;
        }
        /// <summary>The window whose tab started a download (a tab can be dragged to another window).</summary>
        private MainWindow GetWindowForCore(object? sender)
        {
            if (sender is CoreWebView2 core)
            {
                foreach (var pair in _tabOwners)
                {
                    if (pair.Key.WebView?.CoreWebView2 == core && pair.Value.IsVisible) return pair.Value;
                }
            }
            return this;
        }
        private static void RefreshDownloadStatusTexts()
        {
            foreach (DownloadItem item in DownloadStore.Items) item.RefreshStatusText();
        }
        private void BtnCloseDownloadsPopup_Click(object sender, RoutedEventArgs e) => DownloadsPopup.IsOpen = false;
        private void BtnFullDownloadHistory_Click(object sender, RoutedEventArgs e)
        {
            DownloadsPopup.IsOpen = false;
            OpenDownloadsTab();
        }
        private void DownloadRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject)) return; // the "show in folder" button handles itself
            if ((sender as FrameworkElement)?.DataContext is not DownloadItem item || item.Status != DownloadStatus.Completed) return;
            if (!File.Exists(item.SavePath))
            {
                item.RefreshStatusText(); // now reads "File deleted"
                return;
            }
            DownloadsPopup.IsOpen = false;
            OpenDownloadedFile(item);
        }
        private void DownloadShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
            DownloadsPopup.IsOpen = false;
            RevealDownloadedFile(item);
        }
        // ---- shared by the dropdown and the Downloads tab. They only ever use the path stored in the app's own
        // ---- download list (the web page sends an id, never a path), so a page cannot make us open arbitrary files.
        private static void OpenDownloadedFile(DownloadItem item)
        {
            try
            {
                if (!File.Exists(item.SavePath))
                {
                    item.RefreshStatusText();
                    return;
                }
                if (IsRiskyFile(item.SavePath) &&
                    ThemedDialog.Show($"\"{item.FileName}\" is a program, script or file that can run code.\nOnly open it if you trust where it came from.\n\nOpen it anyway?",
                        "Open download", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    return;
                }
                Process.Start(new ProcessStartInfo(item.SavePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Could not open downloaded file: {item.SavePath}", ex);
                ThemedDialog.Show($"Could not open the file:\n{ex.Message}", "Open download", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private static readonly HashSet<string> RiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl",
            ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".jar", ".reg", ".lnk",
            ".url", ".msc", ".chm", ".jnlp", ".application", ".appinstaller", ".docm", ".xlsm", ".pptm", ".iso",
            ".dll", ".sct", ".wsc", ".appref-ms", ".settingcontent-ms", ".library-ms", ".gadget", ".vhd", ".vhdx", ".img",
            ".msp", ".mst", ".msixbundle", ".appxbundle", ".xll", ".wll", ".xlam", ".ppam", ".dotm", ".xltm", ".potm", ".sldm",
            ".diagcab", ".scf", ".inf", ".ps1xml", ".psc1", ".vb", ".ws", ".theme", ".themepack", ".py", ".pyw", ".pyz"
        };
        private static bool IsRiskyFile(string path) => RiskyExtensions.Contains(Path.GetExtension(path));
        private static void RevealDownloadedFile(DownloadItem item)
        {
            try
            {
                if (File.Exists(item.SavePath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.SavePath}\"") { UseShellExecute = true });
                    return;
                }
                string? folder = Path.GetDirectoryName(item.SavePath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                }
                item.RefreshStatusText();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Could not show download in folder: {item.SavePath}", ex);
            }
        }
        private static void OpenDownloadsFolder()
        {
            try
            {
                string folder = KnownFolders.GetDownloadsFolder();
                if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not open the Downloads folder", ex);
            }
        }
        // ---- Downloads tab (https://local.browser/downloads.html, shown as mint://downloads) ----
        /// <summary>Ctrl+J and "Full download history": switch to the Downloads tab if it is open, otherwise open it in a new tab.</summary>
        private void OpenDownloadsTab() => _ = OpenDownloadsTabAsync();
        private async Task OpenDownloadsTabAsync()
        {
            try
            {
                BrowserTab? existing = Tabs.FirstOrDefault(tab => IsDownloadsPage(tab.Url));
                if (existing != null)
                {
                    SelectedTab = existing;
                    return;
                }
                await AddNewTabAsync(DownloadsUrl);
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not open the Downloads tab", ex);
                ThemedDialog.Show($"Could not open the Downloads tab: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        // File.Exists for every finished download used to run on the UI thread at each poll of the Downloads page (every 0.6 s
        // while something is downloading). The answer is cached for a few seconds per path, so a long list costs one disk check
        // per file every few seconds instead of one per poll. A file deleted outside the browser shows as missing within that time.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Exists, long Tick)> _fileExistsCache = new();
        private const long FileExistsCacheMs = 10000;
        private static bool FileExistsCached(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            long now = Environment.TickCount64;
            if (_fileExistsCache.TryGetValue(path, out var hit) && now - hit.Tick < FileExistsCacheMs) return hit.Exists;
            if (_fileExistsCache.Count > 5000) _fileExistsCache.Clear();
            bool exists = File.Exists(path);
            _fileExistsCache[path] = (exists, now);
            return exists;
        }
        /// <summary>
        /// Adds the icon for a download to the shared icon table (once per distinct icon).
        /// A completed program gets its own icon from the file on disk; all other types get
        /// the Windows file-type icon. Returns the table key, or null when no icon is available.
        /// </summary>
        private static string? RegisterIcon(DownloadItem d, Dictionary<string, string> icons)
        {
            string? finishedFile = d.Status == DownloadStatus.Completed && FileExistsCached(d.SavePath)
                ? d.SavePath : null;
            string key = CustomBrowser.Helpers.ShellIcon.KeyFor(d.FileName, finishedFile);
            if (!icons.ContainsKey(key))
            {
                string? png = CustomBrowser.Helpers.ShellIcon.GetBase64ForFile(d.FileName, finishedFile);
                if (png == null) return null;
                icons[key] = png;
            }
            return key;
        }

        private static string BuildDownloadsMessage()
        {
            var icons = new Dictionary<string, string>();
            var rows = DownloadStore.Items.Select(d => new
            {
                id = d.Id,
                fileName = d.FileName,
                url = d.Url,
                savePath = d.SavePath,
                category = d.Category,
                status = d.Status.ToString().ToLowerInvariant(),
                progress = d.Progress,
                speed = d.Speed,
                downloadedSize = d.DownloadedSize,
                totalSize = d.TotalSize,
                receivedBytes = d.ReceivedBytes,
                totalBytes = d.TotalBytes,
                startedAt = new DateTimeOffset(d.StartedAt).ToUnixTimeMilliseconds(),
                completedAt = d.CompletedAt.HasValue ? new DateTimeOffset(d.CompletedAt.Value).ToUnixTimeMilliseconds() : (long?)null,
                fileExists = d.Status != DownloadStatus.Completed || FileExistsCached(d.SavePath),
                errorMessage = d.ErrorMessage,
                queuePosition = d.QueuePosition,
                iconKey = RegisterIcon(d, icons)
            }).ToList();
            return JsonSerializer.Serialize(new { type = "downloads-data", data = rows, icons }, BridgeJsonOptions);
        }
        private static void HandleDownloadAction(string action, string? id, JsonElement? root = null)
        {
            try
            {
                if (action == "clear-downloads")
                {
                    bool deleteFromDisk = root.HasValue && root.Value.TryGetProperty("deleteFileFromDisk", out var dfdEl) && dfdEl.GetBoolean();
                    for (int i = DownloadStore.Items.Count - 1; i >= 0; i--)
                    {
                        var itm = DownloadStore.Items[i];
                        if (itm.Status == DownloadStatus.Completed || itm.Status == DownloadStatus.Failed)
                        {
                            DownloadStore.Service.DeleteDownload(itm.Id, deleteFromDisk);
                        }
                    }
                    return;
                }

                if (string.IsNullOrEmpty(id)) return;
                DownloadItem? item = DownloadStore.Items.FirstOrDefault(d => d.Id == id);
                if (item == null) return;

                // A download the browser itself transfers (our downloader was refused) is paused / resumed through WebView2.
                if (BrowserDownloads.TryGetValue(item.Id, out CoreWebView2DownloadOperation? browserOp) &&
                    HandleBrowserDownloadAction(action, item, browserOp)) return;

                switch (action)
                {
                    case "open-download":
                        OpenDownloadedFile(item);
                        break;
                    case "show-download":
                        RevealDownloadedFile(item);
                        break;
                    case "pause-download":
                        DownloadStore.Service.PauseDownload(item.Id);
                        break;
                    case "resume-download":
                        DownloadStore.Service.ResumeDownload(item.Id);
                        break;
                    case "restart-download":
                        DownloadStore.Service.RestartDownload(item.Id);
                        break;
                    case "refresh-download-link":
                        if (root.HasValue && root.Value.TryGetProperty("newUrl", out var urlEl))
                        {
                            string newUrl = urlEl.GetString() ?? string.Empty;
                            DownloadStore.Service.RefreshDownloadLink(item.Id, newUrl);
                        }
                        break;
                    case "remove-download":
                    case "delete-download":
                        bool deleteDisk = root.HasValue && root.Value.TryGetProperty("deleteFileFromDisk", out var diskEl) && diskEl.GetBoolean();
                        DownloadStore.Service.DeleteDownload(item.Id, deleteDisk);
                        break;
                    case "copy-download-link":
                        if (!string.IsNullOrEmpty(item.Url))
                        {
                            Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(item.Url));
                        }
                        break;
                    case "copy-file-path":
                        if (!string.IsNullOrEmpty(item.SavePath))
                        {
                            Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(item.SavePath));
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Download action '{action}' failed", ex);
            }
        }
    }
}

