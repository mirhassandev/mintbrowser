using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CustomBrowser.Helpers;
using CustomBrowser.Models;
using CustomBrowser.Services;
using CustomBrowser.Services.Streams;
using CustomBrowser.Views;

namespace CustomBrowser
{
    /// <summary>IDM-style media download: the toolbar button appears when the page loads video / audio streams.</summary>
    public partial class MainWindow
    {
        private DispatcherTimer? _streamsTimer;
        private int _streamsShown = -1;

        private void InitStreams()
        {
            _streamsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _streamsTimer.Tick += (_, _) => { UpdateStreamsButton(); UpdateYtdlButton(); };
            _streamsTimer.Start();
            Closed += (_, _) => _streamsTimer?.Stop();
        }

        private void UpdateStreamsButton()
        {
            try
            {
                BrowserTab? tab = SelectedTab;
                int count = tab == null ? 0 : StreamSniffer.Count(tab);
                if (count == _streamsShown) return;
                _streamsShown = count;
                BtnStreams.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
                StreamsBadgeText.Text = count > 9 ? "9+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Updating the media button failed", ex);
            }
        }

        private static async Task<string> GetStreamCookiesAsync(DetectedStream stream)
        {
            if (stream.Core == null) return string.Empty;
            try
            {
                return await CookieManagerService.GetCookieHeaderAsync(stream.Core, stream.Url);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not read cookies for the stream - continuing without them", ex);
                return string.Empty;
            }
        }

        private async void BtnStreams_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BrowserTab? tab = SelectedTab;
                if (tab == null) return;
                List<DetectedStream> streams = StreamSniffer.GetStreams(tab);
                if (streams.Count == 0) return;

                string folder = KnownFolders.GetDownloadsFolder();
                Directory.CreateDirectory(folder);
                string title = tab.Title ?? string.Empty;

                var picker = new StreamPickerWindow(this, streams, title, folder, GetStreamCookiesAsync);
                if (picker.ShowDialog() != true || picker.Result == null) return;
                await StartStreamDownloadAsync(picker.Result);
            }
            catch (Exception ex)
            {
                AppLog.Error("Starting the media download failed", ex);
                ThemedDialog.Show(this, "The media download could not be started:\n" + ex.Message, "Download media", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task StartStreamDownloadAsync(StreamPick pick)
        {
            DetectedStream source = pick.Source;
            string cookieHeader = await GetStreamCookiesAsync(source);

            // A plain media file is an ordinary download: the normal engine (chunks, resume) does the work.
            if (pick.Manifest.Protocol == StreamProtocol.Progressive)
            {
                TrackSpec file = pick.Tracks[0];
                string directPath = GetUniqueFilePath(pick.Folder, SanitizeFileName(pick.FileName + file.RawExt));
                var direct = new DownloadItem
                {
                    FileName = Path.GetFileName(directPath),
                    Url = source.Url,
                    SavePath = directPath,
                    Type = DownloadType.Direct,
                    Referer = source.Referer,
                    UserAgent = source.UserAgent,
                    Status = DownloadStatus.Queued
                };
                DownloadStore.Items.Insert(0, direct);
                if (!IsDownloadsPage(SelectedTab?.Url)) ShowDownloadsPopup();
                DownloadStore.Service.Enqueue(direct, cookieHeader);
                return;
            }

            StreamPlan plan = StreamPlanner.Plan(pick.Tracks, FfmpegTool.Find() != null, pick.ConvertToMp4);
            string outputPath = GetUniqueFilePath(pick.Folder, SanitizeFileName(pick.FileName + plan.Ext));
            string host = Uri.TryCreate(source.Url, UriKind.Absolute, out Uri? sourceUri) ? sourceUri.Host : string.Empty;

            var spec = new StreamJobSpec
            {
                Title = pick.Title,
                Referer = source.Referer,
                Origin = source.Origin,
                UserAgent = source.UserAgent,
                CookieHeader = cookieHeader,
                CookieHost = host,
                OutputPath = outputPath,
                Tracks = pick.Tracks,
                Concurrency = 6,
                ConvertToMp4 = pick.ConvertToMp4
            };

            var item = new DownloadItem
            {
                FileName = Path.GetFileName(outputPath),
                Url = source.Url,
                SavePath = outputPath,
                Type = DownloadType.Stream,
                Referer = source.Referer,
                UserAgent = source.UserAgent,
                Status = DownloadStatus.Queued
            };

            StreamStore.WriteSpec(item.Id, spec);
            DownloadStore.Items.Insert(0, item);
            if (!IsDownloadsPage(SelectedTab?.Url)) ShowDownloadsPopup();
            DownloadStore.Service.EnqueueStream(item);
        }
    }
}

