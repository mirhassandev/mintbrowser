using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CustomBrowser.Helpers;
using CustomBrowser.Models;
using CustomBrowser.Services;
using CustomBrowser.Services.Ytdl;
using CustomBrowser.Views;
using Microsoft.Web.WebView2.Core;

namespace CustomBrowser
{
    /// <summary>Video / audio download for sites that hide their streams from the page (YouTube, Facebook, Instagram, ...) with yt-dlp.</summary>
    public partial class MainWindow
    {
        /// <summary>The toolbar button shows on these sites (and their sub-domains). Add a host here to get the button on another site.</summary>
        private static readonly string[] YtdlHosts =
        {
            "youtube.com", "youtu.be", "facebook.com", "fb.watch", "instagram.com", "twitter.com", "x.com",
            "tiktok.com", "vimeo.com", "dailymotion.com", "twitch.tv", "reddit.com", "soundcloud.com",
            "bilibili.com", "streamable.com", "pinterest.com", "tumblr.com", "bsky.app", "odysee.com",
            "rumble.com", "ok.ru", "vk.com", "9gag.com"
        };

        private bool _ytdlShown; // refreshed by the media timer in MainWindow.Streams.cs

        private void UpdateYtdlButton()
        {
            try
            {
                bool show = IsYtdlSupportedUrl(SelectedTab?.Url);
                if (show == _ytdlShown) return;
                _ytdlShown = show;
                BtnYtdl.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Updating the yt-dlp button failed", ex);
            }
        }

        private static bool IsYtdlSupportedUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            string host = uri.Host.ToLowerInvariant();
            if (host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal))
            {
                // the home page and search results hold no single video
                string path = uri.AbsolutePath;
                return path.StartsWith("/watch", StringComparison.Ordinal) || path.StartsWith("/shorts/", StringComparison.Ordinal) ||
                       path.StartsWith("/live/", StringComparison.Ordinal) || path.StartsWith("/embed/", StringComparison.Ordinal);
            }

            foreach (string h in YtdlHosts)
            {
                if (host == h || host.EndsWith("." + h, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static Task<string?> ExportYtdlCookiesAsync(BrowserTab tab, string url)
        {
            CoreWebView2? core = tab.WebView?.CoreWebView2;
            if (core == null) return Task.FromResult<string?>(null);
            return YtdlCookies.ExportAsync(core, url);
        }

        private void BtnYtdl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BrowserTab? tab = SelectedTab;
                if (tab == null) return;
                string url = tab.Url ?? string.Empty;
                if (!IsYtdlSupportedUrl(url)) return;

                string folder = KnownFolders.GetDownloadsFolder();
                Directory.CreateDirectory(folder);

                var picker = new YtdlPickerWindow(this, url, tab.Title ?? string.Empty, folder, () => ExportYtdlCookiesAsync(tab, url));
                if (picker.ShowDialog() != true || picker.Result == null) return;
                StartYtdlDownload(picker.Result, url, tab);
            }
            catch (Exception ex)
            {
                AppLog.Error("Starting the yt-dlp download failed", ex);
                ThemedDialog.Show(this, "The download could not be started:\n" + ex.Message, "Download video / audio", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void StartYtdlDownload(YtdlChoice choice, string pageUrl, BrowserTab tab)
        {
            string ext = choice.Mode == YtdlMode.AudioMp3 ? ".mp3" : (choice.Mode == YtdlMode.AudioOriginal ? ".m4a" : ".mp4");
            string safeName = SanitizeFileName(choice.FileBaseName + ext);
            if (safeName.Length == 0) safeName = "video" + ext;

            // The real extension is only known when yt-dlp is done (mp4, mkv, webm, m4a, opus ...); it is fixed up then.
            string plannedPath = GetUniqueFilePath(choice.Folder, safeName);
            string baseName = Path.GetFileNameWithoutExtension(plannedPath);

            string userAgent = string.Empty;
            if (choice.UseCookies)
            {
                try { userAgent = tab.WebView?.CoreWebView2?.Settings.UserAgent ?? string.Empty; }
                catch (Exception ex) { AppLog.Debug("Could not read the browser user agent", ex); }
            }

            var spec = new YtdlJobSpec
            {
                PageUrl = pageUrl,
                Title = choice.Title,
                Selector = choice.Selector,
                AudioMp3 = choice.Mode == YtdlMode.AudioMp3,
                OutputFolder = choice.Folder,
                BaseName = baseName,
                CookieFile = choice.UseCookies ? (choice.CookieFile ?? string.Empty) : string.Empty,
                UserAgent = userAgent,
                EstimatedBytes = choice.EstimatedBytes
            };

            var item = new DownloadItem
            {
                FileName = Path.GetFileName(plannedPath),
                Url = pageUrl,
                SavePath = plannedPath,
                Type = DownloadType.Ytdl,
                Status = DownloadStatus.Queued
            };

            YtdlStore.WriteSpec(item.Id, spec);
            DownloadStore.Items.Insert(0, item);
            if (!IsDownloadsPage(SelectedTab?.Url)) ShowDownloadsPopup();
            DownloadStore.Service.EnqueueYtdl(item);
        }
    }
}

