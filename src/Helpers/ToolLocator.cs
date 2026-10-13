using System;
using System.IO;

namespace CustomBrowser.Helpers
{
    /// <summary>Finds helper programs (ffmpeg, yt-dlp, deno ...) in the folders of PATH.</summary>
    public static class ToolLocator
    {
        public static string? FindOnPath(string fileName)
        {
            try
            {
                string? path = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(path)) return null;
                foreach (string dir in path.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (ArgumentException)
                    {
                        // an invalid folder in PATH is simply skipped
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Searching PATH failed", ex);
            }
            return null;
        }
    }
}

