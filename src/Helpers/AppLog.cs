using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CustomBrowser.Helpers
{
    /// <summary>
    /// Tiny thread-safe logger. Writes to the debugger output and to
    /// %LocalAppData%\CustomBrowser\logs\app.log (rolled to app.old.log at ~1 MB).
    /// Logging itself must never throw, so a failure inside the file write is swallowed on purpose.
    /// </summary>
    public static class AppLog
    {
        private const long MaxBytes = 1_000_000;
        private static readonly object Gate = new();

        private static readonly System.Text.RegularExpressions.Regex UrlWithQuery = new(@"(https?://[^\s?#""'<>|]+)[?#][^\s""'<>|]*", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Cuts the query string and fragment off every web address in a log line: signed download links carry their secret there.</summary>
        internal static string Redact(string text) => UrlWithQuery.Replace(text, "$1?...");

        public static string LogFilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomBrowser", "logs", "app.log");

        public static void Info(string message) => Write("INFO", message, null, toFile: true);

        public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex, toFile: true);

        public static void Error(string message, Exception ex) => Write("ERROR", message, ex, toFile: true);

        /// <summary>For expected / noisy failures (e.g. a favicon that cannot be fetched): debugger output only.</summary>
        public static void Debug(string message, Exception? ex = null) => Write("DEBUG", message, ex, toFile: false);

        private static void Write(string level, string message, Exception? ex, bool toFile)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Redact(message)}";
            if (ex != null) line += $" | {ex.GetType().Name}: {Redact(ex.Message)}";

            System.Diagnostics.Debug.WriteLine(line);
            if (!toFile) return;

            try
            {
                lock (Gate)
                {
                    string? dir = Path.GetDirectoryName(LogFilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    var info = new FileInfo(LogFilePath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        File.Move(LogFilePath, Path.ChangeExtension(LogFilePath, ".old.log"), overwrite: true);
                    }

                    var sb = new StringBuilder(line).AppendLine();
                    if (ex != null) sb.AppendLine(Redact(ex.ToString()));
                    File.AppendAllText(LogFilePath, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Intentionally empty: a broken log file must never crash or block the browser.
            }
        }
    }
}

