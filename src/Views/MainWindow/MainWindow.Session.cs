using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using CustomBrowser.Models;
using CustomBrowser.Services;
using CustomBrowser.Views;

namespace CustomBrowser
{
    /// <summary>Closing a window: the "are you sure" question, and remembering the tabs that were still open.</summary>
    public partial class MainWindow
    {
        /// <summary>
        /// Set by App when Windows is signing out or shutting down. Windows must always be able to close then,
        /// so no question is asked.
        /// </summary>
        internal static bool SessionIsEnding { get; set; }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (!SessionIsEnding && !ConfirmWindowClose())
            {
                e.Cancel = true;
                return;
            }
            if (_transferredTab == null) SaveWindowPlacement(); // handle still alive here, so RestoreBounds is accurate
        }

        /// <summary>
        /// Asks before closing a window that holds several tabs, or the last window while downloads are still running.
        /// A window that was emptied on purpose (its tab was dragged out, or its last tab was closed) holds no tabs and closes silently.
        /// </summary>
        private bool ConfirmWindowClose()
        {
            int tabCount = Tabs.Count;
            bool lastWindow = _openBrowserWindows.Count <= 1;
            int running = lastWindow
                ? DownloadStore.Items.Count(d => d.Status == DownloadStatus.Downloading || d.Status == DownloadStatus.Queued)
                : 0;
            if (tabCount < 2 && running == 0) return true;

            string message;
            if (running > 0 && tabCount >= 2)
            {
                message = $"You have {tabCount} tabs open and {DownloadsText(running)} in progress.\n\nClosing the browser closes the tabs and pauses the downloads (you can resume them next time).\n\nClose anyway?";
            }
            else if (running > 0)
            {
                message = $"{CapitalizeFirst(DownloadsText(running))} in progress.\n\nClosing the browser pauses {(running == 1 ? "it" : "them")} (you can resume next time).\n\nClose anyway?";
            }
            else
            {
                message = $"This window has {tabCount} tabs open.\n\nClose them all?";
            }

            return ThemedDialog.Show(this, message, "Close Mint Browser", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        private static string DownloadsText(int count) => count == 1 ? "1 download" : $"{count} downloads";

        private static string CapitalizeFirst(string text) =>
            text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

        /// <summary>
        /// The tabs still open when a window closes join the closed-tab list, so Ctrl+Shift+T brings them back in a new window.
        /// Added last-to-first, so the left-most tab is the first one to reopen.
        /// </summary>
        private void RememberOpenTabsAsClosed()
        {
            for (int i = Tabs.Count - 1; i >= 0; i--)
            {
                string? url = Tabs[i].Url;
                if (!string.IsNullOrWhiteSpace(url) && !IsBuiltInPage(url)) ClosedTabStore.Push(url);
            }
        }
    }
}
