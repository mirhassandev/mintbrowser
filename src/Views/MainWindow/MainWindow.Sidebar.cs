using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using CustomBrowser.Helpers;
using CustomBrowser.Views;
using CustomBrowser.Models;
using CustomBrowser.Services;
namespace CustomBrowser
{
    public partial class MainWindow
    {
        // --- DATABASE IPC & UI HELPERS (Firefox Places UI Architecture) ---
        public static IEnumerable<CustomBrowser.Models.BookmarkEntry> GetAllBookmarks()
        {
            return _dbService.GetAllBookmarks();
        }
        public bool ToggleBookmarkCurrentPage()
        {
            if (SelectedTab?.WebView?.CoreWebView2 == null) return false;
            var core = SelectedTab.WebView.CoreWebView2;
            string url = core.Source;
            if (string.IsNullOrWhiteSpace(url) || IsBuiltInPage(url)) return false;
            string title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? url : core.DocumentTitle;
            var existing = _dbService.GetBookmark(url);
            if (existing == null)
            {
                // Add bookmark and open Firefox-style edit panel
                _dbService.AddOrUpdateBookmark(title, url, faviconUrl: SelectedTab.Favicon ?? "");
                UpdateBookmarkVisualState();
                OpenBookmarkEditPopover(title, url, "Bookmarks Toolbar");
                return true;
            }
            else
            {
                // Already bookmarked -> open edit popover
                OpenBookmarkEditPopover(existing.Title, existing.Url, existing.Folder);
                return false;
            }
        }
        private void OpenBookmarkEditPopover(string title, string url, string folder)
        {
            TxtBookmarkTitle.Text = title;
            TxtBookmarkUrl.Text = url;
            TxtBookmarkFolder.Text = string.IsNullOrWhiteSpace(folder) ? "Bookmarks Toolbar" : folder;
            BookmarkEditPopup.IsOpen = true;
            TxtBookmarkTitle.Focus();
            TxtBookmarkTitle.SelectAll();
        }
        public void UpdateBookmarkVisualState()
        {
            try
            {
                if (PathBookmarkStar == null) return;
                string? url = SelectedTab?.Url;
                if (string.IsNullOrWhiteSpace(url) || IsBuiltInPage(url))
                {
                    PathBookmarkStar.Fill = Brushes.Transparent;
                    PathBookmarkStar.Stroke = new SolidColorBrush(Color.FromRgb(176, 176, 186));
                    BtnBookmark.ToolTip = "Bookmark this tab (Ctrl+D)";
                    return;
                }
                bool isBookmarked = _dbService.IsBookmarked(url);
                if (isBookmarked)
                {
                    PathBookmarkStar.Fill = new SolidColorBrush(Color.FromRgb(250, 250, 250)); // Frosted-white filled star
                    PathBookmarkStar.Stroke = new SolidColorBrush(Color.FromRgb(250, 250, 250));
                    BtnBookmark.ToolTip = "Edit bookmark for this tab (Ctrl+D)";
                }
                else
                {
                    PathBookmarkStar.Fill = Brushes.Transparent;
                    PathBookmarkStar.Stroke = new SolidColorBrush(Color.FromRgb(176, 176, 186));
                    BtnBookmark.ToolTip = "Bookmark this tab (Ctrl+D)";
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("UpdateBookmarkVisualState failed", ex);
            }
        }
        private void BtnBookmark_Click(object sender, RoutedEventArgs e)
        {
            ToggleBookmarkCurrentPage();
        }
        private void BtnSaveBookmark_Click(object sender, RoutedEventArgs e)
        {
            string title = TxtBookmarkTitle.Text.Trim();
            string url = TxtBookmarkUrl.Text.Trim();
            string folder = TxtBookmarkFolder.Text.Trim();
            if (!string.IsNullOrWhiteSpace(url))
            {
                _dbService.AddOrUpdateBookmark(title, url, folder, faviconUrl: SelectedTab?.Favicon ?? "");
                UpdateBookmarkVisualState();
            }
            BookmarkEditPopup.IsOpen = false;
        }
        private void BtnRemoveBookmark_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtBookmarkUrl.Text.Trim();
            if (!string.IsNullOrWhiteSpace(url))
            {
                _dbService.RemoveBookmark(url);
                UpdateBookmarkVisualState();
            }
            BookmarkEditPopup.IsOpen = false;
        }
        private DatabaseService.HistorySortMode _currentHistorySortMode = DatabaseService.HistorySortMode.Date;
        private string _currentSidebarView = "History";
        private void BtnHistory_Click(object sender, RoutedEventArgs e)
        {
            ToggleSidebar("History");
        }
        private void ToggleSidebar(string? view = null)
        {
            if (SidebarPanel.Visibility == Visibility.Visible && (string.IsNullOrEmpty(view) || _currentSidebarView == view))
            {
                HideSidebarPanel();
                return;
            }
            if (!string.IsNullOrEmpty(view))
            {
                SwitchSidebarView(view);
            }
            ShowSidebarPanel();
            if (_currentSidebarView == "History")
            {
                TxtSidebarSearch.Text = string.Empty;
                RefreshSidebarHistory();
                TxtSidebarSearch.Focus();
            }
            else if (_currentSidebarView == "Bookmarks")
            {
                TxtSidebarSearch.Text = string.Empty;
                RefreshSidebarBookmarks();
            }
        }
        private double _sidebarWidth = 300;

        private void ShowSidebarPanel()
        {
            SidebarColumn.Width = new GridLength(Math.Max(240, Math.Min(600, _sidebarWidth)));
            SidebarResizer.Visibility = Visibility.Visible;
            SidebarPanel.Visibility = Visibility.Visible;
        }

        private void HideSidebarPanel()
        {
            if (SidebarColumn.ActualWidth > 0) _sidebarWidth = SidebarColumn.ActualWidth;
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarResizer.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
        }

        private void BtnCloseSidebar_Click(object sender, RoutedEventArgs e)
        {
            HideSidebarPanel();
        }
        private void BtnSidebarViewSwitcher_Click(object sender, RoutedEventArgs e)
        {
            SidebarViewPopup.IsOpen = !SidebarViewPopup.IsOpen;
        }
        private void SwitchSidebarView(string view)
        {
            _currentSidebarView = view;
            TxtSidebarTitle.Text = view;
            TxtSidebarSearchWatermark.Text = view == "Bookmarks" ? "Search bookmarks" : "Search history";
            SidebarViewPopup.IsOpen = false;
            SidebarHistoryScroll.Visibility = view == "History" ? Visibility.Visible : Visibility.Collapsed;
            SidebarBookmarksScroll.Visibility = view == "Bookmarks" ? Visibility.Visible : Visibility.Collapsed;
            SidebarPasswordsPanel.Visibility = view == "Passwords" ? Visibility.Visible : Visibility.Collapsed;
            SidebarSearchRow.Visibility = view == "Passwords" ? Visibility.Collapsed : Visibility.Visible;
            if (view == "Passwords") TxtPasswordsStatus.Text = string.Empty;
            if (view == "History") RefreshSidebarHistory();
            else if (view == "Bookmarks") RefreshSidebarBookmarks();
        }
        private void BtnSelectBookmarks_Click(object sender, RoutedEventArgs e) => SwitchSidebarView("Bookmarks");
        private void BtnSelectHistory_Click(object sender, RoutedEventArgs e) => SwitchSidebarView("History");
        private void BtnSelectPasswords_Click(object sender, RoutedEventArgs e) => SwitchSidebarView("Passwords");
        /// <summary>
        /// WebView2 offers no way to list or edit single saved logins; the only management it exposes is removing
        /// all of them from the profile. Nothing here ever reads, shows or logs a password.
        /// </summary>
        private async void BtnClearSavedPasswords_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CoreWebView2? core = SelectedTab?.WebView?.CoreWebView2;
                if (core == null)
                {
                    TxtPasswordsStatus.Text = "Open a page first, then try again.";
                    return;
                }
                if (!ConfirmDestructive("Clear saved passwords",
                        "Remove ALL passwords saved by Mint Browser?\n\nThis cannot be undone. Sites will offer to save your logins again the next time you sign in."))
                {
                    return;
                }
                BtnClearSavedPasswords.IsEnabled = false;
                TxtPasswordsStatus.Text = "Clearing...";
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.PasswordAutosave);
                TxtPasswordsStatus.Text = "Saved passwords were removed.";
                AppLog.Info("Saved passwords cleared from the WebView2 profile");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Clearing saved passwords failed", ex);
                TxtPasswordsStatus.Text = "Could not clear saved passwords: " + ex.Message;
            }
            finally
            {
                BtnClearSavedPasswords.IsEnabled = true;
            }
        }
        private void TxtSidebarSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtSidebarSearchWatermark.Visibility = string.IsNullOrEmpty(TxtSidebarSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            string q = TxtSidebarSearch.Text.Trim();
            if (_currentSidebarView == "History")
            {
                RefreshSidebarHistory(q);
            }
            else if (_currentSidebarView == "Bookmarks")
            {
                RefreshSidebarBookmarks(q);
            }
        }
        private readonly HashSet<string> _collapsedHistoryGroups = new HashSet<string>();

        // Click on a group header (date / site) collapses or expands that group.
        private void HistoryGroupHeader_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not HistoryGroup group) return;

            group.IsExpanded = !group.IsExpanded;

            // Only remember the choice outside of search (search temporarily expands everything).
            if (string.IsNullOrWhiteSpace(TxtSidebarSearch.Text))
            {
                if (group.IsExpanded) _collapsedHistoryGroups.Remove(group.Header);
                else _collapsedHistoryGroups.Add(group.Header);
            }

            e.Handled = true;
        }
        private void RefreshSidebarHistory(string filterQuery = "")
        {
            try
            {
                var groups = _dbService.GetGroupedHistory(filterQuery, _currentHistorySortMode);

                // Remember which groups the user collapsed. While searching, show every group so no match is hidden.
                bool searching = !string.IsNullOrWhiteSpace(filterQuery);
                foreach (HistoryGroup group in groups)
                {
                    group.IsExpanded = searching || !_collapsedHistoryGroups.Contains(group.Header);
                }

                SidebarHistoryGroupList.ItemsSource = groups;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Failed to load grouped history", ex);
            }
        }
        private void RefreshSidebarBookmarks(string filterQuery = "")
        {
            try
            {
                var bookmarks = _dbService.GetAllBookmarks();
                if (!string.IsNullOrWhiteSpace(filterQuery))
                {
                    bookmarks = bookmarks.Where(b =>
                        (b.Title?.Contains(filterQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (b.Url?.Contains(filterQuery, StringComparison.OrdinalIgnoreCase) ?? false));
                }
                SidebarBookmarksList.ItemsSource = bookmarks.ToList();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Failed to load bookmarks list", ex);
            }
        }
        private void BtnSidebarMoreOptions_Click(object sender, RoutedEventArgs e)
        {
            SidebarSortPopup.IsOpen = !SidebarSortPopup.IsOpen;
        }
        private void UpdateSortCheckmarks()
        {
            CheckSortDate.Visibility        = _currentHistorySortMode == DatabaseService.HistorySortMode.Date         ? Visibility.Visible : Visibility.Collapsed;
            CheckSortSite.Visibility        = _currentHistorySortMode == DatabaseService.HistorySortMode.Site         ? Visibility.Visible : Visibility.Collapsed;
            CheckSortLastVisited.Visibility  = _currentHistorySortMode == DatabaseService.HistorySortMode.LastVisited  ? Visibility.Visible : Visibility.Collapsed;
            CheckSortMostVisited.Visibility  = _currentHistorySortMode == DatabaseService.HistorySortMode.MostVisited  ? Visibility.Visible : Visibility.Collapsed;
        }
        private void BtnSortByDate_Click(object sender, RoutedEventArgs e)
        {
            _currentHistorySortMode = DatabaseService.HistorySortMode.Date;
            UpdateSortCheckmarks();
            SidebarSortPopup.IsOpen = false;
            RefreshSidebarHistory(TxtSidebarSearch.Text.Trim());
        }
        private void BtnSortBySite_Click(object sender, RoutedEventArgs e)
        {
            _currentHistorySortMode = DatabaseService.HistorySortMode.Site;
            UpdateSortCheckmarks();
            SidebarSortPopup.IsOpen = false;
            RefreshSidebarHistory(TxtSidebarSearch.Text.Trim());
        }
        private void BtnSortByLastVisited_Click(object sender, RoutedEventArgs e)
        {
            _currentHistorySortMode = DatabaseService.HistorySortMode.LastVisited;
            UpdateSortCheckmarks();
            SidebarSortPopup.IsOpen = false;
            RefreshSidebarHistory(TxtSidebarSearch.Text.Trim());
        }
        private void BtnSortByMostVisited_Click(object sender, RoutedEventArgs e)
        {
            _currentHistorySortMode = DatabaseService.HistorySortMode.MostVisited;
            UpdateSortCheckmarks();
            SidebarSortPopup.IsOpen = false;
            RefreshSidebarHistory(TxtSidebarSearch.Text.Trim());
        }
        private void BtnClearSidebarHistory_Click(object sender, RoutedEventArgs e)
        {
            SidebarSortPopup.IsOpen = false;
            if (ConfirmDestructive("Clear History", "Clear ALL browsing history?\n\nThis cannot be undone."))
            {
                _dbService.ClearHistory();
                RefreshSidebarHistory();
            }
        }
        private void SidebarHistoryItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is HistoryEntry entry && !string.IsNullOrWhiteSpace(entry.Url))
            {
                NavigateOrOpenTab(entry.Url);
            }
        }
        private void BtnDeleteHistoryRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                _dbService.RemoveHistory(url);
                RefreshSidebarHistory(TxtSidebarSearch.Text.Trim());
            }
        }
        private void SidebarBookmarkItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is BookmarkEntry bookmark && !string.IsNullOrWhiteSpace(bookmark.Url))
            {
                NavigateOrOpenTab(bookmark.Url);
            }
        }
        private void BtnDeleteBookmarkRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                _dbService.RemoveBookmark(url);
                RefreshSidebarBookmarks(TxtSidebarSearch.Text.Trim());
                UpdateBookmarkVisualState();
            }
        }
        private void NavigateOrOpenTab(string url)
        {
            if (SelectedTab?.WebView?.CoreWebView2 != null)
            {
                SelectedTab.WebView.CoreWebView2.Navigate(url);
            }
            else
            {
                _ = OpenTabSafeAsync(url);
            }
        }
        // Search engine selector & omnibox shortcuts
        private void BtnSearchEngineSelector_Click(object sender, RoutedEventArgs e)
        {
            SearchEnginePopup.IsOpen = !SearchEnginePopup.IsOpen;
        }
        private void SelectSearchEngine(SearchEngineInfo? engine)
        {
            SearchEnginePopup.IsOpen = false;
            _activeSearchEngine = engine;
            UpdateActiveEngineVisuals();
            string text = TxtAddressBar.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text) && engine != null)
            {
                _ = RefreshAddressSuggestionsAsync(text);
                TxtAddressBar.Focus();
                TxtAddressBar.CaretIndex = TxtAddressBar.Text.Length;
            }
            else
            {
                TxtAddressBar.Focus();
            }
        }
        private void BtnClearActiveEngine_Click(object sender, RoutedEventArgs e)
        {
            _activeSearchEngine = null;
            UpdateActiveEngineVisuals();
            TxtAddressBar.Focus();
            if (!string.IsNullOrWhiteSpace(TxtAddressBar.Text))
            {
                _ = RefreshAddressSuggestionsAsync(TxtAddressBar.Text.Trim());
            }
        }
        private void UpdateActiveEngineVisuals()
        {
            if (_activeSearchEngine == null)
            {
                ActiveEnginePill.Visibility = Visibility.Collapsed;
                BtnSearchEngineSelector.Visibility = Visibility.Visible;
                TxtAddressBar.Tag = "Search with Google or enter address";
            }
            else
            {
                ActiveEnginePill.Visibility = Visibility.Visible;
                BtnSearchEngineSelector.Visibility = Visibility.Collapsed;
                TxtActiveEngineName.Text = _activeSearchEngine.Name;
                try
                {
                    ImgActiveEngine.Source = _activeSearchEngine.Icon;
                }
                catch (Exception engineIconEx) { AppLog.Debug("Search engine icon could not be loaded", engineIconEx); }
                ActiveEnginePill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double pillWidth = ActiveEnginePill.DesiredSize.Width;
                if (pillWidth < 40) pillWidth = 90;
                TxtAddressBar.Tag = "Search the Web";
            }
        }
        private void BtnEngineGoogle_Click(object sender, RoutedEventArgs e) => SelectSearchEngine(EngineGoogle);
        private void BtnEngineBing_Click(object sender, RoutedEventArgs e) => SelectSearchEngine(EngineBing);
        private void BtnEngineDuckDuckGo_Click(object sender, RoutedEventArgs e) => SelectSearchEngine(EngineDuckDuckGo);
        private void BtnEngineMDN_Click(object sender, RoutedEventArgs e) => SelectSearchEngine(EngineMDN);
        private void BtnEnginePerplexity_Click(object sender, RoutedEventArgs e) => SelectSearchEngine(EnginePerplexity);
        private void BtnEngineWikipedia_Click(object sender, RoutedEventArgs e) => SelectSearchEngine(EngineWikipedia);
    }
}


