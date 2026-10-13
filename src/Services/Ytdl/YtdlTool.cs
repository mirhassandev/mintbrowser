using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CustomBrowser.Helpers;

namespace CustomBrowser.Services.Ytdl
{
    /// <summary>Starts yt-dlp.exe as a hidden child process (never through a shell: every argument is passed separately).</summary>
    internal static class YtdlProcess
    {
        public static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args, string? workDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) psi.WorkingDirectory = workDir;
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            return psi;
        }

        public static void KillQuietly(Process proc)
        {
            try { if (!proc.HasExited) proc.Kill(true); }
            catch (Exception) { /* already gone */ }
        }

        /// <summary>Runs yt-dlp to the end and returns its exit code, standard output and error output.</summary>
        public static async Task<(int ExitCode, string Output, string Error)> RunCaptureAsync(
            string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
        {
            using var proc = new Process { StartInfo = CreateStartInfo(exe, args, null) };
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

            try { proc.StandardInput.Close(); } catch (Exception) { /* not needed */ }
            Task<string> outTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(proc);
                if (ct.IsCancellationRequested) throw;
                throw new YtdlException("yt-dlp took too long to answer (" + (int)timeout.TotalSeconds + " seconds).");
            }

            string output = await outTask.ConfigureAwait(false);
            string error = await errTask.ConfigureAwait(false);
            return (proc.ExitCode, output, error);
        }
    }

    /// <summary>Finds, installs and updates yt-dlp.exe. The browser keeps its own copy in %LocalAppData%\CustomBrowser\tools.</summary>
    public static class YtdlTool
    {
        private const string ExeUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        private const string SumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";

        private static readonly string[] VersionArgs = { "--version" };
        private static readonly char[] SpaceOrTab = { ' ', '\t' };

        public static string ToolsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CustomBrowser", "tools");

        public static string ManagedPath => Path.Combine(ToolsDir, "yt-dlp.exe");

        /// <summary>The browser's own copy first, then next to the app, then anything on PATH (for example from winget).</summary>
        public static string? Find()
        {
            try
            {
                if (File.Exists(ManagedPath)) return ManagedPath;

                string baseDir = AppContext.BaseDirectory;
                string[] local = { Path.Combine(baseDir, "Tools", "yt-dlp.exe"), Path.Combine(baseDir, "yt-dlp.exe") };
                foreach (string c in local)
                {
                    if (File.Exists(c)) return c;
                }

                string? onPath = FindOnPath("yt-dlp.exe");
                if (onPath != null) return onPath;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Searching for yt-dlp failed", ex);
            }
            return null;
        }

        /// <summary>Looks for a program in every folder of PATH (the browser sees the PATH it was started with).</summary>
        public static string? FindOnPath(string fileName) => ToolLocator.FindOnPath(fileName);

        public static bool IsYoutubeUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "youtu.be" || host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal)
                || host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);
        }

        /// <summary>Current yt-dlp needs a JavaScript runtime (Deno, Node or Bun) to read YouTube properly.</summary>
        public static bool HasJsRuntime()
        {
            return FindOnPath("deno.exe") != null || FindOnPath("node.exe") != null || FindOnPath("bun.exe") != null;
        }

        /// <summary>yt-dlp uses Deno by default. If only Node or Bun is installed it has to be told to use it. Only YouTube pages need this.</summary>
        public static List<string> JsRuntimeArgsFor(string pageUrl)
        {
            var args = new List<string>();
            if (!IsYoutubeUrl(pageUrl)) return args;
            if (FindOnPath("deno.exe") != null) return args;

            string? node = FindOnPath("node.exe");
            if (node != null)
            {
                args.Add("--js-runtimes");
                args.Add("node:" + node);
                return args;
            }
            string? bun = FindOnPath("bun.exe");
            if (bun != null)
            {
                args.Add("--js-runtimes");
                args.Add("bun:" + bun);
            }
            return args;
        }

        public static bool IsManaged(string exe)
        {
            try
            {
                return string.Equals(Path.GetFullPath(exe), Path.GetFullPath(ManagedPath), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>For example "2026.09.12", or an empty string if it could not be read.</summary>
        public static async Task<string> GetVersionAsync(string exe, CancellationToken ct)
        {
            try
            {
                var result = await YtdlProcess.RunCaptureAsync(exe, VersionArgs, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                if (result.ExitCode != 0) return string.Empty;
                string first = result.Output.Split('\n')[0].Trim();
                return first.Length > 40 ? string.Empty : first;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Reading the yt-dlp version failed", ex);
                return string.Empty;
            }
        }

        /// <summary>Downloads the official yt-dlp.exe from GitHub and installs it only if its SHA-256 matches the official checksum list.</summary>
        public static async Task<string> DownloadAsync(IProgress<string>? status, CancellationToken ct)
        {
            Directory.CreateDirectory(ToolsDir);
            string temp = ManagedPath + ".download";

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MintBrowser");

            status?.Report("Reading the official checksum...");
            string sums = await http.GetStringAsync(SumsUrl, ct).ConfigureAwait(false);
            string? expected = null;
            foreach (string raw in sums.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(SpaceOrTab, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                string name = parts[parts.Length - 1].TrimStart('*');
                if (string.Equals(name, "yt-dlp.exe", StringComparison.OrdinalIgnoreCase))
                {
                    expected = parts[0];
                    break;
                }
            }
            if (expected == null) throw new YtdlException("The official checksum for yt-dlp.exe was not found - nothing was installed.");

            status?.Report("Downloading yt-dlp (about 18 MB)...");
            using (HttpResponseMessage response = await http.GetAsync(ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(target, 81920, ct).ConfigureAwait(false);
            }

            status?.Report("Checking the download...");
            string actual;
            using (FileStream check = File.OpenRead(temp))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
            }
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(temp); } catch (Exception) { /* leftover temp file is harmless */ }
                throw new YtdlException("The downloaded file did not match the official checksum, so it was deleted.");
            }

            try
            {
                File.Move(temp, ManagedPath, overwrite: true);
            }
            catch (IOException)
            {
                throw new YtdlException("yt-dlp is in use by a running download. Try again when it has finished.");
            }
            return ManagedPath;
        }

        /// <summary>Replaces the browser's own copy with the newest release. A copy that lives elsewhere (winget, PATH) is left alone.</summary>
        public static async Task<string> UpdateAsync(string exe, IProgress<string>? status, CancellationToken ct)
        {
            if (!IsManaged(exe))
            {
                return "This copy of yt-dlp is managed outside the browser (" + exe + "). Update it with:  winget upgrade yt-dlp.yt-dlp";
            }
            await DownloadAsync(status, ct).ConfigureAwait(false);
            string version = await GetVersionAsync(ManagedPath, ct).ConfigureAwait(false);
            return version.Length == 0 ? "yt-dlp was updated." : "yt-dlp is up to date (" + version + ").";
        }
    }
}

