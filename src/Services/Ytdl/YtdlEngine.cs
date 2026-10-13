using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CustomBrowser.Helpers;

namespace CustomBrowser.Services.Ytdl
{
    /// <summary>Runs one yt-dlp download and turns its output into progress. Pause = the process is stopped; its .part files stay, so a later start continues.</summary>
    public static class YtdlEngine
    {
        private static readonly ConcurrentDictionary<int, Process> Running = new ConcurrentDictionary<int, Process>();

        static YtdlEngine()
        {
            // A browser that is closed must never leave yt-dlp / ffmpeg processes behind.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();
        }

        private static void KillAll()
        {
            foreach (Process p in Running.Values) YtdlProcess.KillQuietly(p);
        }

        public static async Task<YtdlResult> RunAsync(
            YtdlJobSpec spec, string exe, string? ffmpegPath, long limitBytesPerSecond,
            Action<YtdlProgress> report, CancellationToken ct)
        {
            Directory.CreateDirectory(spec.OutputFolder);
            List<string> args = BuildArguments(spec, ffmpegPath, limitBytesPerSecond);

            var tracker = new Tracker(spec, report);
            var errorTail = new StringBuilder();

            using var proc = new Process { StartInfo = YtdlProcess.CreateStartInfo(exe, args, spec.OutputFolder) };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) tracker.Handle(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (errorTail)
                {
                    errorTail.AppendLine(e.Data);
                    if (errorTail.Length > 6000) errorTail.Remove(0, errorTail.Length - 6000);
                }
            };

            try
            {
                if (!proc.Start()) throw new YtdlException("yt-dlp could not be started.");
            }
            catch (YtdlException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new YtdlException("yt-dlp could not be started: " + ex.Message);
            }

            int pid = proc.Id;
            Running[pid] = proc;
            try { proc.StandardInput.Close(); } catch (Exception) { /* not needed */ }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                YtdlProcess.KillQuietly(proc);
                try { proc.WaitForExit(3000); } catch (Exception) { /* best effort */ }
                throw;
            }
            finally
            {
                Running.TryRemove(pid, out _);
            }

            string errors;
            lock (errorTail) errors = errorTail.ToString();
            if (proc.ExitCode != 0)
            {
                AppLog.Debug("yt-dlp download failed: " + errors);
                throw new YtdlException(YtdlErrors.Explain(errors));
            }

            string final = FindFinalFile(spec);
            if (final.Length == 0) throw new YtdlException("yt-dlp finished, but the file was not found in " + spec.OutputFolder);
            return new YtdlResult { FinalPath = final };
        }

        private static List<string> BuildArguments(YtdlJobSpec spec, string? ffmpegPath, long limitBytesPerSecond)
        {
            // yt-dlp output templates use % for fields, so a literal % in the path must be doubled.
            string template = Path.Combine(spec.OutputFolder, spec.BaseName).Replace("%", "%%") + ".%(ext)s";

            var a = new List<string>
            {
                "--no-playlist", "--newline", "--no-colors", "--no-mtime",
                "--progress-template", "download:MINT|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s",
                "--progress-template", "postprocess:MINTPP|%(progress.status)s|%(progress.postprocessor)s",
                "-N", "4", "--retries", "10", "--fragment-retries", "10", "--socket-timeout", "30",
                "-f", spec.Selector,
                "-o", template
            };

            a.AddRange(YtdlTool.JsRuntimeArgsFor(spec.PageUrl));

            if (spec.AudioMp3)
            {
                a.Add("-x");
                a.Add("--audio-format");
                a.Add("mp3");
                a.Add("--audio-quality");
                a.Add("0");
            }
            else if (spec.Selector.Contains('+'))
            {
                a.Add("--merge-output-format");
                a.Add("mp4/mkv");
            }

            if (!string.IsNullOrEmpty(ffmpegPath))
            {
                a.Add("--ffmpeg-location");
                a.Add(ffmpegPath);
            }
            if (limitBytesPerSecond > 0)
            {
                a.Add("--limit-rate");
                a.Add(limitBytesPerSecond.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(spec.CookieFile) && File.Exists(spec.CookieFile))
            {
                a.Add("--cookies");
                a.Add(spec.CookieFile);
            }
            if (!string.IsNullOrEmpty(spec.UserAgent))
            {
                a.Add("--user-agent");
                a.Add(spec.UserAgent);
            }

            a.Add("--");
            a.Add(spec.PageUrl);
            return a;
        }

        /// <summary>The base name is unique (the caller made sure), so the newest non-partial "name.*" file is the result.</summary>
        private static string FindFinalFile(YtdlJobSpec spec)
        {
            try
            {
                if (!Directory.Exists(spec.OutputFolder)) return string.Empty;
                FileInfo? newest = Directory.GetFiles(spec.OutputFolder, spec.BaseName + ".*")
                    .Where(f => !YtdlStore.IsPartialFile(Path.GetFileName(f), spec.BaseName))
                    .Where(f => !Path.GetFileName(f).EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                             && !Path.GetFileName(f).EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                return newest == null ? string.Empty : newest.FullName;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Looking for the finished yt-dlp file failed", ex);
                return string.Empty;
            }
        }

        /// <summary>Reads yt-dlp's output lines. With two parts (video, then audio) the byte counts of the finished part are added up.</summary>
        private sealed class Tracker
        {
            private readonly YtdlJobSpec _spec;
            private readonly Action<YtdlProgress> _report;
            private long _doneBefore;
            private long _current;
            private long _currentTotal;
            private double _speed;
            private string _phase = "Downloading";
            private DateTime _last = DateTime.MinValue;

            public Tracker(YtdlJobSpec spec, Action<YtdlProgress> report)
            {
                _spec = spec;
                _report = report;
            }

            public void Handle(string line)
            {
                try
                {
                    if (line.StartsWith("MINTPP|", StringComparison.Ordinal))
                    {
                        string[] pp = line.Split('|');
                        string status = pp.Length > 1 ? pp[1] : string.Empty;
                        string name = pp.Length > 2 ? pp[2] : string.Empty;
                        if (status == "started") _phase = PhaseName(name);
                        else if (status == "finished") _phase = "Finishing";
                        _speed = 0;
                        Emit(true);
                        return;
                    }

                    if (!line.StartsWith("MINT|", StringComparison.Ordinal)) return;
                    string[] p = line.Split('|');
                    string state = p.Length > 1 ? p[1] : string.Empty;
                    long downloaded = ParseLong(p.Length > 2 ? p[2] : string.Empty);
                    long total = ParseLong(p.Length > 3 ? p[3] : string.Empty);
                    if (total <= 0) total = ParseLong(p.Length > 4 ? p[4] : string.Empty);
                    double speed = ParseDouble(p.Length > 5 ? p[5] : string.Empty);

                    if (state == "finished")
                    {
                        _doneBefore += downloaded > 0 ? downloaded : total;
                        _current = 0;
                        _currentTotal = 0;
                        _speed = 0;
                        _phase = "Downloading";
                        Emit(true);
                        return;
                    }

                    _current = downloaded;
                    _currentTotal = total;
                    _speed = speed;
                    _phase = "Downloading";
                    Emit(false);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("Could not read a yt-dlp progress line", ex);
                }
            }

            private void Emit(bool force)
            {
                DateTime now = DateTime.UtcNow;
                if (!force && (now - _last).TotalMilliseconds < 300) return;
                _last = now;

                long done = _doneBefore + _current;
                long total = Math.Max(_doneBefore + _currentTotal, _spec.EstimatedBytes);
                double fraction = total > 0 ? Math.Min(0.999, (double)done / total) : 0.0;
                _report(new YtdlProgress
                {
                    Bytes = done,
                    EstimatedTotalBytes = total,
                    Fraction = fraction,
                    SpeedBytesPerSecond = _speed,
                    Phase = _phase
                });
            }

            private static string PhaseName(string postprocessor)
            {
                switch (postprocessor)
                {
                    case "Merger": return "Merging";
                    case "ExtractAudio": return "Converting";
                    case "FFmpegVideoConvertor":
                    case "FFmpegVideoRemuxer": return "Converting";
                    case "MoveFiles": return "Finishing";
                    default: return "Processing";
                }
            }

            private static long ParseLong(string s)
            {
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) return v;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0) return (long)d;
                return 0;
            }

            private static double ParseDouble(string s)
            {
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0 ? d : 0.0;
            }
        }
    }
}

