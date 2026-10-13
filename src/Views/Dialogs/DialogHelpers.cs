using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CustomBrowser.Views
{
    /// <summary>Small pieces every download dialog needs.</summary>
    internal static class DialogHelpers
    {
        /// <summary>A name that is safe as a file name (without extension). Returns the fallback when nothing is left.</summary>
        public static string CleanFileName(string? raw, string fallback = "")
        {
            string s = (raw ?? string.Empty).Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().TrimEnd('.', ' ');
            if (s.Length > 120) s = s.Substring(0, 120).TrimEnd('.', ' ');
            return s.Length == 0 ? fallback : s;
        }

        public static void BrowseFolder(TextBox box, string title)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
            if (Directory.Exists(box.Text)) dialog.InitialDirectory = box.Text;
            if (dialog.ShowDialog() == true) box.Text = dialog.FolderName;
        }

        public static void DragCard(Window window)
        {
            try { window.DragMove(); }
            catch (InvalidOperationException) { /* mouse button was already released */ }
        }
    }
}

