using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CustomBrowser.Helpers;
using CustomBrowser.Models;
using CustomBrowser.Services.Streams;
using CustomBrowser.Services.Ytdl;
using CustomStatus = CustomBrowser.Models.DownloadStatus;

namespace CustomBrowser.Services
{
    /// <summary>
    /// Downloads that are not done by the Downloader library: HLS / DASH streams and yt-dlp. They share the same queue,
    /// the same "simultaneous downloads" slots and the same pause / resume / restart / delete behaviour. Only the engine differs.
    /// </summary>
    public partial class DirectDownloadService
    {
        private sealed class ExternalProgress
        {
            public long Bytes { get; init; }
            public long EstimatedTotalBytes { get; init; }
            public double Fraction { get; init; }
            public double SpeedBytesPerSecond { get; init; }
            public string Phase { get; init; } = "Downloading";
        }

        private sealed class ExternalRun
        {
            public required DownloadItem Item { get; init; }
            public required bool CleanFirst { get; init; }
            public required Action<ExternalProgress> Report { get; init; }
            public required Func<long> Limit { get; init; }
            public required CancellationToken Token { get; init; }
        }

        private sealed class ExternalOutcome
        {
            public string FinalPath { get; init; } = string.Empty;
            /// <summary>Runs once the item is marked as completed (removes temporary files).</summary>
            public Action? AfterCompleted { get; init; }
        }

        /// <summary>Everything that differs between the engines.</summary>
        private sealed class ExternalKind
        {
            public required string Name { get; init; }
            public required Func<ExternalRun, Task<ExternalOutcome>> Run { get; init; }
            public required Action<string> DeleteSpec { get; init; }
            /// <summary>Called before the saved details are deleted; returns the cleanup that runs after the transfer has stopped.</summary>
            public required Func<DownloadItem, Action> PrepareCleanup { get; init; }
            public required Func<Exception, string> Explain { get; init; }
        }

        private sealed class ExternalSession
        {
            public CancellationTokenSource Cts { get; } = new CancellationTokenSource();
            public TaskCompletionSource<bool> Finished { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static readonly ExternalKind StreamKind = new ExternalKind
        {
            Name = "Stream",
            Run = RunStreamAsync,
            DeleteSpec = StreamStore.DeleteSpec,
            PrepareCleanup = item =>
            {
                string output = item.SavePath;
                return () => StreamStore.DeleteWork(output);
            },
            Explain = FriendlyStreamError
        };

        private static readonly ExternalKind YtdlKind = new ExternalKind
        {
            Name = "yt-dlp",
            Run = RunYtdlAsync,
            DeleteSpec = YtdlStore.DeleteSpec,
            PrepareCleanup = item =>
            {
                YtdlJobSpec? spec = YtdlStore.ReadSpec(item.Id);
                return () =>
                {
                    if (spec != null) YtdlStore.CleanPartials(spec.OutputFolder, spec.BaseName); // unfinished pieces are never worth keeping
                };
            },
            Explain = FriendlyYtdlError
        };

        private readonly ConcurrentDictionary<string, ExternalSession> _externalSessions = new ConcurrentDictionary<string, ExternalSession>();
        private readonly ConcurrentDictionary<string, Task> _externalClosing = new ConcurrentDictionary<string, Task>();

        private static ExternalKind? KindOf(string id)
        {
            DownloadItem? item = FindItem(id);
            if (item == null) return null;
            switch (item.Type)
            {
                case DownloadType.Stream: return StreamKind;
                case DownloadType.Ytdl: return YtdlKind;
                default: return null;
            }
        }

        private bool IsCurrentExternal(string id, ExternalSession session)
        {
            return _externalSessions.TryGetValue(id, out ExternalSession? current) && ReferenceEquals(current, session);
        }

        /// <summary>The job details must already be saved with StreamStore.WriteSpec(item.Id, ...).</summary>
        public void EnqueueStream(DownloadItem item)
        {
            Enqueue(item, job => LaunchExternal(StreamKind, item, job, false));
        }

        /// <summary>The job details must already be saved with YtdlStore.WriteSpec(item.Id, ...).</summary>
        public void EnqueueYtdl(DownloadItem item)
        {
            Enqueue(item, job => LaunchExternal(YtdlKind, item, job, false));
        }

        private void LaunchExternal(ExternalKind kind, DownloadItem item, QueueJob job, bool cleanFirst)
        {
            string id = item.Id;
            var session = new ExternalSession();
            Task? previous = null;
            if (_externalSessions.TryRemove(id, out ExternalSession? old))
            {
                previous = old.Finished.Task;
                try { old.Cts.Cancel(); } catch (Exception ex) { AppLog.Debug("Cancelling the replaced " + kind.Name + " session failed", ex); }
            }
            if (_externalClosing.TryRemove(id, out Task? closing) && previous == null) previous = closing;
            _externalSessions[id] = session;

            _ = Task.Run(async () =>
            {
                try
                {
                    // The old transfer must be gone (and its files released) before the same files are used again.
                    if (previous != null) await Task.WhenAny(previous, Task.Delay(5000)).ConfigureAwait(false);
                    if (job.Cancelled || session.Cts.IsCancellationRequested) return;

                    item.ErrorMessage = string.Empty;
                    item.Speed = "-";
                    item.Status = CustomStatus.Downloading;

                    ExternalOutcome outcome = await kind.Run(new ExternalRun
                    {
                        Item = item,
                        CleanFirst = cleanFirst,
                        Limit = ComputePerDownloadLimit,
                        Token = session.Cts.Token,
                        Report = p =>
                        {
                            if (!IsCurrentExternal(id, session) || item.Status == CustomStatus.Paused) return;
                            item.ReceivedBytes = p.Bytes;
                            item.TotalBytes = p.EstimatedTotalBytes > p.Bytes ? p.EstimatedTotalBytes : 0;
                            item.DownloadedSize = DownloadItem.FormatBytes(p.Bytes);
                            item.TotalSize = p.EstimatedTotalBytes > 0 ? DownloadItem.FormatBytes(p.EstimatedTotalBytes) : string.Empty;
                            item.Progress = Math.Min(99.9, Math.Max(0.0, p.Fraction * 100.0));
                            if (p.Phase == "Downloading")
                            {
                                item.SpeedBytesPerSecond = p.SpeedBytesPerSecond;
                                item.Speed = DownloadItem.FormatSpeed(p.SpeedBytesPerSecond);
                            }
                            else
                            {
                                item.Speed = p.Phase;
                            }
                            if (item.Status != CustomStatus.Downloading) item.Status = CustomStatus.Downloading;
                        }
                    }).ConfigureAwait(false);

                    if (!IsCurrentExternal(id, session)) return; // a newer start replaced this one: it owns the files now

                    long length = 0;
                    try { length = new FileInfo(outcome.FinalPath).Length; }
                    catch (Exception ex) { AppLog.Debug("Could not read the size of the finished " + kind.Name + " file", ex); }

                    item.SavePath = outcome.FinalPath;
                    CustomBrowser.Helpers.MarkOfTheWeb.Apply(outcome.FinalPath, item.Url, item.Referer);
                    item.FileName = Path.GetFileName(outcome.FinalPath);
                    if (length > 0)
                    {
                        item.ReceivedBytes = length;
                        item.TotalBytes = length;
                        item.DownloadedSize = DownloadItem.FormatBytes(length);
                        item.TotalSize = DownloadItem.FormatBytes(length);
                    }
                    item.Speed = "-";
                    item.Progress = 100;
                    item.Status = CustomStatus.Completed;
                    DatabaseService.Instance.SaveDownload(item, packageJson: string.Empty);
                    outcome.AfterCompleted?.Invoke();
                }
                catch (OperationCanceledException)
                {
                    if (IsCurrentExternal(id, session) && item.Status != CustomStatus.Paused && item.Status != CustomStatus.Failed)
                    {
                        item.Status = CustomStatus.Paused;
                        item.Speed = "-";
                        DatabaseService.Instance.SaveDownload(item);
                    }
                }
                catch (Exception ex)
                {
                    if (IsCurrentExternal(id, session) && !job.Cancelled)
                    {
                        AppLog.Error(kind.Name + " download failed: " + item.Url, ex);
                        item.ErrorMessage = kind.Explain(ex);
                        item.Speed = "-";
                        item.Status = CustomStatus.Failed;
                        DatabaseService.Instance.SaveDownload(item);
                    }
                }
                finally
                {
                    if (_externalSessions.TryGetValue(id, out ExternalSession? current) && ReferenceEquals(current, session))
                    {
                        _externalSessions.TryRemove(id, out _);
                    }
                    session.Finished.TrySetResult(true);
                    ReleaseJob(job);
                }
            });
        }

        // ------------------------------------------------------------------
        // The two engines
        // ------------------------------------------------------------------
        private static async Task<ExternalOutcome> RunStreamAsync(ExternalRun run)
        {
            string id = run.Item.Id;
            StreamJobSpec? spec = StreamStore.ReadSpec(id);
            if (spec == null) throw new InvalidOperationException("The saved stream details are missing - add the stream again from its page");
            string workDir = StreamStore.WorkDir(spec.OutputPath);
            if (run.CleanFirst) StreamStore.DeleteWork(spec.OutputPath);

            StreamResult result = await StreamEngine.RunAsync(spec, workDir, p => run.Report(new ExternalProgress
            {
                Bytes = p.Bytes,
                EstimatedTotalBytes = p.EstimatedTotalBytes,
                Fraction = p.Fraction,
                SpeedBytesPerSecond = p.SpeedBytesPerSecond,
                Phase = p.Phase
            }), run.Limit, run.Token).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.Note)) AppLog.Info("Stream finished: " + result.Note);
            return new ExternalOutcome
            {
                FinalPath = result.FinalPath,
                AfterCompleted = () =>
                {
                    StreamStore.DeleteSpec(id);
                    StreamStore.DeleteWork(spec.OutputPath);
                }
            };
        }

        private static async Task<ExternalOutcome> RunYtdlAsync(ExternalRun run)
        {
            DownloadItem item = run.Item;
            string id = item.Id;
            YtdlJobSpec? spec = YtdlStore.ReadSpec(id);
            if (spec == null) throw new YtdlException("The saved download details are missing. Download it again from its page.");
            string? exe = YtdlTool.Find();
            if (exe == null) throw new YtdlException("yt-dlp is not installed. Open the page and click the yt-dlp button once to set it up.");

            if (run.CleanFirst)
            {
                YtdlStore.CleanPartials(spec.OutputFolder, spec.BaseName);
                try { if (File.Exists(item.SavePath)) File.Delete(item.SavePath); }
                catch (Exception ex) { AppLog.Debug("Could not delete the old file before restarting", ex); }
            }

            item.Speed = "Preparing";
            YtdlResult result = await YtdlEngine.RunAsync(spec, exe, FfmpegTool.Find(), run.Limit(), p => run.Report(new ExternalProgress
            {
                Bytes = p.Bytes,
                EstimatedTotalBytes = p.EstimatedTotalBytes,
                Fraction = p.Fraction,
                SpeedBytesPerSecond = p.SpeedBytesPerSecond,
                Phase = p.Phase
            }), run.Token).ConfigureAwait(false);

            return new ExternalOutcome
            {
                FinalPath = result.FinalPath,
                AfterCompleted = () => YtdlStore.ForgetCookies(id) // the details stay so that "Restart" still works; the login cookies do not
            };
        }

        private static Exception Innermost(Exception ex)
        {
            Exception inner = ex;
            while (inner is AggregateException agg && agg.InnerException != null) inner = agg.InnerException;
            return inner;
        }

        private static string FriendlyStreamError(Exception ex)
        {
            Exception inner = Innermost(ex);
            if (inner is StreamHttpException http)
            {
                switch (http.StatusCode)
                {
                    case 401: return "Sign-in required (401) - open the page again and add the stream again";
                    case 403: return "Access denied (403) - the link has probably expired, add the stream again from its page";
                    case 404: return "Not found (404) - the stream is gone or the link expired";
                    case 410: return "Link expired (410) - add the stream again from its page";
                    default: return "Server error (" + http.StatusCode + ")";
                }
            }
            string msg = inner.Message ?? string.Empty;
            return msg.Length > 110 ? msg.Substring(0, 110) + "..." : msg;
        }

        private static string FriendlyYtdlError(Exception ex)
        {
            string msg = Innermost(ex).Message ?? string.Empty;
            return msg.Length > 140 ? msg.Substring(0, 140) + "..." : msg;
        }

        // ------------------------------------------------------------------
        // Commands (called from PauseDownload / ResumeDownload / ... when the item is an external one)
        // ------------------------------------------------------------------
        private void ExternalPause(string id)
        {
            RemoveFromQueue(id);
            CancelActiveJobs(id);
            DownloadItem? item = FindItem(id);
            if (item != null)
            {
                item.Status = CustomStatus.Paused;
                item.Speed = "-";
                DatabaseService.Instance.SaveDownload(item);
            }
            if (_externalSessions.TryGetValue(id, out ExternalSession? session))
            {
                try { session.Cts.Cancel(); } catch (Exception ex) { AppLog.Debug("Pausing the external session failed", ex); }
            }
        }

        private void ExternalResume(ExternalKind kind, string id)
        {
            DownloadItem? item = FindItem(id);
            if (item == null) return;
            if (item.Status == CustomStatus.Downloading || item.Status == CustomStatus.Queued) return;
            Enqueue(item, job => LaunchExternal(kind, item, job, false));
        }

        private void ExternalRestart(ExternalKind kind, string id)
        {
            DownloadItem? item = FindItem(id);
            if (item == null) return;
            RemoveFromQueue(id);
            CancelActiveJobs(id);
            if (_externalSessions.TryRemove(id, out ExternalSession? session))
            {
                _externalClosing[id] = session.Finished.Task;
                try { session.Cts.Cancel(); } catch (Exception ex) { AppLog.Debug("Stopping the external session failed", ex); }
            }
            item.Progress = 0;
            item.ReceivedBytes = 0;
            item.DownloadedSize = "0 MB";
            item.ErrorMessage = string.Empty;
            DatabaseService.Instance.SaveDownload(item, packageJson: string.Empty);
            Enqueue(item, job => LaunchExternal(kind, item, job, true));
        }

        private void ExternalDelete(ExternalKind kind, string id, bool deleteFileFromDisk)
        {
            DownloadItem? item = FindItem(id);
            RemoveFromQueue(id);
            CancelActiveJobs(id);
            Task? finished = null;
            if (_externalSessions.TryRemove(id, out ExternalSession? session))
            {
                finished = session.Finished.Task;
                try { session.Cts.Cancel(); } catch (Exception ex) { AppLog.Debug("Stopping the external session failed", ex); }
            }
            _externalClosing.TryRemove(id, out _);
            DatabaseService.Instance.RemoveDownload(id);
            Action? cleanup = item == null ? null : kind.PrepareCleanup(item);
            kind.DeleteSpec(id);
            if (item == null) return;

            string output = item.SavePath;
            DownloadStore.Items.Remove(item);
            _ = Task.Run(async () =>
            {
                if (finished != null) await Task.WhenAny(finished, Task.Delay(5000)).ConfigureAwait(false);
                cleanup?.Invoke();
                if (!deleteFileFromDisk) return;
                try { if (File.Exists(output)) File.Delete(output); }
                catch (Exception ex) { AppLog.Warn("Could not delete the downloaded file: " + output, ex); }
            });
        }
    }
}



