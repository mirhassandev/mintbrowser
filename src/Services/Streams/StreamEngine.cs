using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CustomBrowser.Helpers;

namespace CustomBrowser.Services.Streams
{
    /// <summary>
    /// Downloads every segment of the chosen tracks in parallel, decrypts AES-128 segments, glues the pieces together
    /// and (when FFmpeg is available) merges video and audio. Finished segments stay on disk, so pause / resume
    /// continues exactly where it stopped.
    /// </summary>
    public static class StreamEngine
    {
        private const int MaxAttempts = 5;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private sealed class WorkItem
        {
            public int TrackIndex;
            public int Index; // -1 = initialization segment
            public SegmentSpec Segment = new SegmentSpec();
            public string Path = string.Empty;
            public bool Single;
            public bool SizeCounted;
        }

        private sealed class AttemptCounter
        {
            public long Value;
        }

        private sealed class Limiter
        {
            private readonly object _gate = new object();
            private long _next;

            public async Task WaitAsync(long bytes, long limitBytesPerSecond, CancellationToken ct)
            {
                if (limitBytesPerSecond <= 0 || bytes <= 0) return;
                long delayMs;
                lock (_gate)
                {
                    long now = Stopwatch.GetTimestamp();
                    long cost = (long)(bytes * (double)Stopwatch.Frequency / limitBytesPerSecond);
                    if (_next < now) _next = now;
                    _next += cost;
                    long wait = _next - now;
                    delayMs = wait * 1000 / Stopwatch.Frequency;
                }
                if (delayMs > 5) await Task.Delay((int)Math.Min(delayMs, 5000), ct).ConfigureAwait(false);
            }
        }

        private sealed class RunState
        {
            public long Bytes;
            /// <summary>Bytes really received from the network in this run (used for the speed; resumed pieces do not count).</summary>
            public long NetBytes;
            public int DoneSegments;
            public int TotalSegments;
            public long KnownTotalBytes;
            public bool AllSingle;
            public long EstimatedBytes;
            public readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> Keys = new ConcurrentDictionary<string, Lazy<Task<byte[]>>>();
            public readonly Limiter Limit = new Limiter();
        }

        public static async Task<StreamResult> RunAsync(StreamJobSpec spec, string workDir, Action<StreamProgress> report,
            Func<long> limitProvider, CancellationToken ct)
        {
            if (spec.Tracks.Count == 0) throw new InvalidOperationException("Nothing was selected to download");
            foreach (TrackSpec check in spec.Tracks)
            {
                if (!string.IsNullOrEmpty(check.Error)) throw new InvalidOperationException(check.Error);
                if (check.Segments.Count == 0) throw new InvalidOperationException("The stream has no media segments");
            }

            Directory.CreateDirectory(workDir);
            StreamHttpContext ctx = StreamHttpContext.From(spec);
            var state = new RunState();
            var items = new List<WorkItem>();

            for (int ti = 0; ti < spec.Tracks.Count; ti++)
            {
                TrackSpec track = spec.Tracks[ti];
                string dir = System.IO.Path.Combine(workDir, "t" + ti.ToString(Inv));
                Directory.CreateDirectory(dir);
                if (!string.IsNullOrEmpty(track.InitUrl))
                {
                    items.Add(new WorkItem
                    {
                        TrackIndex = ti,
                        Index = -1,
                        Path = System.IO.Path.Combine(dir, "init.bin"),
                        Single = false,
                        Segment = new SegmentSpec { Url = track.InitUrl, RangeStart = track.InitRangeStart, RangeLength = track.InitRangeLength }
                    });
                }
                for (int si = 0; si < track.Segments.Count; si++)
                {
                    items.Add(new WorkItem
                    {
                        TrackIndex = ti,
                        Index = si,
                        Path = System.IO.Path.Combine(dir, si.ToString("D6", Inv) + ".seg"),
                        Single = track.SingleFile,
                        Segment = track.Segments[si]
                    });
                }
            }

            state.TotalSegments = items.Count;
            state.AllSingle = spec.Tracks.All(x => x.SingleFile);
            state.EstimatedBytes = spec.Tracks.Sum(x => x.EstimatedBytes);

            using var reporterCts = new CancellationTokenSource();
            Task reporter = RunReporter(async () =>
            {
                long lastBytes = 0;
                long lastTick = Stopwatch.GetTimestamp();
                double speed = 0;
                while (!reporterCts.IsCancellationRequested)
                {
                    try { await Task.Delay(500, reporterCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    long now = Stopwatch.GetTimestamp();
                    long bytes = Interlocked.Read(ref state.Bytes);
                    long net = Interlocked.Read(ref state.NetBytes);
                    double dt = (now - lastTick) / (double)Stopwatch.Frequency;
                    if (dt > 0)
                    {
                        double instant = (net - lastBytes) / dt;
                        speed = speed <= 0 ? instant : speed * 0.6 + instant * 0.4;
                    }
                    lastBytes = net;
                    lastTick = now;
                    try { report(Snapshot(state, bytes, speed, "Downloading")); }
                    catch (Exception ex) { AppLog.Debug("Stream progress callback failed", ex); }
                }
            });

            try
            {
                int parallel = Math.Clamp(spec.Concurrency, 1, 16);
                await Parallel.ForEachAsync(items,
                    new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct },
                    async (item, token) =>
                    {
                        await DownloadWithRetryAsync(item, ctx, state, limitProvider, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
            }
            finally
            {
                reporterCts.Cancel();
                try { await reporter.ConfigureAwait(false); }
                catch (Exception reporterEx) { AppLog.Debug("The stream progress reporter ended with an error", reporterEx); }
            }

            ct.ThrowIfCancellationRequested();
            long finalBytes = Interlocked.Read(ref state.Bytes);
            try { report(Snapshot(state, finalBytes, 0, "Merging...")); }
            catch (Exception ex) { AppLog.Debug("Stream progress callback failed", ex); }

            var rawPaths = new List<string>();
            for (int ti = 0; ti < spec.Tracks.Count; ti++)
            {
                rawPaths.Add(await AssembleTrackAsync(spec.Tracks[ti], ti, workDir, ct).ConfigureAwait(false));
            }

            return await FinalizeAsync(spec, rawPaths, ct).ConfigureAwait(false);
        }

        /// <summary>Starts the progress reporter. It is stopped through its own token, so the caller's token is deliberately not passed to Task.Run.</summary>
        private static Task RunReporter(Func<Task> work) => Task.Run(work, CancellationToken.None);

        private static StreamProgress Snapshot(RunState s, long bytes, double speed, string phase)
        {
            int done = Volatile.Read(ref s.DoneSegments);
            double fraction;
            long estimated;
            if (s.AllSingle)
            {
                long known = Interlocked.Read(ref s.KnownTotalBytes);
                fraction = known > 0 ? Math.Min(1.0, (double)bytes / known) : 0;
                estimated = known > 0 ? known : s.EstimatedBytes;
            }
            else
            {
                fraction = s.TotalSegments > 0 ? (double)done / s.TotalSegments : 0;
                estimated = done >= 3 ? (long)(bytes / (double)done * s.TotalSegments) : s.EstimatedBytes;
            }
            return new StreamProgress
            {
                DoneSegments = done,
                TotalSegments = s.TotalSegments,
                Bytes = bytes,
                EstimatedTotalBytes = estimated,
                SpeedBytesPerSecond = speed,
                Phase = phase,
                Fraction = fraction
            };
        }

        // ------------------------------------------------------------------
        // One segment
        // ------------------------------------------------------------------
        private static async Task DownloadWithRetryAsync(WorkItem item, StreamHttpContext ctx, RunState state,
            Func<long> limitProvider, CancellationToken ct)
        {
            if (File.Exists(item.Path))
            {
                // finished in an earlier run (resume): only count it
                long existing = new FileInfo(item.Path).Length;
                Interlocked.Add(ref state.Bytes, existing);
                Interlocked.Increment(ref state.DoneSegments);
                if (item.Single) Interlocked.Add(ref state.KnownTotalBytes, existing);
                return;
            }

            var counter = new AttemptCounter();
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await DownloadOnceAsync(item, ctx, state, limitProvider, counter, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref state.DoneSegments);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (StreamHttpException ex) when (ex.StatusCode == 401 || ex.StatusCode == 403 || ex.StatusCode == 404 || ex.StatusCode == 410)
                {
                    if (attempt >= 2) throw;
                    Interlocked.Add(ref state.Bytes, -counter.Value);
                    counter.Value = 0;
                    await Task.Delay(700, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException timeout) when (!ct.IsCancellationRequested && attempt >= MaxAttempts)
                {
                    throw new TimeoutException("The server stopped sending data", timeout);
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    AppLog.Debug("Stream segment failed, trying again: " + item.Segment.Url, ex);
                    Interlocked.Add(ref state.Bytes, -counter.Value);
                    counter.Value = 0;
                    await Task.Delay(attempt * 900, ct).ConfigureAwait(false);
                }
            }
        }

        private static async Task DownloadOnceAsync(WorkItem item, StreamHttpContext ctx, RunState state,
            Func<long> limitProvider, AttemptCounter counter, CancellationToken ct)
        {
            SegmentSpec seg = item.Segment;
            string tmp = item.Path + ".tmp";
            bool encrypted = item.Index >= 0 && !string.IsNullOrEmpty(seg.KeyUrl);

            long resumeFrom = 0;
            if (item.Single && !encrypted && File.Exists(tmp)) resumeFrom = new FileInfo(tmp).Length;
            else if (File.Exists(tmp)) File.Delete(tmp);

            using HttpRequestMessage req = StreamHttp.BuildRequest(seg.Url, ctx, seg.RangeStart, seg.RangeLength, resumeFrom);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(45));
            using HttpResponseMessage resp = await StreamHttp.SendAsync(req, linked.Token).ConfigureAwait(false);

            if (resumeFrom > 0 && (int)resp.StatusCode == 416)
            {
                // the partial file already holds everything the server has
                Interlocked.Add(ref state.Bytes, resumeFrom);
                counter.Value += resumeFrom;
                if (item.Single && !item.SizeCounted) { item.SizeCounted = true; Interlocked.Add(ref state.KnownTotalBytes, resumeFrom); }
                File.Move(tmp, item.Path, true);
                return;
            }

            StreamHttp.EnsureOk(resp);
            if (resumeFrom > 0 && resp.StatusCode != HttpStatusCode.PartialContent) resumeFrom = 0; // range ignored: start over

            long? contentLength = resp.Content.Headers.ContentLength;
            if (item.Single && !item.SizeCounted && contentLength.HasValue)
            {
                item.SizeCounted = true;
                Interlocked.Add(ref state.KnownTotalBytes, contentLength.Value + resumeFrom);
            }
            if (resumeFrom > 0)
            {
                Interlocked.Add(ref state.Bytes, resumeFrom);
                counter.Value += resumeFrom;
            }

            using Stream body = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            byte[] buffer = new byte[64 * 1024];

            if (encrypted)
            {
                var ms = new MemoryStream();
                int read;
                while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), linked.Token).ConfigureAwait(false)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    Interlocked.Add(ref state.NetBytes, read);
                    Interlocked.Add(ref state.Bytes, read);
                    counter.Value += read;
                    linked.CancelAfter(TimeSpan.FromSeconds(45));
                    await state.Limit.WaitAsync(read, limitProvider(), ct).ConfigureAwait(false);
                }
                byte[] key = await GetKeyAsync(seg.KeyUrl, ctx, state).ConfigureAwait(false);
                byte[] plain = Decrypt(ms.ToArray(), key, seg);
                await File.WriteAllBytesAsync(tmp, plain, ct).ConfigureAwait(false);
            }
            else
            {
                using (var fs = new FileStream(tmp, resumeFrom > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, true))
                {
                    int got;
                    while ((got = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), linked.Token).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, got), ct).ConfigureAwait(false);
                        Interlocked.Add(ref state.NetBytes, got);
                        Interlocked.Add(ref state.Bytes, got);
                        counter.Value += got;
                        linked.CancelAfter(TimeSpan.FromSeconds(45));
                        await state.Limit.WaitAsync(got, limitProvider(), ct).ConfigureAwait(false);
                    }
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }
            }

            File.Move(tmp, item.Path, true);
        }

        private static async Task<byte[]> GetKeyAsync(string url, StreamHttpContext ctx, RunState state)
        {
            Lazy<Task<byte[]>> lazy = state.Keys.GetOrAdd(url, u =>
                new Lazy<Task<byte[]>>(() => StreamHttp.GetBytesAsync(u, ctx, CancellationToken.None, -1, -1, 4096)));
            try
            {
                return await lazy.Value.ConfigureAwait(false);
            }
            catch (Exception)
            {
                state.Keys.TryRemove(url, out _);
                throw;
            }
        }

        private static byte[] BuildIv(SegmentSpec seg)
        {
            byte[] iv = new byte[16];
            string hex = (seg.Iv ?? string.Empty).Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            if (hex.Length > 0)
            {
                if (hex.Length % 2 == 1) hex = "0" + hex;
                try
                {
                    byte[] parsed = Convert.FromHexString(hex);
                    int count = Math.Min(16, parsed.Length);
                    Array.Copy(parsed, parsed.Length - count, iv, 16 - count, count);
                    return iv;
                }
                catch (FormatException)
                {
                    // fall through to the sequence-number IV
                }
            }
            long sequence = seg.Sequence;
            for (int i = 15; i >= 8; i--)
            {
                iv[i] = (byte)(sequence & 0xFF);
                sequence >>= 8;
            }
            return iv;
        }

        private static byte[] Decrypt(byte[] data, byte[] key, SegmentSpec seg)
        {
            if (key.Length != 16) throw new InvalidDataException("The decryption key is not valid");
            byte[] iv = BuildIv(seg);
            using Aes aes = Aes.Create();
            aes.Key = key;
            try
            {
                return aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
            }
            catch (CryptographicException)
            {
                // damaged padding at the end of a segment: keep everything that can be decoded
                int usable = data.Length - (data.Length % 16);
                if (usable <= 0) throw;
                byte[] cut = new byte[usable];
                Array.Copy(data, cut, usable);
                return aes.DecryptCbc(cut, iv, PaddingMode.None);
            }
        }

        // ------------------------------------------------------------------
        // Putting the pieces together
        // ------------------------------------------------------------------
        private static async Task<string> AssembleTrackAsync(TrackSpec track, int trackIndex, string workDir, CancellationToken ct)
        {
            string dir = System.IO.Path.Combine(workDir, "t" + trackIndex.ToString(Inv));
            string raw = System.IO.Path.Combine(workDir, "track" + trackIndex.ToString(Inv) + ".raw");
            using (var output = new FileStream(raw, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            {
                if (!string.IsNullOrEmpty(track.InitUrl))
                {
                    await AppendFileAsync(System.IO.Path.Combine(dir, "init.bin"), output, ct).ConfigureAwait(false);
                }
                for (int si = 0; si < track.Segments.Count; si++)
                {
                    await AppendFileAsync(System.IO.Path.Combine(dir, si.ToString("D6", Inv) + ".seg"), output, ct).ConfigureAwait(false);
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            return raw;
        }

        private static async Task AppendFileAsync(string path, Stream output, CancellationToken ct)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
            await input.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            string ext = System.IO.Path.GetExtension(path);
            for (int i = 1; i < 1000; i++)
            {
                string candidate = System.IO.Path.Combine(dir, name + " (" + i.ToString(Inv) + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return path;
        }

        /// <summary>Real media never starts like a text playlist or script. If a "video" does, FFmpeg must not be asked to open it.</summary>
        private static bool LooksLikeScript(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] head = new byte[64];
                int n = fs.Read(head, 0, head.Length);
                string text = System.Text.Encoding.UTF8.GetString(head, 0, n).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                return text.StartsWith("#EXT", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("ffconcat", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith('<');
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not check a downloaded piece before FFmpeg", ex);
                return false;
            }
        }

        private static async Task<StreamResult> FinalizeAsync(StreamJobSpec spec, List<string> rawPaths, CancellationToken ct)
        {
            string? ffmpeg = FfmpegTool.Find();
            StreamPlan plan = StreamPlanner.Plan(spec.Tracks, ffmpeg != null, spec.ConvertToMp4);
            string outputDir = System.IO.Path.GetDirectoryName(spec.OutputPath) ?? string.Empty;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(spec.OutputPath);

            bool disguisedScript = false;
            foreach (string piece in rawPaths) { if (LooksLikeScript(piece)) disguisedScript = true; }
            if (disguisedScript) AppLog.Warn("A downloaded piece looks like a playlist or script, not media - FFmpeg was not used");
            if (plan.UseFfmpeg && ffmpeg != null && !disguisedScript)
            {
                string finalPath = UniquePath(System.IO.Path.ChangeExtension(spec.OutputPath, plan.Ext));
                string tmpOut = finalPath + ".tmp";
                var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-nostdin" };
                if (rawPaths.Count >= 2)
                {
                    args.AddRange(new[] { "-protocol_whitelist", "file", "-i", rawPaths[0], "-protocol_whitelist", "file", "-i", rawPaths[1], "-map", "0:v:0", "-map", "1:a:0", "-c", "copy" });
                }
                else
                {
                    args.AddRange(new[] { "-protocol_whitelist", "file", "-i", rawPaths[0], "-map", "0:v?", "-map", "0:a?", "-c", "copy" });
                }
                if (plan.Format == "mp4") { args.Add("-movflags"); args.Add("+faststart"); }
                args.AddRange(new[] { "-f", plan.Format, tmpOut });

                (bool ok, string log) = await FfmpegTool.RunAsync(ffmpeg, args, ct).ConfigureAwait(false);
                if (ok && File.Exists(tmpOut))
                {
                    File.Move(tmpOut, finalPath, true);
                    return new StreamResult { FinalPath = finalPath };
                }
                try { if (File.Exists(tmpOut)) File.Delete(tmpOut); } catch (Exception tmpOutEx) { AppLog.Debug("Could not delete the temporary FFmpeg output", tmpOutEx); }
                AppLog.Warn("FFmpeg could not finish the stream: " + log.Trim());
                // fall through: keep the pieces as they are
                return KeepRawFiles(spec, rawPaths, outputDir, baseName, "FFmpeg could not convert the stream, the original pieces were kept");
            }

            // Reached with UseFfmpeg only when FFmpeg was skipped (a piece looked like a playlist): keep every piece instead of dropping the audio.
            if (plan.Separate || plan.UseFfmpeg)
            {
                return KeepRawFiles(spec, rawPaths, outputDir, baseName, "FFmpeg was not used, so the downloaded pieces were kept as they are");
            }

            string single = UniquePath(System.IO.Path.ChangeExtension(spec.OutputPath, plan.Ext));
            File.Move(rawPaths[0], single, true);
            return new StreamResult { FinalPath = single };
        }

        private static StreamResult KeepRawFiles(StreamJobSpec spec, List<string> rawPaths, string outputDir, string baseName, string note)
        {
            if (rawPaths.Count >= 2)
            {
                string video = UniquePath(System.IO.Path.Combine(outputDir, baseName + ".video" + spec.Tracks[0].RawExt));
                string audio = UniquePath(System.IO.Path.Combine(outputDir, baseName + ".audio" + spec.Tracks[1].RawExt));
                File.Move(rawPaths[0], video, true);
                File.Move(rawPaths[1], audio, true);
                return new StreamResult { FinalPath = video, Note = note };
            }
            string single = UniquePath(System.IO.Path.Combine(outputDir, baseName + spec.Tracks[0].RawExt));
            File.Move(rawPaths[0], single, true);
            return new StreamResult { FinalPath = single, Note = note };
        }
    }
}

