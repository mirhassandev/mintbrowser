using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Downloader;
using CustomBrowser.Helpers;
using CustomBrowser.Models;
using CustomStatus = CustomBrowser.Models.DownloadStatus;

namespace CustomBrowser.Services
{
    public partial class DirectDownloadService
    {
        /// <summary>One request to run a download. While it sits in _active it owns one "simultaneous download" slot.</summary>
        private sealed class QueueJob
        {
            public required DownloadItem Item { get; init; }
            public required Action<QueueJob> Start { get; init; }
            public volatile bool Cancelled;
        }

        private sealed class ActiveDownloadSession
        {
            public required DownloadService Service { get; set; }
            public required DownloadConfiguration Config { get; set; }
            public required DownloadItem Item { get; set; }
            public required string CookieHeader { get; set; }
            public CancellationTokenSource? Cts { get; set; }
            public DownloadPackage? Package { get; set; }
            public QueueJob? Job { get; set; } // the slot this session currently owns
            /// <summary>Completed when the transfer has ended and the downloader is disposed (its file handles are released).</summary>
            public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly ConcurrentDictionary<string, ActiveDownloadSession> _sessions = new();
        private readonly ConcurrentDictionary<string, DownloadPackage> _packages = new();
        /// <summary>Sessions that were cancelled / restarted: the next launch waits for them to release the file.</summary>
        private readonly ConcurrentDictionary<string, Task> _closing = new();

        private readonly object _queueLock = new();
        private readonly List<QueueJob> _waiting = new();
        private readonly HashSet<QueueJob> _active = new();

        /// <summary>
        /// Lets the browser find out how the first moments of a download went (see WatchStart).
        /// Completes with 0 once bytes are flowing, or with the HTTP status (-1 = other failure) if the transfer fails before that.
        /// </summary>
        private readonly ConcurrentDictionary<string, TaskCompletionSource<int>> _startWatchers = new();

        /// <summary>Call BEFORE Enqueue. The returned task completes as described on <c>_startWatchers</c>.</summary>
        public Task<int> WatchStart(string id)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _startWatchers[id] = tcs;
            return tcs.Task;
        }

        private void SignalStart(string id, int result)
        {
            if (_startWatchers.TryRemove(id, out TaskCompletionSource<int>? tcs)) tcs.TrySetResult(result);
        }

        public DirectDownloadService()
        {
            // Settings changed: start waiting items if the limit went up, re-split the speed cap.
            DownloadSettings.Changed += () => { Pump(); ApplySpeedLimit(); };
        }

        // ------------------------------------------------------------------
        // Queue
        // ------------------------------------------------------------------

        /// <summary>The ONLY way to start a new download. Starts immediately if a slot is free, otherwise waits.</summary>
        public void Enqueue(DownloadItem item, string cookieHeader = "", DownloadPackage? packageToResume = null)
        {
            if (!string.IsNullOrEmpty(cookieHeader)) item.CookieHeader = cookieHeader;
            Enqueue(item, job => LaunchFresh(item, job, packageToResume, cleanPartialFiles: false));
        }

        private void Enqueue(DownloadItem item, Action<QueueJob> start)
        {
            // Latest request wins: a job of this item that is already running is cancelled first, so one item never runs twice.
            CancelActiveJobs(item.Id);
            item.Speed = "-";
            item.ErrorMessage = string.Empty;
            item.Status = CustomStatus.Queued; // BEFORE the job becomes startable (Pump may run on another thread)
            lock (_queueLock)
            {
                _waiting.RemoveAll(j => j.Item.Id == item.Id);
                _waiting.Add(new QueueJob { Item = item, Start = start });
            }
            Pump();
        }

        /// <summary>Marks every running job of this item as cancelled and frees its slot (the caller stops the transfer itself).</summary>
        private void CancelActiveJobs(string id)
        {
            bool released = false;
            lock (_queueLock)
            {
                foreach (QueueJob j in _active.Where(x => x.Item.Id == id).ToList())
                {
                    j.Cancelled = true;
                    _active.Remove(j);
                    released = true;
                }
            }
            if (released) Pump();
        }

        private void Pump()
        {
            var toStart = new List<QueueJob>();
            lock (_queueLock)
            {
                int max = Math.Max(1, DownloadSettings.MaxConcurrent);
                while (_waiting.Count > 0 && _active.Count < max)
                {
                    QueueJob job = _waiting[0];
                    _waiting.RemoveAt(0);
                    _active.Add(job);
                    toStart.Add(job);
                }
                for (int i = 0; i < _waiting.Count; i++) _waiting[i].Item.QueuePosition = i + 1;
                foreach (QueueJob j in toStart) j.Item.QueuePosition = 0;
            }

            ApplySpeedLimit();

            foreach (QueueJob job in toStart)
            {
                try { job.Start(job); }
                catch (Exception ex)
                {
                    AppLog.Error($"Could not start queued download: {job.Item.Url}", ex);
                    job.Item.ErrorMessage = "Could not start";
                    job.Item.Status = CustomStatus.Failed;
                    job.Item.Speed = "-";
                    SignalStart(job.Item.Id, -1);
                    ReleaseJob(job);
                }
            }
        }

        /// <summary>Frees a slot and starts the next waiting item. Safe to call more than once for the same job.</summary>
        private void ReleaseJob(QueueJob? job)
        {
            if (job == null) return;
            bool removed;
            lock (_queueLock) removed = _active.Remove(job);
            if (removed) Pump();
        }

        private void RemoveFromQueue(string id)
        {
            List<QueueJob> removed;
            lock (_queueLock)
            {
                removed = _waiting.Where(j => j.Item.Id == id).ToList();
                _waiting.RemoveAll(j => j.Item.Id == id);
            }
            foreach (QueueJob j in removed) j.Item.QueuePosition = 0;
            if (removed.Count > 0) Pump(); // renumber the rest
        }

        // ------------------------------------------------------------------
        // Speed limit: one global cap, split evenly across the downloads that are running
        // ------------------------------------------------------------------
        private long ComputePerDownloadLimit()
        {
            long total = DownloadSettings.SpeedLimitBytesPerSecond;
            if (total <= 0) return 0; // 0 = unlimited
            int active;
            lock (_queueLock) active = Math.Max(1, _active.Count);
            return Math.Max(4096, total / active);
        }

        private void ApplySpeedLimit()
        {
            long per = ComputePerDownloadLimit();
            foreach (ActiveDownloadSession s in _sessions.Values)
            {
                try { s.Config.MaximumBytesPerSecond = per; } catch (Exception speedEx) { AppLog.Debug("Could not apply the speed limit to an active download", speedEx); }
            }
        }

        // ------------------------------------------------------------------
        // The actual transfer (only ever called from a queue slot)
        // ------------------------------------------------------------------
        /// <summary>
        /// The login cookie must only travel to the site it belongs to. The download library follows redirects on its own and would
        /// send the cookie to any host (CDN, tracker...). So the redirect chain is walked here first, with the cookie, until it
        /// leaves the site. If it does, the download starts at that first foreign address WITHOUT the cookie.
        /// Any failure here falls back to the old behaviour (original address, cookie attached).
        /// </summary>
        private static async Task<(string Url, bool LeftSite)> FindCrossSiteHopAsync(string url, string cookie, string? referer, string userAgent, CancellationToken ct)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? start)) return (url, false);
                using var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
                using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                Uri current = start;
                for (int hop = 0; hop < 10; hop++)
                {
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, current);
                    req.Headers.TryAddWithoutValidation("Cookie", cookie);
                    req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                    if (!string.IsNullOrWhiteSpace(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    using var resp = await client.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
                    int code = (int)resp.StatusCode;
                    if (code < 300 || code >= 400 || resp.Headers.Location == null) return (url, false);
                    Uri next = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location : new Uri(current, resp.Headers.Location);
                    if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) return (url, false);
                    bool downgrade = current.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp;
                    if (downgrade || !SiteDomain.IsSameSite(next.Host, start.Host)) return (next.AbsoluteUri, true);
                    current = next;
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Redirect check for the download failed; using the original address", ex);
            }
            return (url, false);
        }

        private async Task RunDownloadAsync(DownloadItem item, string cookieHeader, DownloadPackage? packageToResume, QueueJob job)
        {
            if (job.Cancelled) return; // paused / cancelled / deleted between getting the slot and starting
            var cts = new CancellationTokenSource();
            var config = new DownloadConfiguration
            {
                MaximumBytesPerSecond = ComputePerDownloadLimit(),
                ChunkCount = 8,
                ParallelDownload = true,
                BufferBlockSize = 1024 * 16,
                ClearPackageOnCompletionWithFailure = false,
                EnableAutoResumeDownload = true,   // resume data lives inside the partial .download file (survives restarts)
                MaxTryAgainOnFailure = 3,          // retry transient network errors before failing
                RequestConfiguration =
                {
                    Headers = new System.Net.WebHeaderCollection()
                }
            };

            if (!string.IsNullOrWhiteSpace(item.Referer)) config.RequestConfiguration.Referer = item.Referer;
            config.RequestConfiguration.UserAgent = string.IsNullOrWhiteSpace(item.UserAgent)
                ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0"
                : item.UserAgent;

            if (!string.IsNullOrEmpty(cookieHeader)) item.CookieHeader = cookieHeader;
            string effectiveCookie = !string.IsNullOrEmpty(cookieHeader) ? cookieHeader : item.CookieHeader;
            string downloadUrl = item.Url;
            if (!string.IsNullOrEmpty(effectiveCookie))
            {
                (string hopUrl, bool leftSite) = await FindCrossSiteHopAsync(item.Url, effectiveCookie, item.Referer, config.RequestConfiguration.UserAgent ?? string.Empty, cts.Token);
                if (leftSite)
                {
                    downloadUrl = hopUrl;
                    AppLog.Info("Download redirects to another site; the login cookie is not sent there.");
                }
                else
                {
                    config.RequestConfiguration.Headers.Add("Cookie", effectiveCookie);
                }
            }

            if (job.Cancelled || cts.IsCancellationRequested)
            {
                // Paused / cancelled / deleted while the redirect probe was running: do not start the transfer.
                cts.Dispose();
                ReleaseJob(job);
                return;
            }

            var downloadService = new DownloadService(config);
            var session = new ActiveDownloadSession
            {
                Service = downloadService,
                Config = config,
                Item = item,
                CookieHeader = item.CookieHeader,
                Cts = cts,
                Package = packageToResume,
                Job = job
            };

            _sessions[item.Id] = session;

            downloadService.DownloadProgressChanged += (s, e) =>
            {
                if (!IsCurrent(item.Id, session)) return; // ignore events from a cancelled / replaced session
                if (item.Status == CustomStatus.Paused) return;

                item.Progress = Math.Min(100.0, Math.Max(0.0, e.ProgressPercentage));
                item.ReceivedBytes = e.ReceivedBytesSize;
                item.TotalBytes = e.TotalBytesToReceive;
                item.SpeedBytesPerSecond = e.AverageBytesPerSecondSpeed; // before Speed: setting the text refreshes the status line
                item.Speed = DownloadItem.FormatSpeed(e.AverageBytesPerSecondSpeed);
                item.DownloadedSize = DownloadItem.FormatBytes(e.ReceivedBytesSize);
                item.TotalSize = DownloadItem.FormatBytes(e.TotalBytesToReceive);
                item.Status = CustomStatus.Downloading;
                if (e.ReceivedBytesSize > 0) SignalStart(item.Id, 0); // the server accepted us: the browser's own copy can be dropped

                if (downloadService.Package != null)
                {
                    _packages[item.Id] = downloadService.Package;
                }
            };

            downloadService.DownloadFileCompleted += (s, e) =>
            {
                if (!IsCurrent(item.Id, session)) return; // stale handler from a cancelled / replaced session
                if (downloadService.Package != null)
                {
                    _packages[item.Id] = downloadService.Package;
                    try
                    {
                        string json = JsonSerializer.Serialize(downloadService.Package);
                        DatabaseService.Instance.SaveDownload(item, json);
                    }
                    catch (Exception resumeSaveEx) { AppLog.Warn($"Could not save resume data for download: {item.Url}", resumeSaveEx); }
                }

                if (e.Cancelled)
                {
                    if (item.Status != CustomStatus.Paused)
                    {
                        item.Status = CustomStatus.Paused;
                    }
                    item.Speed = "-";
                    DatabaseService.Instance.SaveDownload(item);
                }
                else if (e.Error != null)
                {
                    AppLog.Error($"Direct download failed: {item.Url}", e.Error);
                    item.ErrorMessage = FriendlyError(e.Error);
                    item.Status = CustomStatus.Failed;
                    item.Speed = "-";
                    DatabaseService.Instance.SaveDownload(item);
                    SignalStart(item.Id, StartFailureCode(e.Error));
                }
                else
                {
                    DownloadStore.CompleteFromDisk(item);
                    _sessions.TryRemove(item.Id, out _);
                    _packages.TryRemove(item.Id, out _);
                    SignalStart(item.Id, 0);
                }
            };

            try
            {
                if (packageToResume != null)
                {
                    packageToResume.Urls = new[] { downloadUrl };
                    packageToResume.FileName = item.SavePath;
                    await downloadService.DownloadFileTaskAsync(packageToResume, downloadUrl, cts.Token);
                }
                else
                {
                    await downloadService.DownloadFileTaskAsync(downloadUrl, item.SavePath, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // A superseded session (restart / new link / cancel) must not touch the item: a newer session owns it.
                if (IsCurrent(item.Id, session))
                {
                    if (item.Status != CustomStatus.Paused)
                    {
                        item.Status = CustomStatus.Paused;
                    }
                    item.Speed = "-";
                }
            }
            catch (Exception ex)
            {
                if (IsCurrent(item.Id, session))
                {
                    AppLog.Error($"Download task encountered exception: {item.Url}", ex);
                    item.ErrorMessage = FriendlyError(ex);
                    item.Status = CustomStatus.Failed;
                    item.Speed = "-";
                    SignalStart(item.Id, StartFailureCode(ex));
                }
                else
                {
                    AppLog.Debug($"Ignoring error from a replaced download session: {item.Url}", ex);
                }
            }
            finally
            {
                try { downloadService.Dispose(); } catch (Exception disposeEx) { AppLog.Debug("Disposing the download service failed", disposeEx); }
                if (_sessions.TryGetValue(item.Id, out var currentSession) && currentSession == session)
                {
                    _sessions.TryRemove(item.Id, out _);
                }
                SignalStart(item.Id, 0); // ended without a verdict (e.g. paused): do not keep a waiter hanging
                session.Finished.TrySetResult(true);
                ReleaseJob(session.Job); // finished / failed / cancelled: free the slot so the next queued item starts
            }
        }

        /// <summary>Starts a brand-new session for an item (restart, refreshed link, resume after app restart, new download).</summary>
        private void LaunchFresh(DownloadItem item, QueueJob job, DownloadPackage? explicitPackage, bool cleanPartialFiles)
        {
            string id = item.Id;
            DownloadPackage? package = explicitPackage;
            Task? oldFinished = null;

            if (_sessions.TryRemove(id, out var old))
            {
                if (old.Service.Package != null) _packages[id] = old.Service.Package;
                try
                {
                    old.Cts?.Cancel();
                    old.Service.CancelAsync();
                }
                catch (Exception replaceCancelEx) { AppLog.Debug("Cancelling the replaced download session failed", replaceCancelEx); }
                if (!ReferenceEquals(old.Job, job)) ReleaseJob(old.Job);
                oldFinished = old.Finished.Task;
            }

            // Restart / Cancel remove the session before this runs, so it is found here instead.
            // Without waiting, the partial file was still locked and silently survived the cleanup.
            if (_closing.TryRemove(id, out Task? closingTask) && oldFinished == null)
            {
                oldFinished = closingTask;
            }

            if (cleanPartialFiles)
            {
                package = null;
            }
            else if (package == null)
            {
                if (_packages.TryGetValue(id, out var cached))
                {
                    package = cached;
                }
                else
                {
                    try
                    {
                        string? savedJson = DatabaseService.Instance.GetDownloadPackageJson(id);
                        if (!string.IsNullOrWhiteSpace(savedJson))
                        {
                            package = JsonSerializer.Deserialize<DownloadPackage>(savedJson);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Debug($"Could not restore saved package json for {id}", ex);
                    }
                }
            }

            item.ErrorMessage = string.Empty;

            _ = Task.Run(async () =>
            {
                try
                {
                    // The cancelled session must have released its file before the same file is opened again.
                    if (oldFinished != null) await Task.WhenAny(oldFinished, Task.Delay(5000));
                    if (job.Cancelled) return;
                    if (cleanPartialFiles) await CleanPartialFilesAsync(item);
                    if (job.Cancelled) return;
                    await RunDownloadAsync(item, item.CookieHeader, package, job);
                }
                catch (Exception ex)
                {
                    if (job.Cancelled)
                    {
                        // Paused / cancelled / deleted meanwhile: leave the item alone (a failed start must not bring a deleted row back).
                        AppLog.Debug($"Ignoring start error of a cancelled download: {item.Url}", ex);
                        ReleaseJob(job);
                        return;
                    }
                    AppLog.Error($"Download failed to start: {item.Url}", ex);
                    item.Status = CustomStatus.Failed;
                    item.Speed = "-";
                    DatabaseService.Instance.SaveDownload(item);
                    SignalStart(item.Id, StartFailureCode(ex));
                    ReleaseJob(job);
                }
            });
        }

        private bool IsCurrent(string id, ActiveDownloadSession session) =>
            _sessions.TryGetValue(id, out var current) && ReferenceEquals(current, session);

        /// <summary>HTTP status behind a failure, or -1 when it was not an HTTP refusal. Never 0 (0 means "bytes are flowing").</summary>
        private static int StartFailureCode(Exception? ex)
        {
            int status = ExtractHttpStatus(ex);
            return status > 0 ? status : -1;
        }

        private static string FriendlyError(Exception ex)
        {
            switch (ExtractHttpStatus(ex))
            {
                case 401: return "Sign-in required (401) – log into the site in a tab, then retry";
                case 403: return "Access denied (403) – link may have expired; try Restart or Refresh Address ✏";
                case 404: return "File not found (404)";
                case 410: return "Link expired (410)";
                case 416: return "Server rejected the resume (416) - use Restart";
            }
            string msg = ex.Message ?? string.Empty;
            return msg.Length > 90 ? msg.Substring(0, 90) + "..." : msg;
        }

        /// <summary>Real HTTP status from the exception chain. Falls back to the message text with URLs removed first,
        /// so a "403" inside a link is never mistaken for a status code. 0 = unknown.</summary>
        private static int ExtractHttpStatus(Exception? ex)
        {
            for (Exception? cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is System.Net.Http.HttpRequestException http && http.StatusCode.HasValue) return (int)http.StatusCode.Value;
                if (cur is System.Net.WebException web && web.Response is System.Net.HttpWebResponse resp) return (int)resp.StatusCode;
            }
            string text = System.Text.RegularExpressions.Regex.Replace(ex?.Message ?? string.Empty, @"https?://\S+", " ");
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(text, @"\b(401|403|404|410|416)\b");
            return m.Success ? int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        // ------------------------------------------------------------------
        // Commands
        // ------------------------------------------------------------------
        public void PauseDownload(string id)
        {
            // The downloads page polls (about every 600 ms), so a click can arrive just after the item finished or failed.
            // Pausing it then would overwrite its final state, so the click is ignored.
            DownloadItem? target = FindItem(id);
            if (target != null && (target.Status == CustomStatus.Completed || target.Status == CustomStatus.Failed)) return;
            if (KindOf(id) != null) { ExternalPause(id); return; }
            RemoveFromQueue(id); // a waiting item simply leaves the queue
            CancelActiveJobs(id); // a job that already has a slot but no session yet must not start

            if (_sessions.TryGetValue(id, out var session))
            {
                session.Item.Status = CustomStatus.Paused;
                session.Item.Speed = "-";
                string? json = null;
                if (session.Service.Package != null)
                {
                    _packages[id] = session.Service.Package;
                    try
                    {
                        json = System.Text.Json.JsonSerializer.Serialize(session.Service.Package);
                    }
                    catch (Exception resumeSerializeEx) { AppLog.Warn($"Could not serialize resume data for download: {id}", resumeSerializeEx); }
                }
                DatabaseService.Instance.SaveDownload(session.Item, json);
                try
                {
                    session.Service.Pause();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Could not cleanly pause download {id}", ex);
                }
                ReleaseJob(session.Job); // a paused download must not occupy a slot
            }
            else
            {
                var item = FindItem(id);
                if (item != null)
                {
                    item.Status = CustomStatus.Paused;
                    item.Speed = "-";
                    DatabaseService.Instance.SaveDownload(item);
                }
            }
        }

        public void ResumeDownload(string id, string? newUrl = null)
        {
            // A late click on Resume for an item that has meanwhile completed must not start it again
            // (a finished stream no longer has its saved details and would turn into a false failure).
            if (string.IsNullOrWhiteSpace(newUrl) && FindItem(id)?.Status == CustomStatus.Completed) return;
            if (KindOf(id) is ExternalKind resumeKind) { if (string.IsNullOrWhiteSpace(newUrl)) ExternalResume(resumeKind, id); return; }
            var item = FindItem(id);
            if (item == null) return;

            // Already downloading or waiting for a slot: a second click must not start a second copy.
            if (string.IsNullOrWhiteSpace(newUrl) &&
                (item.Status == CustomStatus.Downloading || item.Status == CustomStatus.Queued)) return;

            if (!string.IsNullOrWhiteSpace(newUrl))
            {
                item.Url = newUrl.Trim();
                DatabaseService.Instance.SaveDownload(item);
            }

            // Paused in-memory session and no new URL: resume it as soon as a slot is free.
            if (string.IsNullOrWhiteSpace(newUrl) &&
                _sessions.TryGetValue(id, out var pausedSession) &&
                pausedSession.Service.IsPaused)
            {
                Enqueue(item, job =>
                {
                    if (_sessions.TryGetValue(id, out var live) && ReferenceEquals(live, pausedSession) && live.Service.IsPaused)
                    {
                        live.Job = job;
                        try
                        {
                            item.Status = CustomStatus.Downloading;
                            live.Service.Resume();
                            return;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn($"In-memory resume failed for {id}, launching fresh resume task", ex);
                        }
                    }
                    LaunchFresh(item, job, null, cleanPartialFiles: false);
                });
                return;
            }

            // Otherwise (after an app restart, new link, failed download): fresh session from the saved package.
            Enqueue(item, job => LaunchFresh(item, job, null, cleanPartialFiles: false));
        }

        public void RestartDownload(string id)
        {
            if (KindOf(id) is ExternalKind restartKind) { ExternalRestart(restartKind, id); return; }
            var item = FindItem(id);
            if (item == null) return;

            RemoveFromQueue(id);
            if (_sessions.TryRemove(id, out var session))
            {
                _closing[id] = session.Finished.Task;
                try
                {
                    session.Cts?.Cancel();
                    session.Service.CancelAsync();
                }
                catch (Exception stopEx) { AppLog.Debug($"Stopping the download session failed: {id}", stopEx); }
                ReleaseJob(session.Job);
            }
            _packages.TryRemove(id, out _);

            item.Progress = 0;
            item.ReceivedBytes = 0;
            item.DownloadedSize = "0 MB";
            item.Speed = "0 KB/s";
            item.ErrorMessage = string.Empty;
            DatabaseService.Instance.SaveDownload(item, packageJson: string.Empty); // "" clears the old resume data

            // Partial files are deleted when the item actually gets its slot, right before the new transfer starts.
            Enqueue(item, job => LaunchFresh(item, job, null, cleanPartialFiles: true));
        }

        public void RefreshDownloadLink(string id, string newUrl)
        {
            if (!Uri.TryCreate(newUrl?.Trim(), UriKind.Absolute, out Uri? refreshed) ||
                (refreshed.Scheme != Uri.UriSchemeHttp && refreshed.Scheme != Uri.UriSchemeHttps)) return;
            ResumeDownload(id, newUrl.Trim());
        }

        public void CancelDownload(string id)
        {
            if (KindOf(id) != null) { ExternalPause(id); return; }
            RemoveFromQueue(id);
            CancelActiveJobs(id);

            if (_sessions.TryRemove(id, out var session))
            {
                _closing[id] = session.Finished.Task;
                try
                {
                    session.Cts?.Cancel();
                    session.Service.CancelAsync();
                }
                catch (Exception stopEx2) { AppLog.Debug($"Stopping the download session failed: {id}", stopEx2); }
                ReleaseJob(session.Job);
            }

            var item = FindItem(id);
            if (item != null)
            {
                item.Status = CustomStatus.Paused;
                item.Speed = "-";
                DatabaseService.Instance.SaveDownload(item);
            }
        }

        public void DeleteDownload(string id, bool deleteFileFromDisk)
        {
            if (KindOf(id) is ExternalKind deleteKind) { ExternalDelete(deleteKind, id, deleteFileFromDisk); return; }
            CancelDownload(id);
            _packages.TryRemove(id, out _);
            _closing.TryRemove(id, out Task? closing); // the cancelled session: its file must be released before the cleanup
            DatabaseService.Instance.RemoveDownload(id);

            var item = FindItem(id);
            if (item == null) return;

            if (deleteFileFromDisk) _ = CleanPartialFilesAfterReleaseAsync(item, closing);

            DownloadStore.Items.Remove(item);
        }

        /// <summary>Waits (up to 5 s) for a cancelled session to release its file, then deletes the leftovers.</summary>
        private static async Task CleanPartialFilesAfterReleaseAsync(DownloadItem item, Task? sessionClosed)
        {
            if (sessionClosed != null) await Task.WhenAny(sessionClosed, Task.Delay(5000));
            await CleanPartialFilesAsync(item);
        }

        /// <summary>Deletes the target file and its ".download"/chunk leftovers, retrying while the file is still locked.</summary>
        private static async Task CleanPartialFilesAsync(DownloadItem item)
        {
            await Task.Delay(250);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (File.Exists(item.SavePath))
                    {
                        File.Delete(item.SavePath);
                    }
                    string? dir = Path.GetDirectoryName(item.SavePath);
                    string fileName = Path.GetFileName(item.SavePath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        foreach (var file in Directory.GetFiles(dir, $"{fileName}.*"))
                        {
                            try { File.Delete(file); } catch (Exception deleteEx) { AppLog.Warn($"Could not delete leftover file: {file}", deleteEx); }
                        }
                    }
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(200);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Could not clean files for {item.SavePath}", ex);
                    break;
                }
            }
        }

        private static DownloadItem? FindItem(string id)
        {
            for (int i = 0; i < DownloadStore.Items.Count; i++)
            {
                if (DownloadStore.Items[i].Id == id) return DownloadStore.Items[i];
            }
            return null;
        }
    }
}



