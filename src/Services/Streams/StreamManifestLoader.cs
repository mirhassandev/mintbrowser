using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CustomBrowser.Services.Streams
{
    /// <summary>Downloads and reads a detected playlist / manifest into a list of tracks the user can pick from.</summary>
    public static class StreamManifestLoader
    {
        public static async Task<StreamManifest> LoadAsync(DetectedStream d, string cookieHeader, CancellationToken ct)
        {
            StreamHttpContext ctx = StreamHttpContext.From(d, cookieHeader);

            if (d.Protocol == StreamProtocol.Progressive)
            {
                var single = new StreamManifest { Protocol = StreamProtocol.Progressive, SourceUrl = d.Url };
                string ext = HlsParser.ExtensionOf(d.Url);
                bool audio = d.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                             ext == ".mp3" || ext == ".m4a" || ext == ".aac" || ext == ".ogg" || ext == ".opus" || ext == ".flac" || ext == ".wav";
                if (ext.Length == 0 || ext.Length > 6) ext = audio ? ".m4a" : ".mp4";
                var t = new TrackSpec
                {
                    Id = "p0",
                    Kind = audio ? TrackKind.Audio : TrackKind.Muxed,
                    Label = audio ? "Original audio file" : "Original video file",
                    RawExt = ext,
                    SingleFile = true,
                    KnownBytes = d.ContentLength,
                    PlaylistUrl = d.Url
                };
                t.Segments.Add(new SegmentSpec { Url = d.Url });
                single.Tracks.Add(t);
                return single;
            }

            string text = await StreamHttp.GetStringAsync(d.Url, ctx, ct).ConfigureAwait(false);

            if (d.Protocol == StreamProtocol.Dash || text.TrimStart().StartsWith('<'))
            {
                return DashParser.Parse(text, d.Url);
            }

            if (!text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                return new StreamManifest { Protocol = StreamProtocol.Hls, SourceUrl = d.Url, Error = "This address is not a media playlist" };
            }

            if (HlsParser.IsMaster(text))
            {
                StreamManifest master = HlsParser.ParseMaster(text, d.Url);
                if (!string.IsNullOrEmpty(master.Error)) return master;

                // One playlist is read up front so every quality gets a duration (needed for the size estimate).
                TrackSpec? sample = master.Tracks.FirstOrDefault(x => x.Kind != TrackKind.Audio) ?? master.Tracks[0];
                try
                {
                    string sampleText = await StreamHttp.GetStringAsync(sample.PlaylistUrl, ctx, ct).ConfigureAwait(false);
                    var probe = new TrackSpec { Kind = sample.Kind };
                    HlsParser.ParseMedia(sampleText, sample.PlaylistUrl, probe);
                    master.DurationSeconds = probe.DurationSeconds;
                    master.IsLive = probe.Error.Contains("live", StringComparison.OrdinalIgnoreCase);
                    if (probe.Error.Length > 0 && probe.Error.Contains("DRM", StringComparison.Ordinal)) master.Protected = true;
                    foreach (TrackSpec t in master.Tracks) t.DurationSeconds = probe.DurationSeconds;
                    if (master.IsLive) master.Error = probe.Error;
                    else if (master.Protected) master.Error = probe.Error;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    CustomBrowser.Helpers.AppLog.Debug("Could not read a sample playlist for the size estimate", ex);
                }
                return master;
            }

            // A media playlist on its own: there is only one quality.
            var manifest = new StreamManifest { Protocol = StreamProtocol.Hls, SourceUrl = d.Url, IsMaster = false };
            var track = new TrackSpec { Id = "m0", Kind = TrackKind.Muxed, Label = "Default quality", PlaylistUrl = d.Url };
            HlsParser.ParseMedia(text, d.Url, track);
            if (track.RawExt == ".aac" || track.RawExt == ".mp3" || track.RawExt == ".m4a")
            {
                track.Kind = TrackKind.Audio;
                track.Label = "Audio";
            }
            manifest.DurationSeconds = track.DurationSeconds;
            manifest.Tracks.Add(track);
            if (!string.IsNullOrEmpty(track.Error)) manifest.Error = track.Error;
            return manifest;
        }

        /// <summary>HLS variants only list a playlist address; the segment list is read when the track is really downloaded.</summary>
        public static async Task ResolveTrackAsync(TrackSpec t, StreamHttpContext ctx, CancellationToken ct)
        {
            if (t.Segments.Count > 0 || t.SingleFile || string.IsNullOrEmpty(t.PlaylistUrl)) return;
            string text = await StreamHttp.GetStringAsync(t.PlaylistUrl, ctx, ct).ConfigureAwait(false);
            HlsParser.ParseMedia(text, t.PlaylistUrl, t);
        }
    }

    /// <summary>Works out the file type of the result and where FFmpeg is needed.</summary>
    public static class StreamPlanner
    {
        public static StreamPlan Plan(System.Collections.Generic.IList<TrackSpec> tracks, bool ffmpegAvailable, bool convert)
        {
            var plan = new StreamPlan();
            if (tracks.Count >= 2)
            {
                TrackSpec video = tracks[0];
                TrackSpec audio = tracks[1];
                if (ffmpegAvailable)
                {
                    bool mkv = video.RawExt == ".webm" || audio.RawExt == ".webm" || NeedsMatroska(video.Codecs) || NeedsMatroska(audio.Codecs);
                    plan.UseFfmpeg = true;
                    plan.Ext = mkv ? ".mkv" : ".mp4";
                    plan.Format = mkv ? "matroska" : "mp4";
                }
                else
                {
                    plan.Separate = true;
                    plan.Ext = video.RawExt;
                }
                return plan;
            }

            TrackSpec t = tracks[0];
            if (t.RawExt == ".ts" && convert && ffmpegAvailable)
            {
                plan.UseFfmpeg = true;
                plan.Ext = t.Kind == TrackKind.Audio ? ".m4a" : ".mp4";
                plan.Format = "mp4";
            }
            else
            {
                plan.Ext = t.RawExt;
            }
            return plan;
        }

        private static bool NeedsMatroska(string codecs)
        {
            string c = (codecs ?? string.Empty).ToLowerInvariant();
            return c.Contains("vp9") || c.Contains("vp09") || c.Contains("vp8") || c.Contains("av01") ||
                   c.Contains("opus") || c.Contains("vorbis") || c.Contains("flac");
        }
    }

    /// <summary>Finds ffmpeg.exe (next to the app, in a Tools folder, or on PATH) and runs it.</summary>
    public static class FfmpegTool
    {
        private static string? _cached;

        public static string? Find()
        {
            if (_cached != null && File.Exists(_cached)) return _cached;
            _cached = Search();
            return _cached;
        }

        private static string? Search()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string[] local =
                {
                    Path.Combine(baseDir, "Tools", "ffmpeg.exe"),
                    Path.Combine(baseDir, "ffmpeg.exe"),
                    Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe")
                };
                foreach (string c in local)
                {
                    if (File.Exists(c)) return c;
                }
                string? onPath = CustomBrowser.Helpers.ToolLocator.FindOnPath("ffmpeg.exe");
                if (onPath != null) return onPath;
            }
            catch (Exception ex)
            {
                CustomBrowser.Helpers.AppLog.Debug("Searching for ffmpeg failed", ex);
            }
            return null;
        }

        /// <summary>Runs ffmpeg and returns the last lines of its error output on failure.</summary>
        public static async Task<(bool Ok, string Log)> RunAsync(string exe, System.Collections.Generic.IEnumerable<string> args, CancellationToken ct)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            var log = new System.Text.StringBuilder();
            using var proc = new System.Diagnostics.Process { StartInfo = psi };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (log)
                {
                    log.AppendLine(e.Data);
                    if (log.Length > 4000) log.Remove(0, log.Length - 4000);
                }
            };
            proc.OutputDataReceived += (_, _) => { };

            try
            {
                if (!proc.Start()) return (false, "ffmpeg could not be started");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch (Exception) { }
                throw;
            }
            string text;
            lock (log) text = log.ToString();
            return (proc.ExitCode == 0, text);
        }
    }

    /// <summary>The job details (headers, cookies, chosen tracks) are kept in %LocalAppData% so a paused stream can continue after a restart.</summary>
    public static class StreamStore
    {
        private static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CustomBrowser", "streams");

        public static string SpecPath(string id) => Path.Combine(Root, id + ".json");

        /// <summary>Folder with the downloaded pieces. Next to the target file, so the final move never leaves the disk.</summary>
        public static string WorkDir(string outputPath) => outputPath + ".parts";

        public static void WriteSpec(string id, StreamJobSpec spec)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(SpecPath(id), System.Text.Json.JsonSerializer.Serialize(spec), new System.Text.UTF8Encoding(false));
        }

        public static StreamJobSpec? ReadSpec(string id)
        {
            try
            {
                string path = SpecPath(id);
                if (!File.Exists(path)) return null;
                return System.Text.Json.JsonSerializer.Deserialize<StreamJobSpec>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                CustomBrowser.Helpers.AppLog.Warn("Could not read the saved stream details", ex);
                return null;
            }
        }

        public static void DeleteSpec(string id)
        {
            try { File.Delete(SpecPath(id)); }
            catch (Exception ex) { CustomBrowser.Helpers.AppLog.Debug("Could not delete the saved stream details", ex); }
        }

        public static void DeleteWork(string outputPath)
        {
            try
            {
                string dir = WorkDir(outputPath);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                CustomBrowser.Helpers.AppLog.Debug("Could not delete the temporary stream folder", ex);
            }
        }
    }
}

