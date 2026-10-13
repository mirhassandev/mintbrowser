using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CustomBrowser.Helpers;
using Microsoft.Web.WebView2.Core;

namespace CustomBrowser.Services
{
    /// <summary>
    /// Site icons kept on this computer (%LocalAppData%\CustomBrowser\favicons), one small PNG per host.
    /// The icon comes from the page itself through WebView2 (SVG icons are converted to PNG by the engine), so no third party
    /// is asked for it. A site without an icon gets a coloured letter tile, drawn locally.
    /// The file name carries a short content hash, so a changed icon is a new file and WPF never shows a stale picture.
    /// </summary>
    public static class FaviconCache
    {
        private const int MaxIconBytes = 512 * 1024;

        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x4A, 0x6F, 0xE3), Color.FromRgb(0x00, 0x9E, 0x7F), Color.FromRgb(0xD9, 0x6C, 0x2B), Color.FromRgb(0x8E, 0x5B, 0xD6),
            Color.FromRgb(0xC9, 0x4F, 0x7C), Color.FromRgb(0x2F, 0x8F, 0xBF), Color.FromRgb(0x6B, 0x8E, 0x23), Color.FromRgb(0x7A, 0x6A, 0x5F)
        };

        private static readonly ConcurrentDictionary<string, string> Known = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        public static string FolderPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CustomBrowser", "favicons");

        private static string? HostOf(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            string host = uri.Host.ToLowerInvariant();
            if (host.Length == 0) return null;
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host.Substring(4);
            return host.Length == 0 ? null : host;
        }

        private static string KeyOf(string host)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host))).Substring(0, 16).ToLowerInvariant();
        }

        private static char LetterOf(string host)
        {
            foreach (char c in host)
            {
                if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c);
            }
            return '?';
        }

        private static Color ColorOf(string host)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(host));
            return Palette[hash[0] % Palette.Length];
        }

        /// <summary>Full path of the cached icon of the site of this address, or null when there is none yet. Reads only the local disk.</summary>
        public static string? TryGetPath(string? url)
        {
            string? host = HostOf(url);
            if (host == null) return null;
            string key = KeyOf(host);
            try
            {
                if (Known.TryGetValue(key, out string? known) && File.Exists(known)) return known;
                if (!Directory.Exists(FolderPath)) return null;
                FileInfo? newest = new DirectoryInfo(FolderPath).EnumerateFiles(key + "_*.png")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (newest == null) return null;
                Known[key] = newest.FullName;
                return newest.FullName;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not look up a cached site icon", ex);
                return null;
            }
        }

        /// <summary>file:// address of the cached icon (usable in the browser window), or null.</summary>
        public static string? TryGetUri(string? url)
        {
            string? path = TryGetPath(url);
            return path == null ? null : new Uri(path).AbsoluteUri;
        }

        /// <summary>data: address of the cached icon (usable inside the built-in new-tab page, which cannot load file:// pictures), or null.</summary>
        public static string? TryGetDataUri(string? url)
        {
            try
            {
                string? path = TryGetPath(url);
                if (path == null) return null;
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0 || bytes.Length > MaxIconBytes) return null;
                return "data:image/png;base64," + Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not read a cached site icon", ex);
                return null;
            }
        }

        /// <summary>
        /// Asks the page for its icon and keeps it. When the page has none, an earlier icon is kept, otherwise a letter tile is made.
        /// Returns the file:// address to show, or null on failure. Must run on the UI thread.
        /// </summary>
        public static async Task<string?> UpdateFromPageAsync(CoreWebView2 core, string? pageUrl)
        {
            string? host = HostOf(pageUrl);
            if (host == null) return null;
            try
            {
                byte[]? icon = null;
                try
                {
                    using Stream? stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                    if (stream != null)
                    {
                        using var buffer = new MemoryStream();
                        await stream.CopyToAsync(buffer);
                        if (buffer.Length > 0 && buffer.Length <= MaxIconBytes)
                        {
                            byte[] candidate = buffer.ToArray();
                            if (IsPicture(candidate)) icon = candidate;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug("The page gave no icon: " + host, ex);
                }

                string path;
                if (icon != null)
                {
                    path = Store(host, icon);
                }
                else
                {
                    string? existing = TryGetPath(pageUrl);
                    path = existing ?? Store(host, LetterTile.ToPng(LetterOf(host), ColorOf(host)));
                }
                return new Uri(path).AbsoluteUri;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not store a site icon: " + host, ex);
                return null;
            }
        }

        private static bool IsPicture(byte[] bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return decoder.Frames.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Store(string host, byte[] bytes)
        {
            string key = KeyOf(host);
            string version = Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 8).ToLowerInvariant();
            Directory.CreateDirectory(FolderPath);
            string path = Path.Combine(FolderPath, key + "_" + version + ".png");
            if (File.Exists(path))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // the newest file of a host is the one that is used
            }
            else
            {
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, true);
            }
            Known[key] = path;
            return path;
        }
    }
}
