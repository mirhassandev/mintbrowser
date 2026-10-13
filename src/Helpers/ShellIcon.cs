using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CustomBrowser.Helpers
{
    /// <summary>
    /// Extracts the real Windows Shell icon for a file via SHGetFileInfo —
    /// the same API used by Windows Explorer and IDM.
    /// Programs and other types that carry their own icon (.exe, .ico, .lnk ...)
    /// are asked for their own icon from the finished file on disk, so WinRAR shows
    /// the WinRAR icon; every other type gets the Windows file-type icon.
    /// Results are cached so the P/Invoke cost is paid once per distinct icon.
    /// The same icon is offered two ways: a base-64 PNG for the web pages (Downloads tab)
    /// and a frozen WPF image for the native dropdown, so both always show the same picture.
    /// </summary>
    internal static class ShellIcon
    {
        private static readonly ConcurrentDictionary<string, string> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, BitmapSource> _imageCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>File types whose icon is stored inside each file (programs), so the type alone cannot tell it.</summary>
        private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".scr", ".ico", ".cur", ".ani", ".lnk", ".cpl"
        };

        private const string FileKeyPrefix   = "f:";
        private const int    MaxCachedEntries = 600;

        /// <summary>
        /// Returns a stable cache key for the icon these arguments would produce.
        /// Use this to de-duplicate icons across download rows.
        /// </summary>
        public static string KeyFor(string fileName, string? existingFilePath = null)
        {
            string ext = NormalizedExtension(fileName);
            string? realPath = RealPathFor(ext, existingFilePath);
            return realPath != null ? FileKeyPrefix + realPath : ext;
        }

        /// <summary>
        /// Returns a base-64-encoded PNG for the shell icon of a file.
        /// Programs (.exe, .ico, .lnk ...) use <paramref name="existingFilePath"/> so WinRAR
        /// shows the WinRAR icon; every other type uses the icon Windows shows for its file type.
        /// Safe to call from any thread — WPF imaging is marshalled to the UI dispatcher.
        /// </summary>
        public static string? GetBase64ForFile(string fileName, string? existingFilePath = null)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            string ext = NormalizedExtension(fileName);
            string? realPath = RealPathFor(ext, existingFilePath);
            string  key      = realPath != null ? FileKeyPrefix + realPath : ext;

            if (_cache.TryGetValue(key, out string? cached))
                return cached;

            string? result = OnUiThread(() => FetchIcon(ext, realPath));

            // If the per-file icon was unreadable fall back to the file-type icon (not cached, so a later call retries)
            if (result == null && realPath != null)
                return GetBase64ForFile(fileName);

            if (result == null) return null; // never cache a failure: the next call tries again
            if (_cache.Count > MaxCachedEntries) TrimFileEntries(_cache);
            _cache.TryAdd(key, result);
            return result;
        }

        /// <summary>
        /// The same icon as a frozen WPF image, for native controls (the downloads dropdown).
        /// Null only when the shell cannot provide an icon at all. Safe to call from any thread.
        /// </summary>
        public static BitmapSource? GetImageSourceForFile(string fileName, string? existingFilePath = null)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            string ext = NormalizedExtension(fileName);
            string? realPath = RealPathFor(ext, existingFilePath);
            string  key      = realPath != null ? FileKeyPrefix + realPath : ext;

            if (_imageCache.TryGetValue(key, out BitmapSource? cached))
                return cached;

            BitmapSource? result = OnUiThread(() => FetchBitmap(ext, realPath));

            // Same fallback as above: a locked / unreadable program shows its file-type icon until a later call succeeds.
            if (result == null && realPath != null)
                return GetImageSourceForFile(fileName);

            if (result == null) return null;
            if (_imageCache.Count > MaxCachedEntries) TrimFileEntries(_imageCache);
            _imageCache.TryAdd(key, result);
            return result;
        }

        // ------------------------------------------------------------------

        private static string NormalizedExtension(string? fileName)
        {
            string ext = Path.GetExtension(fileName ?? string.Empty);
            return string.IsNullOrEmpty(ext) ? ".bin" : ext.ToLowerInvariant();
        }

        private static string? RealPathFor(string ext, string? existingFilePath) =>
            !string.IsNullOrWhiteSpace(existingFilePath) && PerFileExtensions.Contains(ext) && !existingFilePath.StartsWith(@"\\", StringComparison.Ordinal)
                ? existingFilePath : null;

        /// <summary>FetchBitmap uses WPF imaging, which wants the UI thread.</summary>
        private static T OnUiThread<T>(Func<T> work)
        {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            return dispatcher != null && !dispatcher.CheckAccess()
                ? dispatcher.Invoke(work, DispatcherPriority.Background)
                : work();
        }

        /// <summary>Per-file icons are keyed by path; trim them when the cache grows too large.</summary>
        private static void TrimFileEntries<T>(ConcurrentDictionary<string, T> cache)
        {
            foreach (string k in cache.Keys)
                if (k.StartsWith(FileKeyPrefix, StringComparison.Ordinal))
                    cache.TryRemove(k, out _);
        }

        private static string? FetchIcon(string ext, string? realPath = null)
        {
            BitmapSource? bmp = FetchBitmap(ext, realPath);
            if (bmp == null) return null;
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return Convert.ToBase64String(ms.ToArray());
            }
            catch (Exception ex)
            {
                AppLog.Debug($"ShellIcon: PNG encoding failed for '{ext}': {ex.Message}");
                return null;
            }
        }

        private static BitmapSource? FetchBitmap(string ext, string? realPath = null)
        {
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                // A finished program is asked by its real path (own icon);
                // everything else by a made-up path (icon for the file type).
                string dummy = realPath ?? Path.Combine(Path.GetTempPath(), "dummy" + ext);

                var  shfi  = new SHFILEINFO();
                uint attrs = realPath == null ? FILE_ATTRIBUTE_NORMAL : 0;
                uint flags = SHGFI_ICON | SHGFI_LARGEICON | (realPath == null ? SHGFI_USEFILEATTRIBUTES : 0);

                IntPtr r = SHGetFileInfo(dummy, attrs, ref shfi,
                    (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

                if (r == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                {
                    AppLog.Debug($"ShellIcon: SHGetFileInfo returned zero for '{ext}' (realPath={realPath})");
                    return null;
                }

                hIcon = shfi.hIcon;

                BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                AppLog.Debug($"ShellIcon: failed for '{ext}': {ex.Message}");
                return null;
            }
            finally
            {
                if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            }
        }

        // ------------------------------------------------------------------
        // P/Invoke
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int    iIcon;
            public uint   dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }

        private const uint SHGFI_ICON              = 0x000000100;
        private const uint SHGFI_LARGEICON         = 0x000000000; // 32×32
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL   = 0x000000080;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}

