using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CustomBrowser.Helpers;
using CustomBrowser.Services.Streams;
using CustomBrowser.Services.Ytdl;

namespace CustomBrowser.Views
{
    /// <summary>
    /// Asks yt-dlp what is on the page, then lets the user choose what to download.
    /// The window never starts a download itself: it hands a YtdlChoice back to the main window.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable", Justification = "Only cancelled on close; the source has no timer or wait handle, so there is nothing to release.")]
    public partial class YtdlPickerWindow : Window
    {
        private readonly string _pageUrl;
        private readonly Func<Task<string?>> _exportCookies;
        private readonly CancellationTokenSource _lifeCts = new CancellationTokenSource();
        private CancellationTokenSource? _loadCts;
        private string? _exe;
        private YtdlInfo? _info;
        private string? _cookieFile;
        private string? _cookieHint;
        private bool _canMerge;

        public YtdlChoice? Result { get; private set; }

        public YtdlPickerWindow(Window owner, string pageUrl, string pageTitle, string folder, Func<Task<string?>> exportCookies)
        {
            InitializeComponent();
            Owner = owner;
            _pageUrl = pageUrl;
            _exportCookies = exportCookies;

            TxtPage.Text = Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? uri) ? uri.Host : pageUrl;
            TxtFolder.Text = folder;
            TxtName.Text = CleanName(pageTitle);

            LstMode.Items.Add("Video + audio");
            LstMode.Items.Add("Audio only");
            LstMode.Items.Add("Audio as MP3");
            LstMode.SelectedIndex = 0;

            Loaded += async (_, _) => await LoadAsync();
            Closed += (_, _) =>
            {
                try { _lifeCts.Cancel(); } catch (Exception) { /* closing anyway */ }
                try { _loadCts?.Cancel(); } catch (Exception) { /* closing anyway */ }
                // The login file belongs to the download if one was started with it; otherwise it must not stay on disk.
                if (Result == null || !string.Equals(Result.CookieFile, _cookieFile, StringComparison.OrdinalIgnoreCase))
                {
                    YtdlStore.DeleteCookieFile(_cookieFile);
                }
            };
        }

        // ------------------------------------------------------------------
        // Reading the page
        // ------------------------------------------------------------------
        private async Task LoadAsync()
        {
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;
            CancellationToken ct = cts.Token;

            _info = null;
            _cookieHint = null;
            BtnDownload.IsEnabled = false;
            ShowStatus("Reading the page...", true, false, false);

            try
            {
                string? exe = YtdlTool.Find();
                if (exe == null)
                {
                    TxtToolVersion.Text = "yt-dlp is not installed";
                    BtnUpdate.Visibility = Visibility.Collapsed;
                    ShowStatus("To download from this site the browser needs yt-dlp, a free open-source tool (about 18 MB). " +
                               "It is downloaded from the official GitHub release and checked against its official checksum before it is used.",
                               false, true, false);
                    return;
                }
                _exe = exe;
                BtnUpdate.Visibility = Visibility.Visible;
                _ = ShowVersionAsync(exe);

                string? cookie = null;
                if (ChkCookies.IsChecked == true)
                {
                    cookie = await _exportCookies();
                    if (ct.IsCancellationRequested)
                    {
                        YtdlStore.DeleteCookieFile(cookie);
                        ct.ThrowIfCancellationRequested();
                    }
                    if (cookie == null) _cookieHint = "No login for this site was found in the browser (are you signed in?). Continuing without it.";
                }
                ReplaceCookieFile(cookie);

                YtdlInfo info = await YtdlProbe.RunAsync(exe, _pageUrl, cookie, ct);
                ct.ThrowIfCancellationRequested();

                _info = info;
                ShowOptions(info);
            }
            catch (OperationCanceledException)
            {
                // a newer load replaced this one, or the window was closed
            }
            catch (YtdlException ex)
            {
                if (!ct.IsCancellationRequested) ShowStatus(ex.Message + RuntimeHint(), false, false, true);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Reading the page with yt-dlp failed", ex);
                if (!ct.IsCancellationRequested) ShowStatus("Something went wrong: " + ex.Message + RuntimeHint(), false, false, true);
            }
        }

        private void ReplaceCookieFile(string? newPath)
        {
            if (_cookieFile != null && !string.Equals(_cookieFile, newPath, StringComparison.OrdinalIgnoreCase))
            {
                YtdlStore.DeleteCookieFile(_cookieFile);
            }
            _cookieFile = newPath;
        }

        private async Task ShowVersionAsync(string exe)
        {
            try
            {
                string v = await YtdlTool.GetVersionAsync(exe, _lifeCts.Token);
                TxtToolVersion.Text = v.Length > 0 ? "yt-dlp " + v : "yt-dlp";
            }
            catch (Exception)
            {
                // the version label is optional
            }
        }

        private void ShowStatus(string text, bool busy, bool setup, bool retry)
        {
            StatusPanel.Visibility = Visibility.Visible;
            OptionsPanel.Visibility = Visibility.Collapsed;
            TxtStatus.Text = text;
            BarBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BtnSetup.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
            BtnRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
            BtnDownload.IsEnabled = false;
        }

        private void ShowOptions(YtdlInfo info)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            OptionsPanel.Visibility = Visibility.Visible;

            _canMerge = FfmpegTool.Find() != null;
            TxtVideoTitle.Text = info.Title;
            string meta = info.Uploader;
            string duration = FormatDuration(info.DurationSeconds);
            if (duration.Length > 0) meta = meta.Length > 0 ? meta + "  -  " + duration : duration;
            if (info.Site.Length > 0) meta = meta.Length > 0 ? meta + "  -  " + info.Site : info.Site;
            TxtMeta.Text = meta;

            string suggested = CleanName(info.Title);
            if (suggested.Length > 0) TxtName.Text = suggested;

            LstQuality.ItemsSource = YtdlPlanner.BuildQualities(info, _canMerge);
            LstQuality.SelectedIndex = 0;
            UpdateNote();
        }

        // ------------------------------------------------------------------
        // Choices
        // ------------------------------------------------------------------
        private YtdlMode SelectedMode()
        {
            switch (LstMode.SelectedIndex)
            {
                case 1: return YtdlMode.AudioOriginal;
                case 2: return YtdlMode.AudioMp3;
                default: return YtdlMode.VideoAudio;
            }
        }

        private string RuntimeHint()
        {
            if (YtdlTool.IsYoutubeUrl(_pageUrl) && !YtdlTool.HasJsRuntime())
            {
                return "\n\nYouTube also needs a JavaScript runtime. Install Deno with:  winget install DenoLand.Deno  (then reopen the browser).";
            }
            return string.Empty;
        }

        private void UpdateNote()
        {
            YtdlMode mode = SelectedMode();
            bool blocked = false;
            var notes = new List<string>();
            if (_cookieHint != null) notes.Add(_cookieHint);

            if (mode == YtdlMode.AudioMp3 && !_canMerge)
            {
                notes.Add("Converting to MP3 needs FFmpeg. Install it with:  winget install Gyan.FFmpeg  (then reopen the browser).");
                blocked = true;
            }
            else if (mode == YtdlMode.VideoAudio && !_canMerge)
            {
                notes.Add("FFmpeg was not found, so only formats that already contain sound are listed (often a lower quality). " +
                          "For the best quality install FFmpeg:  winget install Gyan.FFmpeg");
            }

            string runtime = RuntimeHint().Trim();
            if (runtime.Length > 0) notes.Add(runtime);

            string note = string.Join("\n", notes);
            TxtNote.Text = note;
            TxtNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            LstQuality.IsEnabled = mode == YtdlMode.VideoAudio;
            LstQuality.Opacity = mode == YtdlMode.VideoAudio ? 1.0 : 0.4;
            BtnDownload.IsEnabled = _info != null && !blocked;
        }

        private void LstMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_info == null) return;
            UpdateNote();
        }

        private void LstQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // nothing to recalculate: the size is part of each quality line
        }

        private void ChkCookies_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _ = LoadAsync();
        }

        private void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadAsync();
        }

        // ------------------------------------------------------------------
        // yt-dlp installation / update
        // ------------------------------------------------------------------
        private async void BtnSetup_Click(object sender, RoutedEventArgs e)
        {
            BtnSetup.IsEnabled = false;
            ShowStatus("Downloading yt-dlp...", true, false, false);
            try
            {
                var status = new Progress<string>(m => TxtStatus.Text = m);
                await YtdlTool.DownloadAsync(status, _lifeCts.Token);
                await LoadAsync();
            }
            catch (OperationCanceledException)
            {
                // window closed
            }
            catch (Exception ex)
            {
                AppLog.Warn("Installing yt-dlp failed", ex);
                ShowStatus("yt-dlp could not be installed: " + ex.Message + "\n\nYou can also install it yourself with:  winget install yt-dlp.yt-dlp", false, true, false);
            }
            finally
            {
                BtnSetup.IsEnabled = true;
            }
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_exe == null) return;
            BtnUpdate.IsEnabled = false;
            try
            {
                TxtToolVersion.Text = "Updating...";
                var status = new Progress<string>(m => TxtToolVersion.Text = m);
                string message = await YtdlTool.UpdateAsync(_exe, status, _lifeCts.Token);
                TxtToolVersion.Text = message;
                if (_info == null) await LoadAsync();
            }
            catch (OperationCanceledException)
            {
                // window closed
            }
            catch (Exception ex)
            {
                AppLog.Warn("Updating yt-dlp failed", ex);
                TxtToolVersion.Text = ex.Message;
            }
            finally
            {
                BtnUpdate.IsEnabled = true;
            }
        }

        // ------------------------------------------------------------------
        // Buttons
        // ------------------------------------------------------------------
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            DialogHelpers.BrowseFolder(TxtFolder, "Save the download in...");
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_info == null) return;

            string name = CleanName(TxtName.Text);
            if (name.Length == 0)
            {
                TxtNote.Text = "Please type a file name.";
                TxtNote.Visibility = Visibility.Visible;
                return;
            }

            string folder = TxtFolder.Text.Trim();
            try
            {
                if (folder.Length == 0) throw new InvalidOperationException("Please choose a folder.");
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                TxtNote.Text = "This folder cannot be used: " + ex.Message;
                TxtNote.Visibility = Visibility.Visible;
                return;
            }

            YtdlMode mode = SelectedMode();
            var quality = LstQuality.SelectedItem as YtdlQualityOption;
            int height = mode == YtdlMode.VideoAudio && quality != null ? quality.Height : 0;
            long estimate = mode == YtdlMode.VideoAudio ? (quality != null ? quality.EstimatedBytes : 0) : YtdlPlanner.EstimateAudio(_info);
            bool useCookies = ChkCookies.IsChecked == true && !string.IsNullOrEmpty(_cookieFile);

            Result = new YtdlChoice
            {
                Mode = mode,
                Selector = YtdlPlanner.BuildSelector(mode, height, _canMerge),
                Title = _info.Title,
                FileBaseName = name,
                Folder = folder,
                UseCookies = useCookies,
                CookieFile = useCookies ? _cookieFile : null,
                EstimatedBytes = estimate
            };
            DialogResult = true;
        }

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DialogHelpers.DragCard(this);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        private static string CleanName(string? raw) => DialogHelpers.CleanFileName(raw);

        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return string.Empty;
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0}:{1:00}", t.Minutes, t.Seconds);
        }
    }
}

