using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CustomBrowser.Helpers;
using CustomBrowser.Models;
using CustomBrowser.Services.Streams;

namespace CustomBrowser.Views
{
    /// <summary>What the user chose in the picker.</summary>
    public sealed class StreamPick
    {
        public DetectedStream Source { get; set; } = new DetectedStream();
        public StreamManifest Manifest { get; set; } = new StreamManifest();
        /// <summary>Resolved tracks: [video, audio], [muxed video], or [audio].</summary>
        public List<TrackSpec> Tracks { get; set; } = new List<TrackSpec>();
        public string FileName { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public bool ConvertToMp4 { get; set; } = true;
        public string Title { get; set; } = string.Empty;
    }

    /// <summary>
    /// IDM-style picker: lists the media found on the page, then every quality and audio track of the chosen one.
    /// The window never starts a download itself: it hands a StreamPick back to the main window.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable", Justification = "Only cancelled on close; the source has no timer or wait handle, so there is nothing to release.")]
    public partial class StreamPickerWindow : Window
    {
        private sealed class Entry
        {
            public TrackSpec? Track;
            public DetectedStream? Source;
            public string Text = string.Empty;
            public override string ToString() => Text;
        }

        private const string NoVideoText = "No video (audio only)";
        private const string NoAudioText = "No audio (video only)";

        private readonly List<DetectedStream> _streams;
        private readonly Func<DetectedStream, Task<string>> _cookieProvider;
        private readonly string _pageTitle;
        private readonly CancellationTokenSource _lifeCts = new CancellationTokenSource();
        private CancellationTokenSource? _loadCts;
        private string _cookieHeader = string.Empty;
        private StreamManifest? _manifest;
        private DetectedStream? _current;
        private List<TrackSpec> _audioTracks = new List<TrackSpec>();
        private bool _busy;
        private bool _updating;

        public StreamPick? Result { get; private set; }

        public StreamPickerWindow(Window owner, List<DetectedStream> streams, string pageTitle, string defaultFolder,
            Func<DetectedStream, Task<string>> cookieProvider)
        {
            InitializeComponent();
            Owner = owner;
            _streams = streams;
            _pageTitle = pageTitle;
            _cookieProvider = cookieProvider;

            string page = streams.Count > 0 ? streams[0].PageUrl : string.Empty;
            TxtPage.Text = Uri.TryCreate(page, UriKind.Absolute, out Uri? uri) ? uri.Host : pageTitle;
            TxtFolder.Text = defaultFolder;

            _updating = true;
            for (int i = 0; i < streams.Count; i++)
            {
                LstSources.Items.Add(new Entry { Source = streams[i], Text = Describe(streams[i], i) });
            }
            LstSources.SelectedIndex = 0;
            _updating = false;
            PanelSources.Visibility = streams.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            Loaded += async (_, _) => await LoadSelectedAsync();
            Closed += (_, _) =>
            {
                try { _lifeCts.Cancel(); } catch (Exception) { /* closing anyway */ }
                try { _loadCts?.Cancel(); } catch (Exception) { /* closing anyway */ }
            };
        }

        private static string Describe(DetectedStream d, int index)
        {
            string kind = d.Protocol == StreamProtocol.Hls ? "HLS stream" : d.Protocol == StreamProtocol.Dash ? "DASH stream" : "Media file";
            string name = d.Url;
            if (Uri.TryCreate(d.Url, UriKind.Absolute, out Uri? u))
            {
                string last = u.AbsolutePath.TrimEnd('/');
                int slash = last.LastIndexOf('/');
                if (slash >= 0) last = last.Substring(slash + 1);
                name = u.Host + (last.Length > 0 ? " / " + last : string.Empty);
            }
            if (name.Length > 56) name = name.Substring(0, 53) + "...";
            string size = d.ContentLength > 0 ? "  -  " + DownloadItem.FormatBytes(d.ContentLength) : string.Empty;
            return (index + 1) + ". " + kind + "  -  " + name + size;
        }

        // ------------------------------------------------------------------
        // Reading a stream
        // ------------------------------------------------------------------
        private async Task LoadSelectedAsync()
        {
            DetectedStream? stream = (LstSources.SelectedItem as Entry)?.Source;
            if (stream == null) return;
            _current = stream;

            try { _loadCts?.Cancel(); } catch (Exception) { /* replaced by a newer load */ }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifeCts.Token);
            _loadCts = cts;
            CancellationToken ct = cts.Token;

            _manifest = null;
            BtnDownload.IsEnabled = false;
            ShowStatus("Reading the stream...", true, false);

            try
            {
                _cookieHeader = await _cookieProvider(stream);
                StreamManifest manifest = await StreamManifestLoader.LoadAsync(stream, _cookieHeader, ct);
                ct.ThrowIfCancellationRequested();
                _manifest = manifest;
                ShowOptions(manifest);
            }
            catch (OperationCanceledException)
            {
                // another stream was selected, or the window was closed
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                AppLog.Warn("Could not read the detected stream: " + stream.Url, ex);
                ShowStatus(Explain(ex, "Could not read this stream: "), false, true);
            }
        }

        private static string Explain(Exception ex, string prefix)
        {
            return ex is StreamHttpException http
                ? "The server refused the request (" + http.StatusCode + "). Reload the page and try again."
                : prefix + ex.Message;
        }

        private void ShowStatus(string text, bool busy, bool retry)
        {
            StatusPanel.Visibility = Visibility.Visible;
            OptionsPanel.Visibility = Visibility.Collapsed;
            TxtStatus.Text = text;
            BarBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BtnRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
            BtnDownload.IsEnabled = false;
        }

        private void ShowOptions(StreamManifest manifest)
        {
            if (!string.IsNullOrEmpty(manifest.Error))
            {
                ShowStatus(manifest.Error, false, false);
                return;
            }

            List<TrackSpec> videos = manifest.Tracks
                .Where(t => t.Kind != TrackKind.Audio && string.IsNullOrEmpty(t.Error))
                .OrderByDescending(t => t.Height).ThenByDescending(t => t.Bandwidth).ToList();
            _audioTracks = manifest.Tracks
                .Where(t => t.Kind == TrackKind.Audio && string.IsNullOrEmpty(t.Error))
                .OrderByDescending(t => t.IsDefault).ThenByDescending(t => t.Bandwidth).ToList();

            if (videos.Count == 0 && _audioTracks.Count == 0)
            {
                ShowStatus(manifest.Protected
                    ? "This stream is DRM-protected and cannot be downloaded."
                    : "Nothing downloadable was found in this stream.", false, false);
                return;
            }

            StatusPanel.Visibility = Visibility.Collapsed;
            OptionsPanel.Visibility = Visibility.Visible;

            _updating = true;
            LstVideo.Items.Clear();
            foreach (TrackSpec v in videos) LstVideo.Items.Add(new Entry { Track = v, Text = VideoText(v) });
            if (_audioTracks.Count > 0) LstVideo.Items.Add(new Entry { Track = null, Text = NoVideoText });
            LstVideo.SelectedIndex = 0;
            _updating = false;
            LblVideo.Visibility = Visibility.Visible;

            RebuildAudio();
            TxtName.Text = DialogHelpers.CleanFileName(_pageTitle, "video");
            UpdateSummary();
        }

        private static string VideoText(TrackSpec v)
        {
            var parts = new List<string> { v.Label };
            if (v.Width > 0 && v.Height > 0) parts.Add(v.Width + "x" + v.Height);
            if (v.Bandwidth > 0) parts.Add(StreamLabels.Bitrate(v.Bandwidth));
            string codec = StreamLabels.CodecSummary(v.Codecs);
            if (codec.Length > 0) parts.Add(codec);
            parts.Add(v.Kind == TrackKind.Muxed ? "video + audio" : "video only");
            if (v.EstimatedBytes > 0) parts.Add("~" + DownloadItem.FormatBytes(v.EstimatedBytes));
            return string.Join("  -  ", parts);
        }

        private static string AudioText(TrackSpec a)
        {
            return a.EstimatedBytes > 0 ? a.Label + "  -  ~" + DownloadItem.FormatBytes(a.EstimatedBytes) : a.Label;
        }

        // ------------------------------------------------------------------
        // Selection logic
        // ------------------------------------------------------------------
        private TrackSpec? SelectedVideo() => (LstVideo.SelectedItem as Entry)?.Track;

        private TrackSpec? SelectedAudio() => (LstAudio.SelectedItem as Entry)?.Track;

        /// <summary>The audio list depends on the chosen video: hidden when the sound is inside the video, with a "no audio" line when it is separate.</summary>
        private void RebuildAudio()
        {
            TrackSpec? video = SelectedVideo();
            bool muxed = video != null && video.Kind == TrackKind.Muxed;
            TrackSpec? keep = SelectedAudio();

            _updating = true;
            LstAudio.Items.Clear();
            if (!muxed)
            {
                foreach (TrackSpec a in _audioTracks) LstAudio.Items.Add(new Entry { Track = a, Text = AudioText(a) });
                if (video != null && _audioTracks.Count > 0) LstAudio.Items.Add(new Entry { Track = null, Text = NoAudioText });
                LstAudio.SelectedIndex = PreferredAudioIndex(video, keep);
            }
            _updating = false;
            PanelAudio.Visibility = LstAudio.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private int PreferredAudioIndex(TrackSpec? video, TrackSpec? keep)
        {
            int first = -1;
            for (int i = 0; i < LstAudio.Items.Count; i++)
            {
                if (LstAudio.Items[i] is not Entry e || e.Track == null) continue;
                if (keep != null && ReferenceEquals(e.Track, keep)) return i;
                if (video != null && video.AudioGroup.Length > 0 &&
                    string.Equals(e.Track.AudioGroup, video.AudioGroup, StringComparison.OrdinalIgnoreCase)) return i;
                if (first < 0) first = i;
            }
            return first >= 0 ? first : 0;
        }

        private List<TrackSpec> CurrentSelection()
        {
            var list = new List<TrackSpec>();
            TrackSpec? video = SelectedVideo();
            TrackSpec? audio = SelectedAudio();
            if (video != null) list.Add(video);
            if (audio != null && (video == null || video.Kind != TrackKind.Muxed)) list.Add(audio);
            return list;
        }

        private void UpdateSummary()
        {
            List<TrackSpec> sel = CurrentSelection();
            if (sel.Count == 0)
            {
                TxtSummary.Text = "Nothing selected.";
                SetNote(Array.Empty<string>());
                BtnDownload.IsEnabled = false;
                return;
            }

            bool ffmpeg = FfmpegTool.Find() != null;
            ChkConvert.IsEnabled = ffmpeg;
            StreamPlan plan = StreamPlanner.Plan(sel, ffmpeg, ChkConvert.IsChecked == true);
            long size = sel.Sum(t => t.EstimatedBytes);
            string text = "Result: " + plan.Ext.TrimStart('.').ToUpperInvariant() + " file";
            if (size > 0) text += "  -  about " + DownloadItem.FormatBytes(size);
            TxtSummary.Text = text;

            var notes = new List<string>();
            if (_manifest != null) notes.AddRange(_manifest.Notes);
            if (_manifest != null && _manifest.Protected) notes.Add("Some tracks are DRM-protected and are not listed.");
            if (sel.Count >= 2 && !ffmpeg)
            {
                notes.Add("FFmpeg was not found, so video and audio will be saved as two separate files. " +
                          "For one merged file install it:  winget install Gyan.FFmpeg");
            }
            SetNote(notes);
            BtnDownload.IsEnabled = !_busy;
        }

        private void SetNote(IEnumerable<string> notes)
        {
            string text = string.Join("\n", notes);
            TxtNote.Text = text;
            TxtNote.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowProblem(string text)
        {
            TxtNote.Text = text;
            TxtNote.Visibility = Visibility.Visible;
        }

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------
        private async void LstSources_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || !IsLoaded) return;
            await LoadSelectedAsync();
        }

        private void LstVideo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || _manifest == null) return;
            RebuildAudio();
            UpdateSummary();
        }

        private void LstAudio_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || _manifest == null) return;
            UpdateSummary();
        }

        private void ChkConvert_Changed(object sender, RoutedEventArgs e)
        {
            if (_manifest == null) return;
            UpdateSummary();
        }

        private async void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            await LoadSelectedAsync();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            DialogHelpers.BrowseFolder(TxtFolder, "Save the download in...");
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DialogHelpers.DragCard(this);
        }

        // ------------------------------------------------------------------
        // Download button: the segment lists of the chosen qualities are read now, so errors show here and not later
        // ------------------------------------------------------------------
        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _current == null || _manifest == null) return;
            List<TrackSpec> selection = CurrentSelection();
            if (selection.Count == 0) return;

            string name = DialogHelpers.CleanFileName(TxtName.Text);
            if (name.Length == 0)
            {
                ShowProblem("Please type a file name.");
                return;
            }
            string folder = TxtFolder.Text.Trim();
            try
            {
                if (folder.Length == 0 || !System.IO.Path.IsPathRooted(folder)) throw new InvalidOperationException("Please choose a folder.");
                System.IO.Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                ShowProblem("This folder cannot be used: " + ex.Message);
                return;
            }

            _busy = true;
            BtnDownload.IsEnabled = false;
            ShowProblem("Preparing the download...");
            try
            {
                StreamHttpContext ctx = StreamHttpContext.From(_current, _cookieHeader);
                foreach (TrackSpec t in selection)
                {
                    await StreamManifestLoader.ResolveTrackAsync(t, ctx, _lifeCts.Token);
                    if (!string.IsNullOrEmpty(t.Error))
                    {
                        ShowProblem(t.Error);
                        return;
                    }
                    if (t.Segments.Count == 0)
                    {
                        ShowProblem("This quality has no media segments.");
                        return;
                    }
                }

                Result = new StreamPick
                {
                    Source = _current,
                    Manifest = _manifest,
                    Tracks = selection,
                    FileName = name,
                    Folder = folder,
                    ConvertToMp4 = ChkConvert.IsChecked == true,
                    Title = _pageTitle
                };
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                // window closed
            }
            catch (Exception ex)
            {
                AppLog.Warn("Preparing the stream download failed", ex);
                ShowProblem(Explain(ex, "Could not prepare the download: "));
            }
            finally
            {
                _busy = false;
                if (Result == null) UpdateSummary();
            }
        }
    }
}

