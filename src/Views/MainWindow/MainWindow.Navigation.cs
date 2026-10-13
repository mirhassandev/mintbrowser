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
        // ------------------------------------------------------------------
        // Navigation / address bar
        // ------------------------------------------------------------------
        private static readonly Geometry ReloadGeometry = Geometry.Parse("M 20.91,13.25 A 9,9 0 1 1 12,3 C 14.52,3 16.93,4 18.74,5.74 L 21,8 M 21,3 L 21,8 L 16,8");
        private static readonly Geometry StopGeometry = Geometry.Parse("M 6,6 L 18,18 M 18,6 L 6,18");

        private void UpdateNavigationButtons()
        {
            CoreWebView2? core = SelectedTab?.WebView?.CoreWebView2;
            BtnBack.IsEnabled = core?.CanGoBack ?? false;
            BtnForward.IsEnabled = core?.CanGoForward ?? false;
            bool isLoading = SelectedTab?.IsLoading ?? false;
            if (PathReloadIcon != null)
            {
                PathReloadIcon.Data = isLoading ? StopGeometry : ReloadGeometry;
            }
            BtnReload.ToolTip = isLoading ? "Stop loading this page (Esc)" : "Reload current page (Ctrl+R)";
            SetTopProgressVisible(isLoading);
        }

        // Loading bar: appears only if a load lasts longer than a short grace period, and fades in / out instead of popping.
        private DispatcherTimer? _progressShowTimer;
        private bool _progressWanted;
        private void SetTopProgressVisible(bool loading)
        {
            _progressWanted = loading;
            if (loading)
            {
                if (TopProgressBar.Visibility == Visibility.Visible) { FadeTopProgress(true); return; }
                if (_progressShowTimer == null)
                {
                    _progressShowTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(120) };
                    _progressShowTimer.Tick += (s, e) =>
                    {
                        _progressShowTimer!.Stop();
                        if (_progressWanted) FadeTopProgress(true);
                    };
                }
                if (!_progressShowTimer.IsEnabled) _progressShowTimer.Start();
            }
            else
            {
                _progressShowTimer?.Stop();
                if (TopProgressBar.Visibility == Visibility.Visible) FadeTopProgress(false);
            }
        }
        private void FadeTopProgress(bool show)
        {
            double from = TopProgressBar.Visibility == Visibility.Visible ? TopProgressBar.Opacity : 0;
            if (show)
            {
                TopProgressBar.Visibility = Visibility.Visible;
                TopProgressBar.Opacity = 1; // resting value once the animation ends
                TopProgressBar.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(140))
                {
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
                });
            }
            else
            {
                TopProgressBar.Opacity = 0;
                var fade = new System.Windows.Media.Animation.DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(200))
                {
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
                };
                fade.Completed += (s, e) =>
                {
                    if (!_progressWanted)
                    {
                        TopProgressBar.Visibility = Visibility.Collapsed;
                        TopProgressBar.Opacity = 1;
                    }
                };
                TopProgressBar.BeginAnimation(UIElement.OpacityProperty, fade);
            }
        }
        private string ResolveNavigationTarget(string input, SearchEngineInfo? engine = null)
        {
            input = input.Trim();
            if (IsDownloadsAlias(input)) return DownloadsUrl; // mint://downloads, chrome://downloads, about:downloads
            string? libraryTarget = ResolveLibraryAlias(input); // mint://history, mint://bookmarks, mint://passwords (and chrome:// / about: forms)
            if (libraryTarget != null) return libraryTarget;
            // Explicit, known scheme -> use as typed. (A generic "has a colon" test would break "localhost:3000".)
            int colon = input.IndexOf(':');
            if (colon > 0 && KnownSchemes.Contains(input[..colon], StringComparer.OrdinalIgnoreCase))
            {
                return input;
            }
            // Local file / folder path such as C:\Users\me\page.html
            try
            {
                if (Path.IsPathRooted(input) && (File.Exists(input) || Directory.Exists(input)))
                {
                    return new Uri(input).AbsoluteUri;
                }
            }
            catch
            {
                // Not a valid path - fall through
            }
            if (!input.Contains(' ') && TryGetHostScheme(input, out string scheme))
            {
                return scheme + input;
            }
            string searchTemplate = (engine ?? _activeSearchEngine)?.SearchUrl ?? "https://www.google.com/search?q={0}";
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, searchTemplate, Uri.EscapeDataString(input));
        }
        /// <summary>Decides whether the text looks like a host (example.com, localhost:3000, 192.168.1.1) and which scheme to use.</summary>
        private static bool TryGetHostScheme(string input, out string scheme)
        {
            scheme = "https://";
            if (!Uri.TryCreate("http://" + input, UriKind.Absolute, out Uri? uri)) return false;
            string host = uri.Host;
            if (string.IsNullOrEmpty(host)) return false;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                ((input.Count(c => c == '.') == 3) || input.StartsWith('[')) && IPAddress.TryParse(host.Trim('[', ']'), out _))
            {
                scheme = "http://"; // local / raw-IP servers rarely have TLS
                return true;
            }
            int lastDot = host.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == host.Length - 1) return false;
            // Real hostnames end in an alphabetic TLD - this keeps "3.14" or "file.1" searching instead of navigating.
            string tld = host[(lastDot + 1)..];
            return tld.Length >= 2 &&
                   tld.All(char.IsLetter) &&
                   Uri.CheckHostName(host) == UriHostNameType.Dns;
        }
        private void NavigateToUrl()
        {
            CoreWebView2? core = SelectedTab?.WebView?.CoreWebView2;
            string input = TxtAddressBar.Text;
            if (core == null || string.IsNullOrWhiteSpace(input)) return;
            try
            {
                HideAddressSuggestions();
                AddRecentQuery(input.Trim());
                core.Navigate(ResolveNavigationTarget(input, _activeSearchEngine));
                SelectedTab!.WebView!.Focus();
            }
            catch (ArgumentException)
            {
                HideAddressSuggestions();
                AddRecentQuery(input.Trim());
                string fallbackSearch = _activeSearchEngine?.SearchUrl ?? "https://www.google.com/search?q={0}";
                core.Navigate(string.Format(System.Globalization.CultureInfo.InvariantCulture, fallbackSearch, Uri.EscapeDataString(input.Trim())));
            }
        }
        private void BtnGo_Click(object sender, RoutedEventArgs e) => NavigateToUrl();
        private void TxtAddressBar_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Backspace when address bar is empty clears active search engine badge
            if (e.Key == Key.Back && string.IsNullOrEmpty(TxtAddressBar.Text) && _activeSearchEngine != null)
            {
                e.Handled = true;
                BtnClearActiveEngine_Click(sender, e);
                return;
            }
            bool popupOpen = AddressSuggestionsPopup.IsOpen && AddressSuggestions.Count > 0;
            // Shift + Delete removes highlighted history suggestion (Firefox standard shortcut)
            if (e.Key == Key.Delete && (Keyboard.Modifiers & ModifierKeys.Shift) != 0 && popupOpen)
            {
                if (AddressSuggestionsList.SelectedItem is AddressSuggestion selectedSug && !string.IsNullOrEmpty(selectedSug.Url))
                {
                    e.Handled = true;
                    _dbService.RemoveHistory(selectedSug.Url);
                    _dbService.RemoveBookmark(selectedSug.Url);
                    int selIndex = AddressSuggestionsList.SelectedIndex;
                    AddressSuggestions.Remove(selectedSug);
                    if (AddressSuggestions.Count == 0)
                    {
                        HideAddressSuggestions();
                    }
                    else
                    {
                        int newIndex = Math.Min(selIndex, AddressSuggestions.Count - 1);
                        AddressSuggestionsList.SelectedIndex = newIndex;
                        UpdateAddressBarFromSuggestion(newIndex);
                    }
                    return;
                }
            }
            if (e.Key == Key.Down && popupOpen)
            {
                e.Handled = true;
                // Save what the user typed before we start replacing it with suggestion text.
                if (!_suggestionArrowNavigated)
                {
                    _arrowNavOriginalText = TxtAddressBar.Text;
                    _suggestionArrowNavigated = true;
                }
                int currentIndex = AddressSuggestionsList.SelectedIndex;
                int nextIndex = currentIndex + 1;
                if (nextIndex >= AddressSuggestions.Count)
                {
                    // Past the bottom: cycle back to original typed text
                    AddressSuggestionsList.SelectedIndex = -1;
                    _suppressSuggestionRefresh = true;
                    try
                    {
                        TxtAddressBar.Text = _arrowNavOriginalText;
                        TxtAddressBar.CaretIndex = _arrowNavOriginalText.Length;
                    }
                    finally
                    {
                        _suppressSuggestionRefresh = false;
                    }
                }
                else
                {
                    AddressSuggestionsList.SelectedIndex = nextIndex;
                    AddressSuggestionsList.ScrollIntoView(AddressSuggestionsList.SelectedItem);
                    UpdateAddressBarFromSuggestion(nextIndex);
                }
                return;
            }
            if (e.Key == Key.Up && popupOpen)
            {
                e.Handled = true;
                if (!_suggestionArrowNavigated)
                {
                    _arrowNavOriginalText = TxtAddressBar.Text;
                    _suggestionArrowNavigated = true;
                }
                int currentIndex = AddressSuggestionsList.SelectedIndex;
                if (currentIndex <= 0)
                {
                    if (currentIndex == 0)
                    {
                        // Moving up from first item: return to original typed text
                        AddressSuggestionsList.SelectedIndex = -1;
                        _suppressSuggestionRefresh = true;
                        try
                        {
                            TxtAddressBar.Text = _arrowNavOriginalText;
                            TxtAddressBar.CaretIndex = _arrowNavOriginalText.Length;
                        }
                        finally
                        {
                            _suppressSuggestionRefresh = false;
                        }
                    }
                    else
                    {
                        // Moving up from original text: wrap to bottom
                        int last = AddressSuggestions.Count - 1;
                        AddressSuggestionsList.SelectedIndex = last;
                        AddressSuggestionsList.ScrollIntoView(AddressSuggestionsList.SelectedItem);
                        UpdateAddressBarFromSuggestion(last);
                    }
                }
                else
                {
                    int prevIndex = currentIndex - 1;
                    AddressSuggestionsList.SelectedIndex = prevIndex;
                    AddressSuggestionsList.ScrollIntoView(AddressSuggestionsList.SelectedItem);
                    UpdateAddressBarFromSuggestion(prevIndex);
                }
                return;
            }
            // Tab accepts the highlighted suggestion (like Chrome).
            if (e.Key == Key.Tab && popupOpen &&
                AddressSuggestionsList.SelectedItem is AddressSuggestion tabSuggestion)
            {
                e.Handled = true;
                AcceptAddressSuggestion(tabSuggestion);
                return;
            }
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                // The suggestion list lags the text by the debounce delay. Only trust the highlighted row if it was
                // built for what is typed right now (or the user picked it with the arrow keys); otherwise a fast
                // "type + Enter" navigated to a stale suggestion (e.g. a search for the half-typed text).
                bool listIsCurrent = _suggestionArrowNavigated ||
                    string.Equals(_suggestionsQuery, NormalizeQuery(TxtAddressBar.Text), StringComparison.Ordinal);
                if (popupOpen && listIsCurrent && AddressSuggestionsList.SelectedItem is AddressSuggestion suggestion)
                {
                    AcceptAddressSuggestion(suggestion);
                }
                else
                {
                    NavigateToUrl();
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                // If user was arrow-navigating, first Esc restores their typed text and closes dropdown.
                if (_suggestionArrowNavigated && AddressSuggestionsPopup.IsOpen)
                {
                    _suppressSuggestionRefresh = true;
                    try
                    {
                        TxtAddressBar.Text = _arrowNavOriginalText;
                        TxtAddressBar.CaretIndex = _arrowNavOriginalText.Length;
                    }
                    finally
                    {
                        _suppressSuggestionRefresh = false;
                    }
                    _suggestionArrowNavigated = false;
                    _arrowNavOriginalText = string.Empty;
                    HideAddressSuggestions();
                    return;
                }
                // First Esc (no arrow nav) closes the dropdown, second Esc restores the page address (Chrome behaviour).
                if (AddressSuggestionsPopup.IsOpen)
                {
                    HideAddressSuggestions();
                    return;
                }
                BrowserTab? tab = SelectedTab;
                _suppressSuggestionRefresh = true;
                try
                {
                    TxtAddressBar.Text = tab == null ? "" : GetAddressBarText(tab.Url);
                }
                finally
                {
                    _suppressSuggestionRefresh = false;
                }
                tab?.WebView?.Focus();
            }
        }
        /// <summary>Updates the address bar text to mirror the suggestion at <paramref name="index"/> without
        /// triggering a new suggestion lookup. Caret is placed at the end so the user sees the full text.</summary>
        private void UpdateAddressBarFromSuggestion(int index)
        {
            if (index < 0 || index >= AddressSuggestions.Count) return;
            string text = AddressSuggestions[index].InputText;
            if (string.IsNullOrEmpty(text)) return;
            _suppressSuggestionRefresh = true;
            try
            {
                TxtAddressBar.Text = text;
                TxtAddressBar.CaretIndex = text.Length;
            }
            finally
            {
                _suppressSuggestionRefresh = false;
            }
        }
        // Select the whole URL when the bar gets focus (standard browser behaviour).
        private void TxtAddressBar_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Only select-all here: fetching suggestions for the current URL would pop up a pointless dropdown on every focus.
            TxtAddressBar.SelectAll();
        }
        private void TxtAddressBar_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSuggestionRefresh || !TxtAddressBar.IsKeyboardFocusWithin) return;
            _suggestionArrowNavigated = false;
            _ = RefreshAddressSuggestionsAsync(TxtAddressBar.Text);
        }
        private void TxtAddressBar_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!AddressSuggestionsPopup.IsMouseOver && !TxtAddressBar.IsKeyboardFocusWithin)
                {
                    HideAddressSuggestions();
                }
            }), DispatcherPriority.Input);
        }
        private void AddressBarHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (AddressSuggestionsPopup.IsOpen)
            {
                AddressSuggestionsBorder.Width = Math.Max(440, AddressBarHost.ActualWidth);
            }
        }
        private void AddressSuggestionsPopup_Opened(object sender, EventArgs e)
        {
            AddressSuggestionsBorder.Width = Math.Max(440, AddressBarHost.ActualWidth);
            AddressSuggestionsBorder.Opacity = 0;
            AddressSuggestionsBorder.RenderTransformOrigin = new Point(0.5, 0);
            AddressSuggestionsBorder.RenderTransform = new ScaleTransform(0.985, 0.985);
            AddressSuggestionsList.SelectedIndex = AddressSuggestions.Count > 0 ? 0 : -1;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };
            AddressSuggestionsBorder.BeginAnimation(UIElement.OpacityProperty, fade);
            if (AddressSuggestionsBorder.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(110))
                    { EasingFunction = fade.EasingFunction });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(110))
                    { EasingFunction = fade.EasingFunction });
            }
        }
        private void AddressSuggestionsPopup_Closed(object sender, EventArgs e)
        {
            AddressSuggestionsList.SelectedIndex = -1;
        }
        private static string NormalizeQuery(string? text) => Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        /// <summary>
        /// Privacy gate for the web suggestion lookup. Without it every keystroke goes to Google, even when another
        /// search engine is selected, and even when the text is a URL, a file path or a pasted secret.
        /// </summary>
        private bool ShouldAskWebForSuggestions(string query)
        {
            // The lookup endpoint is Google's: only use it when the search itself would go to Google.
            if (_activeSearchEngine != null && !ReferenceEquals(_activeSearchEngine, EngineGoogle)) return false;
            if (query.Length > 80) return false; // pasted text, not a search
            if (query.Contains("://") || query.Contains('\\')) return false; // URL or Windows path
            if (query.Length >= 2 && char.IsLetter(query[0]) && query[1] == ':') return false; // drive path such as C:/x
            int colon = query.IndexOf(':');
            if (colon > 0 && KnownSchemes.Contains(query[..colon], StringComparer.OrdinalIgnoreCase)) return false;
            if (!query.Contains(' '))
            {
                if (query.Length >= 32) return false; // one long token: key, hash, password
                if (query.Contains('/') || query.Contains('@')) return false; // path or address
                if (TryGetHostScheme(query, out _)) return false; // example.com, localhost:3000, 192.168.1.1
            }
            return true;
        }
        private static bool IsSearchUrl(string url) =>
            url.StartsWith("https://www.google.com/search?q=", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://www.bing.com/search?q=", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://duckduckgo.com/?q=", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://developer.mozilla.org/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://www.perplexity.ai/search?q=", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://en.wikipedia.org/", StringComparison.OrdinalIgnoreCase);
        private async Task RefreshAddressSuggestionsAsync(string rawQuery)
        {
            _suggestionCts?.Cancel();
            _suggestionCts?.Dispose();
            string query = NormalizeQuery(rawQuery);
            if (query.Length == 0)
            {
                // Clear the field reference first: cancelling an already-disposed source throws, which used to
                // leave the dropdown stuck open after the address bar was emptied.
                _suggestionCts = null;
                HideAddressSuggestions();
                return;
            }
            CancellationTokenSource cts = new();
            CancellationToken token = cts.Token; // read now: the source may be disposed by a newer refresh
            _suggestionCts = cts;
            try
            {
                await Task.Delay(60, token);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<AddressSuggestion> results = BuildLocalSuggestions(query, seen);
                // Local matches appear instantly; web suggestions are merged in when (if) they arrive.
                ApplyAddressSuggestions(cts, query, results);
                await AppendRemoteSuggestionsAsync(query, results, seen, token);
                // Show text results immediately — don't block on image downloads.
                ApplyAddressSuggestions(cts, query, results);
                // Prewarm images in the background: when each one loads, re-apply to show it.
                _ = PrewarmAndRefreshAsync(cts, query, results, token);
            }
            catch (OperationCanceledException) { /* expected: a newer keystroke cancelled this lookup */ }
            catch (Exception ex)
            {
                AppLog.Warn("Address suggestions failed", ex);
            }
        }
        private void ApplyAddressSuggestions(CancellationTokenSource cts, string query, List<AddressSuggestion> suggestions)
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(_suggestionCts, cts)) return;
            if (suggestions.Count == 0 || !TxtAddressBar.IsKeyboardFocusWithin)
            {
                HideAddressSuggestions();
                return;
            }
            int previousIndex = AddressSuggestionsList.SelectedIndex;
            _suggestionsQuery = query;
            AddressSuggestions.Clear();
            foreach (AddressSuggestion suggestion in suggestions)
            {
                AddressSuggestions.Add(suggestion);
            }
            if (!AddressSuggestionsPopup.IsOpen) AddressSuggestionsPopup.IsOpen = true;
            // Keep the row the user arrowed to; otherwise highlight the first (the typed text itself).
            AddressSuggestionsList.SelectedIndex =
                _suggestionArrowNavigated && previousIndex >= 0 && previousIndex < AddressSuggestions.Count
                    ? previousIndex
                    : 0;
        }
        private List<AddressSuggestion> BuildLocalSuggestions(string query, HashSet<string> seen)
        {
            var result = new List<AddressSuggestion>();
            SearchEngineInfo activeEngine = _activeSearchEngine ?? EngineGoogle;
            string primaryUrl = ResolveNavigationTarget(query, _activeSearchEngine);
            bool isNavigation = !IsSearchUrl(primaryUrl);
            bool hasWebIcon = isNavigation &&
                              Uri.TryCreate(primaryUrl, UriKind.Absolute, out Uri? primaryUri) &&
                              (primaryUri.Scheme == Uri.UriSchemeHttp || primaryUri.Scheme == Uri.UriSchemeHttps);
            string searchSubtitle = _activeSearchEngine != null ? $"Search with {_activeSearchEngine.Name}" : "Google Search";
            string searchIconUrl = activeEngine.IconUrl;
            result.Add(new AddressSuggestion
            {
                Title = query,
                Subtitle = isNavigation ? "Visit this site" : searchSubtitle,
                TrailingText = isNavigation ? "Enter ↵" : "Search ↵",
                Url = primaryUrl,
                InputText = query,
                IconUrl = !isNavigation ? searchIconUrl : string.Empty,
                HasIcon = !isNavigation,
                IconGlyph = isNavigation ? "\uE774" : "\uE721",
                IsNavigation = true
            });
            seen.Add(query);
            // Open tabs are fast local suggestions.
            foreach (BrowserTab tab in Tabs)
            {
                string tabUrl = tab.Url ?? string.Empty;
                string tabTitle = tab.Title ?? string.Empty;
                if (IsBuiltInPage(tabUrl) || string.IsNullOrWhiteSpace(tabUrl)) continue;
                if (!ContainsSuggestionText(tabTitle, tabUrl, query)) continue;
                if (!seen.Add(tabUrl)) continue;
                result.Add(new AddressSuggestion
                {
                    Title = string.IsNullOrWhiteSpace(tabTitle) ? tabUrl : tabTitle,
                    Subtitle = GetSuggestionDomain(tabUrl),
                    TrailingText = "Switch to tab",
                    Url = tabUrl,
                    InputText = tabUrl,
                    IconUrl = tab.Favicon ?? string.Empty,
                    HasIcon = !string.IsNullOrWhiteSpace(tab.Favicon),
                    IconGlyph = "\uE77B",
                    IsNavigation = true,
                    Tab = tab
                });
                if (result.Count >= 4) break;
            }
            // Database Bookmarks (Firefox Places Provider)
            try
            {
                foreach (BookmarkEntry bookmark in _dbService.GetAllBookmarks())
                {
                    if (string.IsNullOrWhiteSpace(bookmark.Url) || IsBuiltInPage(bookmark.Url)) continue;
                    if (!ContainsSuggestionText(bookmark.Title, bookmark.Url, query)) continue;
                    if (!seen.Add(bookmark.Url)) continue;
                    result.Add(new AddressSuggestion
                    {
                        Title = string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Url : bookmark.Title,
                        Subtitle = GetSuggestionDomain(bookmark.Url),
                        TrailingText = "Bookmark",
                        Url = bookmark.Url,
                        InputText = bookmark.Url,
                        IconUrl = !string.IsNullOrWhiteSpace(bookmark.FaviconUrl) ? bookmark.FaviconUrl : GetFaviconSuggestionUrl(bookmark.Url),
                        HasIcon = true,
                        IconGlyph = "\uE735", // Star icon
                        IsNavigation = true
                    });
                    if (result.Count >= 5) break;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Bookmark suggestions failed", ex);
            }
            // Database History ranked by Frecency (Firefox Places Architecture)
            try
            {
                foreach (HistoryEntry history in _dbService.GetFrecentHistory(60))
                {
                    if (string.IsNullOrWhiteSpace(history.Url) || IsBuiltInPage(history.Url)) continue;
                    if (!ContainsSuggestionText(history.Title, history.Url, query)) continue;
                    if (!seen.Add(history.Url)) continue;
                    result.Add(new AddressSuggestion
                    {
                        Title = string.IsNullOrWhiteSpace(history.Title) ? history.Url : history.Title,
                        Subtitle = GetSuggestionDomain(history.Url),
                        TrailingText = "History",
                        Url = history.Url,
                        InputText = history.Url,
                        IconUrl = !string.IsNullOrWhiteSpace(history.FaviconUrl) ? history.FaviconUrl : GetFaviconSuggestionUrl(history.Url),
                        HasIcon = true,
                        IconGlyph = "\uE81C", // Clock/History icon
                        IsNavigation = true
                    });
                    if (result.Count >= 7) break;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("History suggestions failed", ex);
            }
            foreach (string recent in _recentQueries.AsEnumerable().Reverse())
            {
                if (string.IsNullOrWhiteSpace(recent) || !recent.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(recent)) continue;
                result.Add(CreateSearchSuggestion(recent, "Recent search"));
                if (result.Count >= 8) break;
            }
            return result;
        }
        private async Task AppendRemoteSuggestionsAsync(string query, List<AddressSuggestion> result, HashSet<string> seen, CancellationToken cancellationToken)
        {
            try
            {
                if (!ShouldAskWebForSuggestions(query)) return; // privacy gate
                string endpoint = $"https://www.google.com/complete/search?client=gws-wiz&hl=en&q={Uri.EscapeDataString(query)}";
                using HttpResponseMessage response = await _suggestionClient.GetAsync(endpoint, cancellationToken);
                if (!response.IsSuccessStatusCode) return;
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                int firstBracket = content.IndexOf('[');
                int lastBracket = content.LastIndexOf(']');
                if (firstBracket < 0 || lastBracket <= firstBracket) return;
                string json = content.Substring(firstBracket, lastBracket - firstBracket + 1);
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return;
                JsonElement itemsArray = root[0];
                if (itemsArray.ValueKind != JsonValueKind.Array) return;
                foreach (JsonElement item in itemsArray.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() == 0) continue;
                    string rawText = item[0].GetString() ?? string.Empty;
                    string cleanText = Regex.Replace(rawText, "<.*?>", string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(cleanText)) continue;
                    string displayTitle = cleanText;
                    string subtitle = _activeSearchEngine != null ? $"Search with {_activeSearchEngine.Name}" : "Google Search";
                    string iconUrl = string.Empty;
                    bool hasIcon = false;
                    if (item.GetArrayLength() > 3 && item[3].ValueKind == JsonValueKind.Object)
                    {
                        JsonElement meta = item[3];
                        if (meta.TryGetProperty("zh", out JsonElement zhEl) && !string.IsNullOrWhiteSpace(zhEl.GetString()))
                        {
                            displayTitle = zhEl.GetString()!;
                        }
                        if (meta.TryGetProperty("zi", out JsonElement ziEl) && !string.IsNullOrWhiteSpace(ziEl.GetString()))
                        {
                            subtitle = ziEl.GetString()!;
                        }
                        if (meta.TryGetProperty("zs", out JsonElement zsEl) && !string.IsNullOrWhiteSpace(zsEl.GetString()))
                        {
                            iconUrl = zsEl.GetString()!;
                            hasIcon = true;
                        }
                    }
                    if (!seen.Add(cleanText) && !seen.Add(displayTitle)) continue;
                    bool navigation = TryGetHostScheme(cleanText, out string textScheme);
                    string url = navigation
                        ? textScheme + cleanText
                        : BuildSearchUrl(cleanText);
                    if (navigation)
                    {
                        subtitle = "Web site";
                        iconUrl = string.Empty; // a site the user has not chosen yet gets no request
                        hasIcon = false;
                    }
                    result.Add(new AddressSuggestion
                    {
                        Title = displayTitle,
                        Subtitle = subtitle,
                        TrailingText = navigation ? GetSuggestionDomain(url) : string.Empty,
                        Url = url,
                        InputText = cleanText,
                        IconUrl = iconUrl,
                        HasIcon = hasIcon,
                        IconGlyph = navigation ? "\uE774" : "\uE721",
                        IsNavigation = true
                    });
                    if (result.Count >= 8) break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Offline / blocked / timed out: the local suggestions are already on screen.
                AppLog.Warn("Google suggestion request failed", ex);
            }
        }
        private static HttpClient CreateSuggestionClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
            return client;
        }
        // Dedicated lightweight client for icon images only (separate timeout).
        private static readonly HttpClient _iconClient = new() { Timeout = TimeSpan.FromSeconds(3) };
        /// <summary>
        /// Downloads entity icons in background; on completion, triggers a UI refresh for each item
        /// that got a new icon. Suggestions are already visible — this just upgrades their icons.
        /// </summary>
        private async Task PrewarmAndRefreshAsync(CancellationTokenSource cts, string query,
            List<AddressSuggestion> suggestions, CancellationToken ct)
        {
            bool anyLoaded = false;
            var tasks = suggestions
                .Where(s => s.HasIcon && !string.IsNullOrEmpty(s.IconUrl) &&
                            (s.IconUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) || s.IconUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) &&
                            s.CachedIcon == null)
                .Select(async s =>
                {
                    if (_iconCache.TryGetValue(s.IconUrl, out BitmapImage? cached))
                    {
                        s.CachedIcon = cached;
                        anyLoaded = true;
                        return;
                    }
                    try
                    {
                        using var imgCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        imgCts.CancelAfter(TimeSpan.FromSeconds(2.5));
                        byte[] bytes = s.IconUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                            ? await File.ReadAllBytesAsync(new Uri(s.IconUrl).LocalPath, imgCts.Token)
                            : await DownloadSuggestionIconAsync(s.IconUrl, imgCts.Token);
                        BitmapImage bmp = new();
                        using (var ms = new System.IO.MemoryStream(bytes))
                        {
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = 64;
                            bmp.StreamSource = ms;
                            bmp.EndInit();
                            bmp.Freeze();
                        }
                        if (_iconCache.Count >= 300) _iconCache.Clear(); // small on purpose: icons are cheap to fetch again
                        _iconCache.TryAdd(s.IconUrl, bmp);
                        s.CachedIcon = bmp;
                        anyLoaded = true;
                    }
                    catch (Exception iconEx) { AppLog.Debug("Suggestion icon unavailable - glyph fallback used", iconEx); }
                })
                .ToList();
            if (tasks.Count == 0) return;
            await Task.WhenAll(tasks);
            if (anyLoaded && !ct.IsCancellationRequested)
            {
                // Re-apply on the UI thread to trigger DataTrigger update for icons that loaded.
                _ = Dispatcher.BeginInvoke(new Action(() =>
                    ApplyAddressSuggestions(cts, query, suggestions)));
            }
        }
        private const int MaxSuggestionIconBytes = 256 * 1024;

        /// <summary>Downloads an icon but gives up as soon as it is larger than a real icon can be.</summary>
        private static async Task<byte[]> DownloadSuggestionIconAsync(string url, CancellationToken ct)
        {
            using HttpResponseMessage response = await _iconClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxSuggestionIconBytes) throw new IOException("The icon is too large");
            using Stream body = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxSuggestionIconBytes) throw new IOException("The icon is too large");
            }
            return buffer.ToArray();
        }

        private AddressSuggestion CreateSearchSuggestion(string query, string subtitle)
        {
            return new AddressSuggestion
            {
                Title = query,
                Subtitle = subtitle,
                Url = BuildSearchUrl(query),
                InputText = query,
                IconGlyph = "\uE721",
                IsNavigation = true
            };
        }
        private string BuildSearchUrl(string query) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, (_activeSearchEngine ?? EngineGoogle).SearchUrl, Uri.EscapeDataString(query));

        private static string GetFaviconSuggestionUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || IsBuiltInPage(url))
            {
                return GetLocalFaviconFileUri();
            }
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? siteUri) &&
                (siteUri.Scheme == Uri.UriSchemeHttp || siteUri.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrEmpty(siteUri.Host))
            {
                // Only an icon that is already stored on this computer is used: nothing is requested from any server. Unknown sites stay blank until the page reports its own icon.
                return FaviconCache.TryGetUri(url) ?? string.Empty;
            }
            return GetLocalFaviconFileUri();
        }
        private static string GetSuggestionDomain(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || string.IsNullOrWhiteSpace(uri.Host))
                return url;
            return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.Host[4..]
                : uri.Host;
        }
        private static bool ContainsSuggestionText(string title, string url, string query)
        {
            return title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   url.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        private void AddRecentQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || TryGetHostScheme(query, out _)) return;
            string normalized = Regex.Replace(query, @"\s+", " ").Trim();
            if (normalized.Length < 2) return;
            _recentQueries.RemoveAll(x => x.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            _recentQueries.Add(normalized);
            if (_recentQueries.Count > 30) _recentQueries.RemoveAt(0);
        }
        private void AddressSuggestionsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && source is not ListBoxItem)
            {
                source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }
            if (source is ListBoxItem item && item.DataContext is AddressSuggestion suggestion)
            {
                e.Handled = true;
                AcceptAddressSuggestion(suggestion);
            }
        }
        private void AcceptAddressSuggestion(AddressSuggestion suggestion)
        {
            _suppressSuggestionRefresh = true;
            TxtAddressBar.Text = suggestion.InputText;
            _suppressSuggestionRefresh = false;
            HideAddressSuggestions();
            // "Switch to tab" rows jump to the existing tab instead of loading the page a second time.
            if (suggestion.Tab != null && Tabs.Contains(suggestion.Tab))
            {
                SelectedTab = suggestion.Tab;
                return;
            }
            CoreWebView2? core = SelectedTab?.WebView?.CoreWebView2;
            if (core == null) return;
            AddRecentQuery(suggestion.InputText);
            try
            {
                core.Navigate(suggestion.Url);
                SelectedTab?.WebView?.Focus();
            }
            catch (ArgumentException) { NavigateToUrl(); }
        }
        // ------------------------------------------------------------------
        // New-tab shortcuts (user-managed, persisted to shortcuts.json, shared by all windows)
        // ------------------------------------------------------------------

        private sealed class ShortcutEntry
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Title { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            
        }

        private const int MaxShortcuts = 10; // same cap as Chrome
        private static readonly object ShortcutLock = new object();
        private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
        private static List<ShortcutEntry>? _shortcuts;

        private static string ShortcutFilePath => Path.Combine(GetUserDataFolder(), "shortcuts.json");

        private static ShortcutEntry MakeShortcut(string title, string url)
        {
            
            return new ShortcutEntry
            {
                Title = title,
                Url = url
            };
        }

        private static List<ShortcutEntry> LoadShortcutsLocked()
        {
            if (_shortcuts != null) return _shortcuts;

            try
            {
                if (File.Exists(ShortcutFilePath))
                {
                    _shortcuts = JsonSerializer.Deserialize<List<ShortcutEntry>>(File.ReadAllText(ShortcutFilePath)) ?? new List<ShortcutEntry>();
                    return _shortcuts;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not read shortcuts.json", ex);
                // The defaults below are saved over the file: keep the unreadable original so nothing is lost for good.
                try
                {
                    if (File.Exists(ShortcutFilePath)) File.Copy(ShortcutFilePath, ShortcutFilePath + ".bad", overwrite: true);
                }
                catch (Exception copyEx) { AppLog.Debug("Could not back up unreadable shortcuts.json", copyEx); }
            }

            // First run: start with a few sensible shortcuts the user can edit or remove.
            _shortcuts = new List<ShortcutEntry>
            {
                MakeShortcut("Google", "https://www.google.com/"),
                MakeShortcut("YouTube", "https://www.youtube.com/"),
                MakeShortcut("GitHub", "https://github.com/"),
                MakeShortcut("Wikipedia", "https://www.wikipedia.org/"),
                MakeShortcut("DuckDuckGo", "https://duckduckgo.com/"),
                MakeShortcut("Reddit", "https://www.reddit.com/")
            };
            SaveShortcutsLocked();
            return _shortcuts;
        }

        private static void SaveShortcutsLocked()
        {
            try
            {
                string json = JsonSerializer.Serialize(_shortcuts, IndentedJson);
                string tmp = ShortcutFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, ShortcutFilePath, overwrite: true); // a crash mid-write can no longer truncate the file
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not save shortcuts.json", ex);
            }
        }

        /// <summary>Accepts "github.com", "https://github.com/x" ... and returns a valid http(s) URL, or null.</summary>
        private static string? NormalizeShortcutUrl(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();
            if (!input.Contains("://")) input = "https://" + input;

            if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            if (string.IsNullOrEmpty(uri.Host) || (!uri.Host.Contains('.') && uri.Host != "localhost")) return null;
            return uri.AbsoluteUri;
        }

        private static string DefaultShortcutTitle(string url)
        {
            string host = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : url;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            return host.Length > 0 ? char.ToUpperInvariant(host[0]) + host[1..] : url;
        }

        /// <summary>Returns an error message, or null on success.</summary>
        private static string? AddShortcut(string? title, string? url)
        {
            string? normalized = NormalizeShortcutUrl(url);
            if (normalized == null) return "Enter a valid web address.";

            lock (ShortcutLock)
            {
                List<ShortcutEntry> list = LoadShortcutsLocked();
                if (list.Count >= MaxShortcuts) return $"You can have up to {MaxShortcuts} shortcuts. Remove one to add another.";
                if (list.Any(s => string.Equals(s.Url, normalized, StringComparison.OrdinalIgnoreCase))) return "That shortcut already exists.";

                list.Add(MakeShortcut(string.IsNullOrWhiteSpace(title) ? DefaultShortcutTitle(normalized) : title.Trim(), normalized));
                SaveShortcutsLocked();
            }
            return null;
        }

        private static string? UpdateShortcut(string? id, string? title, string? url)
        {
            string? normalized = NormalizeShortcutUrl(url);
            if (normalized == null) return "Enter a valid web address.";

            lock (ShortcutLock)
            {
                List<ShortcutEntry> list = LoadShortcutsLocked();
                int index = list.FindIndex(s => s.Id == id);
                if (index < 0) return "That shortcut no longer exists.";
                if (list.Any(s => s.Id != id && string.Equals(s.Url, normalized, StringComparison.OrdinalIgnoreCase))) return "That shortcut already exists.";

                ShortcutEntry updated = MakeShortcut(string.IsNullOrWhiteSpace(title) ? DefaultShortcutTitle(normalized) : title.Trim(), normalized);
                updated.Id = list[index].Id; // keep identity so the page can keep tracking it
                list[index] = updated;
                SaveShortcutsLocked();
            }
            return null;
        }

        private static string? RemoveShortcut(string? id)
        {
            lock (ShortcutLock)
            {
                List<ShortcutEntry> list = LoadShortcutsLocked();
                list.RemoveAll(s => s.Id == id);
                SaveShortcutsLocked();
            }
            return null;
        }

        private static string BuildShortcutsMessage()
        {
            List<ShortcutEntry> snapshot;
            lock (ShortcutLock)
            {
                snapshot = LoadShortcutsLocked().ToList();
            }

            var data = snapshot.Select(s => new { id = s.Id, title = s.Title, url = s.Url, iconUrl = FaviconCache.TryGetDataUri(s.Url) ?? string.Empty });
            return JsonSerializer.Serialize(new { type = "top-sites-data", maxShortcuts = MaxShortcuts, data }, BridgeJsonOptions);
        }

        private void HideAddressSuggestions()
        {
            _suggestionCts?.Cancel();
            _suggestionsQuery = string.Empty;
            _suggestionArrowNavigated = false;
            AddressSuggestions.Clear();
            AddressSuggestionsList.SelectedIndex = -1;
            AddressSuggestionsPopup.IsOpen = false;
        }
        private void TxtAddressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // First click focuses + selects all; later clicks place the caret normally.
            if (!TxtAddressBar.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                TxtAddressBar.Focus();
            }
        }
        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            CoreWebView2? core = SelectedTab?.WebView?.CoreWebView2;
            if (core != null && core.CanGoBack)
            {
                core.GoBack();
            }
        }
        private void BtnForward_Click(object sender, RoutedEventArgs e)
        {
            CoreWebView2? core = SelectedTab?.WebView?.CoreWebView2;
            if (core != null && core.CanGoForward)
            {
                core.GoForward();
            }
        }
        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            CoreWebView2? core = SelectedTab?.WebView?.CoreWebView2;
            if (core == null) return;
            if (SelectedTab?.IsLoading == true)
            {
                core.Stop();
                SelectedTab.IsLoading = false;
                UpdateNavigationButtons();
            }
            else
            {
                core.Reload();

                // Smooth one-turn spin so the click feels acknowledged.
                ReloadRotate.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(450))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                });
            }
        }
        // ------------------------------------------------------------------
        // Extensions (uBlock)
        // ------------------------------------------------------------------
        private async void BtnUBlock_Click(object sender, RoutedEventArgs e)
        {
            CoreWebView2? core = SelectedTab?.WebView?.CoreWebView2;
            if (core == null) return;
            try
            {
                var profile = core.Profile;
                var extensions = await profile.GetBrowserExtensionsAsync();
                foreach (var ext in extensions)
                {
                    if (ext.Name.Contains("uBlock", StringComparison.OrdinalIgnoreCase) ||
                        ext.Id.Equals("cjpalhdlnbpafiamejdnhcphjbkeiagm", StringComparison.OrdinalIgnoreCase))
                    {
                        await AddNewTabAsync($"chrome-extension://{ext.Id}/dashboard.html");
                        return;
                    }
                }
                // Not loaded yet: try the copy that ships with the app before asking the user.
                string bundledDir = Path.Combine(AppContext.BaseDirectory, "Extensions", "uBlockOrigin");
                if (File.Exists(Path.Combine(bundledDir, "manifest.json")))
                {
                    var bundledExt = await profile.AddBrowserExtensionAsync(bundledDir);
                    await AddNewTabAsync($"chrome-extension://{bundledExt.Id}/dashboard.html");
                    return;
                }
                var dialog = new OpenFileDialog
                {
                    Title = "Select uBlock Origin manifest.json file",
                    Filter = "Extension Manifest (manifest.json)|manifest.json"
                };
                if (dialog.ShowDialog() == true)
                {
                    string extensionFolder = Path.GetDirectoryName(dialog.FileName)!;
                    var newExt = await profile.AddBrowserExtensionAsync(extensionFolder);
                    await AddNewTabAsync($"chrome-extension://{newExt.Id}/dashboard.html");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("uBlock button failed", ex);
                ThemedDialog.Show($"Could not load extension: {ex.Message}", "Extension Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

