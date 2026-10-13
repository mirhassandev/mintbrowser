using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CustomBrowser.Helpers;

namespace CustomBrowser.Services.Ytdl
{
    /// <summary>The job details are kept in %LocalAppData% so a paused yt-dlp download can continue after a restart.</summary>
    public static class YtdlStore
    {
        private static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CustomBrowser", "ytdl");

        public static string SpecPath(string id) => Path.Combine(Root, id + ".json");

        public static string NewCookiePath()
        {
            Directory.CreateDirectory(Root);
            return Path.Combine(Root, "cookies-" + Guid.NewGuid().ToString("N") + ".txt");
        }

        public static void WriteSpec(string id, YtdlJobSpec spec)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(SpecPath(id), JsonSerializer.Serialize(spec), new System.Text.UTF8Encoding(false));
        }

        public static YtdlJobSpec? ReadSpec(string id)
        {
            try
            {
                string path = SpecPath(id);
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<YtdlJobSpec>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not read the saved yt-dlp details", ex);
                return null;
            }
        }

        /// <summary>Removes the saved details and the login-cookie file of a download that is gone.</summary>
        public static void DeleteSpec(string id)
        {
            try
            {
                YtdlJobSpec? spec = ReadSpec(id);
                if (spec != null) DeleteCookieFile(spec.CookieFile);
                File.Delete(SpecPath(id));
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not delete the saved yt-dlp details", ex);
            }
        }

        /// <summary>A finished download no longer needs the login cookies: the file is deleted, the details stay (for "Restart").</summary>
        public static void ForgetCookies(string id)
        {
            YtdlJobSpec? spec = ReadSpec(id);
            if (spec == null || string.IsNullOrEmpty(spec.CookieFile)) return;
            DeleteCookieFile(spec.CookieFile);
            spec.CookieFile = string.Empty;
            WriteSpec(id, spec);
        }

        /// <summary>Only ever deletes files inside the browser's own yt-dlp folder.</summary>
        public static void DeleteCookieFile(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string full = Path.GetFullPath(path);
                string root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
                if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not delete a login-cookie file", ex);
            }
        }

        private static readonly Regex PartialTail = new Regex(
            @"^(?:f\d+(?:-\w+)?\.)?[A-Za-z0-9]{1,5}(?:\.part(?:-Frag\d+)?|\.ytdl|\.temp)?$", RegexOptions.Compiled);
        private static readonly Regex FormatPrefix = new Regex(@"^f\d+(?:-\w+)?\.", RegexOptions.Compiled);

        /// <summary>True for yt-dlp's leftovers of "baseName": .part, .ytdl, .temp and the per-format pieces (name.f137.mp4).
        /// The finished file (name.mp4) and files of other downloads (name.final.mp4) are never matched.</summary>
        public static bool IsPartialFile(string fileName, string baseName)
        {
            string prefix = baseName + ".";
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string tail = fileName.Substring(prefix.Length);
            if (!PartialTail.IsMatch(tail)) return false;
            string low = tail.ToLowerInvariant();
            bool hasSuffix = low.Contains(".part") || low.EndsWith(".ytdl", StringComparison.Ordinal) || low.EndsWith(".temp", StringComparison.Ordinal);
            return hasSuffix || FormatPrefix.IsMatch(tail);
        }

        public static void CleanPartials(string folder, string baseName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(baseName) || !Directory.Exists(folder)) return;
                foreach (string file in Directory.GetFiles(folder, baseName + ".*"))
                {
                    if (!IsPartialFile(Path.GetFileName(file), baseName)) continue;
                    try { File.Delete(file); }
                    catch (Exception ex) { AppLog.Debug("Could not delete a leftover yt-dlp file: " + file, ex); }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Cleaning the leftover yt-dlp files failed", ex);
            }
        }
    }
}

