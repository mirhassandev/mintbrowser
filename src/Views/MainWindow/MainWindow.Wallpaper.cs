using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using CustomBrowser.Helpers;
using CustomBrowser.Views;

namespace CustomBrowser
{
    /// <summary>The wallpaper: choose an image, go back to the default background, and keep every window in step.</summary>
    public partial class MainWindow
    {
        private static readonly string[] WallpaperExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

        /// <summary>Raised after the wallpaper was changed in one window, so the other windows follow. The argument is the window that changed it.</summary>
        private static event Action<MainWindow>? WallpaperChanged;
        private Action<MainWindow>? _wallpaperChangedHandler;

        private void InitWallpaperSync()
        {
            _wallpaperChangedHandler = source =>
            {
                if (ReferenceEquals(source, this)) return;
                Dispatcher.BeginInvoke(new Action(LoadSavedWallpaper), DispatcherPriority.Background);
            };
            WallpaperChanged += _wallpaperChangedHandler;
        }

        private void ShutdownWallpaperSync()
        {
            if (_wallpaperChangedHandler != null) WallpaperChanged -= _wallpaperChangedHandler;
        }

        /// <summary>The saved wallpaper file (wallpaper.jpg / .png ...), or null when the default background is in use.</summary>
        private static string? FindSavedWallpaper()
        {
            string folder = GetUserDataFolder();
            return WallpaperExtensions.Select(ext => Path.Combine(folder, "wallpaper" + ext)).FirstOrDefault(File.Exists);
        }

        // One click: first time it goes straight to the file picker; once a wallpaper is set, a small menu offers "choose" or "reset".
        private void BtnWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (FindSavedWallpaper() == null) PickWallpaper();
            else WallpaperMenuPopup.IsOpen = true;
        }

        private void BtnWallpaperChoose_Click(object sender, RoutedEventArgs e)
        {
            WallpaperMenuPopup.IsOpen = false;
            PickWallpaper();
        }

        private void BtnWallpaperReset_Click(object sender, RoutedEventArgs e)
        {
            WallpaperMenuPopup.IsOpen = false;
            ResetWallpaper();
        }

        private void PickWallpaper()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose a wallpaper",
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                // Decode FIRST: if the image is invalid the previously saved wallpaper stays untouched.
                var bitmap = LoadBitmap(dialog.FileName);
                SaveWallpaperFile(dialog.FileName);
                ApplyWallpaper(bitmap);
                WallpaperChanged?.Invoke(this);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not set the wallpaper", ex);
                ThemedDialog.Show(this, $"Could not set the wallpaper: {ex.Message}", "Wallpaper", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Copies the image into the profile keeping its real format (a PNG stays a PNG) and removes any older wallpaper file.</summary>
        private static void SaveWallpaperFile(string source)
        {
            string ext = Path.GetExtension(source).ToLowerInvariant();
            if (!WallpaperExtensions.Contains(ext)) ext = ".jpg";
            string folder = GetUserDataFolder();
            string destination = Path.Combine(folder, "wallpaper" + ext);
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, destination, overwrite: true);
            }
            DeleteSavedWallpapers(except: destination);
        }

        private static void DeleteSavedWallpapers(string? except = null)
        {
            string folder = GetUserDataFolder();
            foreach (string ext in WallpaperExtensions)
            {
                string path = Path.Combine(folder, "wallpaper" + ext);
                if (string.Equals(path, except, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                try { File.Delete(path); }
                catch (Exception ex) { AppLog.Debug("Could not delete an old wallpaper file", ex); }
            }
        }

        private void ResetWallpaper()
        {
            try
            {
                DeleteSavedWallpapers();
                ResetWallpaperVisuals();
                WallpaperChanged?.Invoke(this);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not reset the wallpaper", ex);
                ThemedDialog.Show(this, $"Could not reset the wallpaper: {ex.Message}", "Wallpaper", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Back to the plain dark background and the default tint behind the tab bar and toolbar.</summary>
        private void ResetWallpaperVisuals()
        {
            SetResourceReference(BackgroundProperty, "MintSurfaceBrush");
            _lumaGrid = null;
            _tintTop = 0x59;
            _tintBottom = 0x1A;
            var brush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x59, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(0x1A, 0, 0, 0), 1)
                },
                new Point(0, 0), new Point(0, 1));
            brush.Freeze();
            Resources["ChromeTintBrush"] = brush;
        }
    }
}
