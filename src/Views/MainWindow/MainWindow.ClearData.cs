using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using CustomBrowser.Helpers;
using CustomBrowser.Services;
using CustomBrowser.Views;

namespace CustomBrowser
{
    /// <summary>"Clear browsing data" (Ctrl+Shift+Delete): history, cookies and site data, cache, passwords and autofill for a time range.</summary>
    public partial class MainWindow
    {
        private bool _clearDataOpen;

        // async void is required for the call from a shortcut; every exception is caught inside.
        private async void ShowClearBrowsingDataDialog()
        {
            if (_clearDataOpen) return; // the shortcut must not stack a second dialog
            _clearDataOpen = true;
            try
            {
                HideTabPreview();
                HideAddressSuggestions();
                var dialog = new ClearDataWindow(this);
                if (dialog.ShowDialog() != true || dialog.Choice == null) return;
                await ClearBrowsingDataAsync(dialog.Choice);
            }
            catch (Exception ex)
            {
                AppLog.Error("Clearing browsing data failed", ex);
                ThemedDialog.Show(this, $"Not everything could be cleared: {ex.Message}\n\nDetails were saved to the log file:\n{AppLog.LogFilePath}",
                    "Clear browsing data", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _clearDataOpen = false;
            }
        }

        private async Task ClearBrowsingDataAsync(ClearDataChoice choice)
        {
            DateTime? since = choice.Range == null ? null : DateTime.Now - choice.Range.Value;

            // Clearing everything is the one case that cannot be taken back and signs the user out everywhere.
            if (since == null && (choice.Cookies || choice.Passwords) &&
                ThemedDialog.Show(this, "You are about to remove data from ALL TIME, including cookies or saved passwords.\n\nThis cannot be undone.\n\nContinue?",
                    "Clear browsing data", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            var kinds = (CoreWebView2BrowsingDataKinds)0;
            var cleared = new List<string>();
            if (choice.History) { kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory; cleared.Add("browsing history"); }
            if (choice.Cookies) { kinds |= CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllSite; cleared.Add("cookies and site data"); }
            if (choice.Cache) { kinds |= CoreWebView2BrowsingDataKinds.DiskCache; cleared.Add("cached files"); }
            if (choice.Passwords) { kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave; cleared.Add("saved passwords"); }
            if (choice.Autofill) { kinds |= CoreWebView2BrowsingDataKinds.GeneralAutofill; cleared.Add("autofill data"); }

            // All tabs share one profile, so any open tab can clear it.
            CoreWebView2? anyCore = SelectedTab?.WebView?.CoreWebView2 ??
                                    Tabs.Select(t => t.WebView?.CoreWebView2).FirstOrDefault(c => c != null);
            if ((int)kinds != 0)
            {
                if (anyCore == null) throw new InvalidOperationException("No page is open, so the browser data cannot be reached. Open a tab and try again.");
                if (since == null) await anyCore.Profile.ClearBrowsingDataAsync(kinds);
                else await anyCore.Profile.ClearBrowsingDataAsync(kinds, since.Value, DateTime.Now.AddMinutes(1));
            }

            if (choice.History)
            {
                // The history list, the address-bar searches and the closed-tab list are the app's own copies of the same activity.
                if (since == null) _dbService.ClearHistory();
                else _dbService.ClearHistorySince(since.Value);
                BrowserSettings.ClearRecentSearches();
                ClosedTabStore.Clear();
            }

            AppLog.Info($"Cleared {string.Join(", ", cleared)} ({choice.RangeLabel})");
            string what = cleared.Count == 1 ? cleared[0] : string.Join(", ", cleared.Take(cleared.Count - 1)) + " and " + cleared[^1];
            string reload = choice.Cookies || choice.Cache ? "\n\nReload open tabs to see the change." : string.Empty;
            ThemedDialog.Show(this, $"Removed {what} ({choice.RangeLabel.ToLowerInvariant()}).{reload}",
                "Browsing data cleared", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
