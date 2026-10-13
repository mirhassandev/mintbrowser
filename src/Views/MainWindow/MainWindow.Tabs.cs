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
        // Tabs
        // ------------------------------------------------------------------
        private async Task<BrowserTab> AddNewTabAsync(string initialUrl = NewTabUrl, BrowserTab? opener = null, int insertIndex = -1)
        {
            CoreWebView2Environment environment = await GetEnvironmentAsync();
            bool isNewTab = string.Equals(initialUrl, NewTabUrl, StringComparison.OrdinalIgnoreCase);
            var webView = new WebView2
            {
                DefaultBackgroundColor = isNewTab
                    ? System.Drawing.Color.Transparent
                    : System.Drawing.Color.FromArgb(255, 34, 34, 34),
                Visibility = Visibility.Collapsed
            };
            var tab = new BrowserTab
            {
                Title = isNewTab ? "New Tab" : IsDownloadsPage(initialUrl) ? "Downloads" : IsLibraryPage(initialUrl) ? LibraryTitle(LibrarySectionOf(initialUrl)) : (GetSuggestionDomain(initialUrl) ?? "Loading..."),
                Url = initialUrl,
                Favicon = isNewTab ? GetLocalFaviconFileUri() : GetFaviconSuggestionUrl(initialUrl),
                WebView = webView
            };
            // Every tab's WebView stays in the visual tree; switching tabs only toggles visibility.
            // (Removing/re-adding a WebView2 on each switch is slow and can lose rendering state.)
            tab.Opener = opener;
            tab.IsPopupSpawned = opener != null;
            TabContentGrid.Children.Add(webView);
            if (insertIndex >= 0 && insertIndex <= Tabs.Count)
            {
                Tabs.Insert(insertIndex, tab);
            }
            else
            {
                Tabs.Add(tab);
            }
            _tabOwners[tab] = this;
            SelectedTab = tab; // makes the WebView visible so its HWND gets created
            try
            {
                await webView.EnsureCoreWebView2Async(environment);
                webView.DefaultBackgroundColor = isNewTab
                    ? System.Drawing.Color.Transparent
                    : System.Drawing.Color.FromArgb(255, 34, 34, 34);
                CoreWebView2 core = webView.CoreWebView2;
                string assetFolder = GetAssetFolder();
                if (Directory.Exists(assetFolder))
                {
                    core.SetVirtualHostNameToFolderMapping("local.browser", assetFolder, CoreWebView2HostResourceAccessKind.DenyCors);
                }
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDevToolsEnabled = true;
                // Built-in password saving and form autofill (both are off by default in WebView2).
                // Saved data lives only in this app's own WebView2 profile, not in Edge.
                try
                {
                    core.Profile.IsPasswordAutosaveEnabled = true;
                    core.Profile.IsGeneralAutofillEnabled = true;
                }
                catch (Exception autofillEx)
                {
                    AppLog.Warn("Could not enable password saving / form autofill", autofillEx);
                }
                // Wire up events BEFORE the (potentially slow) extension load and first navigation,
                // so no title/url/download events are missed.
                core.DocumentTitleChanged += (s, e) =>
                {
                    string title = core.DocumentTitle;
                    if (tab.Url == NewTabUrl)
                    {
                        tab.Title = "New Tab";
                    }
                    else if (string.IsNullOrWhiteSpace(title))
                    {
                        // Untitled page: show its host (like Chrome/Firefox) instead of a misleading "New Tab".
                        string host = GetSuggestionDomain(tab.Url ?? string.Empty);
                        tab.Title = string.IsNullOrWhiteSpace(host) ? "Untitled" : host;
                    }
                    else
                    {
                        tab.Title = title;
                    }
                    MainWindow owner = GetTabOwner(tab) ?? this;
                    if (!IsBuiltInPage(tab.Url) && !string.IsNullOrWhiteSpace(tab.Url) && !string.IsNullOrWhiteSpace(title))
                    {
                        _dbService.UpdateTitle(tab.Url, title);
                    }
                    if (ReferenceEquals(owner.SelectedTab, tab)) owner.UpdateWindowTitle();
                };
                // Transparent lets the wallpaper show through the built-in new-tab page.
                // For real sites, use dark theme background (#222222) so searches/navigation NEVER flash blinding white.
                core.NavigationStarting += (s, e) =>
                {
                    // The built-in pages (new tab, downloads) are local files: showing a spinner / stop button / progress bar
                    // for them is what made a new tab look like it was "loading". Chrome and Firefox show nothing here either.
                    tab.IsLoading = !IsBuiltInPage(e.Uri);
                    bool targetIsNewTab = e.Uri != null && e.Uri.StartsWith(NewTabUrl, StringComparison.OrdinalIgnoreCase);
                    webView.DefaultBackgroundColor = targetIsNewTab
                        ? System.Drawing.Color.Transparent
                        : System.Drawing.Color.FromArgb(255, 34, 34, 34);
                    if (!targetIsNewTab && !string.IsNullOrEmpty(e.Uri))
                    {
                        tab.Favicon = GetFaviconSuggestionUrl(e.Uri);
                    }
                    MainWindow owner = GetTabOwner(tab) ?? this;
                    if (ReferenceEquals(owner.SelectedTab, tab)) owner.UpdateNavigationButtons();
                };
                core.SourceChanged += (s, e) =>
                {
                    tab.Url = core.Source;
                    if (!IsBuiltInPage(tab.Url) && !string.IsNullOrEmpty(tab.Url))
                    {
                        if (string.IsNullOrEmpty(tab.Favicon) || tab.Favicon == GetLocalFaviconFileUri())
                        {
                            tab.Favicon = GetFaviconSuggestionUrl(tab.Url);
                        }
                        try
                        {
                            _dbService.AddHistory(core.DocumentTitle, tab.Url, tab.Favicon ?? "");
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn("Failed to record history on SourceChanged", ex);
                        }
                    }
                    MainWindow owner = GetTabOwner(tab) ?? this;
                    // Don't overwrite what the user is typing if a redirect finishes mid-edit.
                    if (ReferenceEquals(owner.SelectedTab, tab))
                    {
                        owner.UpdateContentBackground();
                        if (!owner.TxtAddressBar.IsKeyboardFocused)
                        {
                            owner.TxtAddressBar.Text = GetAddressBarText(tab.Url);
                        }
                        owner.UpdateBookmarkVisualState();
                    }
                };
                core.FaviconChanged += async (s, e) =>
                {
                    try
                    {
                        string pageUrl = core.Source ?? string.Empty;
                        if (IsBuiltInPage(pageUrl))
                        {
                            tab.Favicon = GetLocalFaviconFileUri();
                            return;
                        }
                        // The page's own icon (SVG included) is stored on this computer; no third party is asked for it.
                        string? cachedIcon = await FaviconCache.UpdateFromPageAsync(core, pageUrl);
                        if (cachedIcon == null) return;
                        _dbService.UpdateFavicon(pageUrl, cachedIcon);
                        if (string.Equals(core.Source ?? string.Empty, pageUrl, StringComparison.Ordinal)) tab.Favicon = cachedIcon;
                    }
                    catch (Exception faviconEx)
                    {
                        AppLog.Debug("Could not update the tab icon", faviconEx);
                    }
                };
                core.HistoryChanged += (s, e) =>
                {
                    MainWindow owner = GetTabOwner(tab) ?? this;
                    if (ReferenceEquals(owner.SelectedTab, tab)) owner.UpdateNavigationButtons();
                };
                core.ContentLoading += (s, e) => tab.HasLoadedPage = true; // fires for real documents, never for a download hand-off
                core.NavigationCompleted += (s, e) =>
                {
                    tab.IsLoading = false;
                    if (tab.Url != NewTabUrl && (string.IsNullOrEmpty(tab.Favicon) || tab.Favicon == GetLocalFaviconFileUri()))
                    {
                        tab.Favicon = GetFaviconSuggestionUrl(tab.Url);
                    }
                    if (e.IsSuccess && !IsBuiltInPage(tab.Url) && !string.IsNullOrEmpty(core.Source))
                    {
                        try
                        {
                            MainWindow owner = GetTabOwner(tab) ?? this;
                            _dbService.AddHistory(core.DocumentTitle, core.Source, tab.Favicon ?? "");
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn("Failed to record history", ex);
                        }
                    }
                    MainWindow ownerTab = GetTabOwner(tab) ?? this;
                    if (ReferenceEquals(ownerTab.SelectedTab, tab))
                    {
                        ownerTab.UpdateNavigationButtons();
                        ownerTab.UpdateBookmarkVisualState();
                        _ = ownerTab.CaptureThumbnailLaterAsync(tab);
                    }
                };
                // Keyboard shortcuts must also work while the page has focus (WPF never sees those keys).
                CoreWebView2Controller? controller = GetController(webView);
                if (controller != null)
                {
                    controller.AcceleratorKeyPressed += (s, e) =>
                    {
                        if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown &&
                            e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown) return;
                        MainWindow owner = GetTabOwner(tab) ?? this;
                        Key key = KeyInterop.KeyFromVirtualKey((int)e.VirtualKey);
                        Action? action = owner.GetShortcutAction(key, Keyboard.Modifiers);
                        if (action == null) return;
                        e.Handled = true;
                        if (e.PhysicalKeyStatus.WasKeyDown != 0) return; // ignore auto-repeat (holding Ctrl+W / Ctrl+T)
                        owner.Dispatcher.BeginInvoke(action);
                    };
                }
                // Links with target=_blank / window.open would otherwise spawn a bare WebView2 popup window.
                // We handle this by opening the URL as a regular new tab. This avoids fragile CoreWebView2
                // handoff (NewWindow = core) which causes crashes during OAuth flows.
                core.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    // Popup blocker: a page can call window.open() in a loop and flood the browser with tabs.
                    // Only a real click / key press by the user may open a new tab.
                    if (!e.IsUserInitiated)
                    {
                        AppLog.Debug($"Blocked a popup that was not user-initiated: {e.Uri}");
                        return;
                    }
                    string popupUrl = string.IsNullOrWhiteSpace(e.Uri) ? string.Empty : e.Uri;
                    // Only web pages may open as a popup tab (a page must not reach file:, chrome-extension:, data: ... this way).
                    if (!Uri.TryCreate(popupUrl, UriKind.Absolute, out Uri? popupUri) ||
                        (popupUri.Scheme != Uri.UriSchemeHttp && popupUri.Scheme != Uri.UriSchemeHttps))
                    {
                        AppLog.Debug($"Blocked a popup with a non-web address: {popupUrl}");
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(popupUrl))
                    {
                        MainWindow owner = GetTabOwner(tab) ?? this;
                        owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                        {
                            _ = owner.OpenTabSafeAsync(popupUrl, tab);
                        }));
                    }
                };
                core.ProcessFailed += (s, e) =>
                {
                    // WebView2 renderer/browser process failed: log it. We intentionally do NOT close
                    // the tab here — WebView2 shows its own sad-tab UI and the user can reload.
                    AppLog.Warn($"WebView2 ProcessFailed: kind={e.ProcessFailedKind}, pid={e.ProcessDescription}");
                };
                core.ContainsFullScreenElementChanged += (s, e) =>
                {
                    MainWindow owner = GetTabOwner(tab) ?? this;
                    owner.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ReferenceEquals(owner.SelectedTab, tab)) owner.SetHtmlFullscreen(core.ContainsFullScreenElement);
                    }));
                };
                core.WindowCloseRequested += (s, e) =>
                {
                    MainWindow owner = GetTabOwner(tab) ?? this;
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        try
                        {
                            if (!owner.Tabs.Contains(tab) || _closedTabs.TryGetValue(tab, out _)) return;
                            // Only the LAST tab needs the flag ("open a blank tab" instead of closing the window).
                            // Setting it for other tabs left it stale, so the next normal close of the last tab
                            // opened a new tab instead of closing the window (user had to click X twice).
                            owner._scriptRequestedClose = owner.Tabs.Count == 1;
                            owner.CloseTab(tab);
                        }
                        catch (Exception closeEx)
                        {
                            owner._scriptRequestedClose = false;
                            AppLog.Warn("Error while closing tab requested by script", closeEx);
                        }
                    }));
                };
                core.WebMessageReceived += async (s, e) =>
                {
                    string? payload = null;
                    try { payload = e.TryGetWebMessageAsString(); } catch (Exception readEx) { AppLog.Debug("Web message was not a string", readEx); }
                    if (string.IsNullOrWhiteSpace(payload)) return;
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(payload);
                        JsonElement root = document.RootElement;
                        if (!root.TryGetProperty("type", out JsonElement typeElement)) return;
                        string? msgType = typeElement.GetString();
                        // Security: only the built-in new-tab page (https://local.browser) may use the bridge,
                        // except for page drag messages which can originate from any page being browsed.
                        bool bridgeTrusted = Uri.TryCreate(e.Source, UriKind.Absolute, out Uri? bridgeSender) &&
                            bridgeSender.Scheme == Uri.UriSchemeHttps &&
                            string.Equals(bridgeSender.Host, "local.browser", StringComparison.OrdinalIgnoreCase);
                        if (!bridgeTrusted && msgType != "page-drag-start" && msgType != "page-drag-end") return;
                        MainWindow owner = GetTabOwner(tab) ?? this;
                        // A page can send drag messages whenever it likes. Only the tab the user is looking at may do so.
                        if ((msgType == "page-drag-start" || msgType == "page-drag-end") && !ReferenceEquals(owner.SelectedTab, tab)) return;
                        if (msgType == "focus-address-bar")
                        {
                            string initialText = string.Empty;
                            if (root.TryGetProperty("text", out JsonElement textElement))
                            {
                                initialText = textElement.GetString() ?? string.Empty;
                            }
                            _ = owner.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                owner.TxtAddressBar.Focus();
                                if (!string.IsNullOrEmpty(initialText))
                                {
                                    owner.TxtAddressBar.Text = initialText;
                                    owner.TxtAddressBar.CaretIndex = owner.TxtAddressBar.Text.Length;
                                }
                                else
                                {
                                    owner.TxtAddressBar.SelectAll();
                                }
                            }));
                        }
                        // New-tab shortcuts are now user-managed (Chrome style) instead of auto-filled from history.
                        // Every reply is a "top-sites-data" message ({ id, title, url, iconUrl }[]) so the page's existing
                        // renderer keeps working; "shortcut-error" is sent when an add/edit is rejected.
                        else if (msgType == "get-top-sites" || msgType == "get-shortcuts")
                        {
                            core.PostWebMessageAsJson(BuildShortcutsMessage());
                        }
                        else if (msgType == "add-shortcut" || msgType == "update-shortcut" || msgType == "remove-shortcut")
                        {
                            string? scId = root.TryGetProperty("id", out JsonElement scIdEl) ? scIdEl.GetString() : null;
                            string? scTitle = root.TryGetProperty("title", out JsonElement scTitleEl) ? scTitleEl.GetString() : null;
                            string? scUrl = root.TryGetProperty("url", out JsonElement scUrlEl) ? scUrlEl.GetString() : null;

                            string? error = msgType switch
                            {
                                "add-shortcut" => AddShortcut(scTitle, scUrl),
                                "update-shortcut" => UpdateShortcut(scId, scTitle, scUrl),
                                _ => RemoveShortcut(scId)
                            };

                            if (error != null)
                            {
                                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "shortcut-error", message = error }, BridgeJsonOptions));
                            }
                            core.PostWebMessageAsJson(BuildShortcutsMessage());
                        }
                        else if (msgType == "get-downloads")
                        {
                            core.PostWebMessageAsJson(BuildDownloadsMessage());
                        }
                        else if (msgType is "open-download" or "show-download" or "remove-download" or "delete-download" or "pause-download" or "resume-download" or "restart-download" or "refresh-download-link" or "copy-download-link" or "copy-file-path" or "clear-downloads")
                        {
                            string? dlId = root.TryGetProperty("id", out JsonElement dlIdEl) ? dlIdEl.GetString() : null;
                            HandleDownloadAction(msgType, dlId, root);
                            core.PostWebMessageAsJson(BuildDownloadsMessage());
                        }
                        else if (msgType == "add-download")
                        {
                            string? addUrlText = root.TryGetProperty("url", out JsonElement addUrlEl) ? addUrlEl.GetString() : null;
                            string? addError = await AddDownloadFromLinkAsync(addUrlText, core);
                            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "add-download-result", ok = addError == null, message = addError }, BridgeJsonOptions));
                            core.PostWebMessageAsJson(BuildDownloadsMessage());
                        }
                        else if (msgType != null && LibraryMessageTypes.Contains(msgType))
                        {
                            // History / bookmarks / passwords page. Every destructive action is confirmed in a native dialog first.
                            await owner.HandleLibraryMessageAsync(core, msgType, root);
                        }
                        else if (msgType == "get-download-settings")
                        {
                            core.PostWebMessageAsJson(BuildDownloadSettingsMessage());
                        }
                        else if (msgType == "set-download-settings")
                        {
                            int dsMax = root.TryGetProperty("maxConcurrent", out JsonElement dsMaxEl) && dsMaxEl.TryGetInt32(out int dsMaxVal) ? dsMaxVal : DownloadSettings.MaxConcurrent;
                            long dsKb = root.TryGetProperty("speedLimitKBps", out JsonElement dsKbEl) && dsKbEl.TryGetInt64(out long dsKbVal) ? dsKbVal : DownloadSettings.SpeedLimitKBps;
                            DownloadSettings.Apply(dsMax, dsKb);
                            core.PostWebMessageAsJson(BuildDownloadSettingsMessage());
                            core.PostWebMessageAsJson(BuildDownloadsMessage());
                        }
                        else if (msgType == "open-downloads-folder")
                        {
                            OpenDownloadsFolder();
                        }
                        else if (msgType == "page-drag-start" &&
                                 root.TryGetProperty("text", out JsonElement startTextEl))
                        {
                            string dragText = startTextEl.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(dragText))
                            {
                                _ = owner.Dispatcher.BeginInvoke(new Action(() =>
                                    owner.TrackWebViewDragForTitleBarSearch(dragText.Trim())));
                            }
                        }
                        else if (msgType == "page-drag-end" &&
                                 root.TryGetProperty("text", out JsonElement endTextEl))
                        {
                            string dragText = endTextEl.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(dragText))
                            {
                                _ = owner.Dispatcher.BeginInvoke(new Action(() =>
                                    owner.HandleWebViewDragEnd(dragText.Trim())));
                            }
                        }
                    }
                    catch (JsonException jsonEx) { AppLog.Debug("Ignoring malformed web message", jsonEx); }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Suggestion bridge failed", ex);
                    }
                };
                await core.AddScriptToExecuteOnDocumentCreatedAsync(DragStartBridgeScript);
                _ = core.ExecuteScriptAsync(DragStartBridgeScript);
                core.DownloadStarting += CoreWebView2_DownloadStarting;
                CustomBrowser.Services.Streams.StreamSniffer.Attach(tab, core);
                // The new-tab / downloads pages need no extension, so start loading them right away instead of waiting
                // for extension start-up; this is the main reason a new tab used to sit empty for a moment.
                bool navigatedEarly = false;
                if (IsBuiltInPage(initialUrl) && Directory.Exists(assetFolder))
                {
                    core.Navigate(initialUrl);
                    navigatedEarly = true;
                }
                _extensionsTask ??= AutoLoadExtensionsAsync(core.Profile);
                try
                {
                    await _extensionsTask;
                }
                catch (Exception ex)
                {
                    // A broken extension must never make every new tab fail.
                    AppLog.Warn("Extension loading failed", ex);
                    _extensionsTask = null; // let the next tab try again instead of caching the failure
                }
                if (_closedTabs.TryGetValue(tab, out _)) return tab; // closed while initializing
                // Navigate to the initial URL (no popup handoff needed - popups open as plain new tabs).
                if (navigatedEarly)
                {
                    // already navigating
                }
                else if (initialUrl == NewTabUrl && !Directory.Exists(assetFolder))
                {
                    core.Navigate("about:blank"); // asset folder missing: don't show a "can't reach local.browser" error
                }
                else
                {
                    core.Navigate(initialUrl);
                }
            }
            catch (Exception) when (_closedTabs.TryGetValue(tab, out _))
            {
                // The tab was closed while its WebView was still starting up - nothing to report.
            }
            catch
            {
                // Don't leave a dead, blank tab behind if the WebView failed to initialize.
                CloseTab(tab);
                throw;
            }
            return tab;
        }
        /// <summary>
        /// Older Microsoft.Web.WebView2 packages don't expose WebView2.CoreWebView2Controller on the WPF control.
        /// Use the public property when it exists, otherwise read the controller from the control's private field.
        /// (Upgrading the NuGet package makes this fallback unnecessary.)
        /// </summary>
        private static CoreWebView2Controller? GetController(WebView2 webView)
        {
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var prop = typeof(WebView2).GetProperty("CoreWebView2Controller", all);
                if (prop?.GetValue(webView) is CoreWebView2Controller fromProp) return fromProp;
                foreach (var field in typeof(WebView2).GetFields(all))
                {
                    if (field.FieldType == typeof(CoreWebView2Controller) &&
                        field.GetValue(webView) is CoreWebView2Controller fromField)
                    {
                        return fromField;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not get CoreWebView2Controller", ex);
            }
            return null; // shortcuts still work while the address bar / toolbar has focus
        }


        private async Task OpenTabSafeAsync(string url, BrowserTab? opener = null, int insertIndex = -1)
        {
            try
            {
                await AddNewTabAsync(url, opener, insertIndex);
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to open new tab", ex);
                ThemedDialog.Show($"Failed to open new tab: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void ApplyWebViewVisibility(BrowserTab? active)
        {
            foreach (var child in TabContentGrid.Children.OfType<WebView2>())
            {
                child.Visibility = active != null && ReferenceEquals(child, active.WebView)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
        private void TabListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TabListBox.SelectedItem is not BrowserTab tab) return;
            if (_htmlFullscreen)
            {
                // Leaving the fullscreen tab: end fullscreen in the page and bring the browser chrome back.
                _ = _activeTab?.WebView?.CoreWebView2?.ExecuteScriptAsync("if (document.fullscreenElement) document.exitFullscreen();");
                SetHtmlFullscreen(false);
            }
            BrowserTab? leaving = _activeTab;
            _selectedTab = tab;
            _activeTab = tab;
            // Snapshot the tab we are leaving while it is still visible, so its hover card shows what it looked like
            // when you left it. The capture request is issued synchronously here, before the page is hidden below.
            if (leaving != null && !ReferenceEquals(leaving, tab) && Tabs.Contains(leaving)) _ = CaptureThumbnailAsync(leaving);
            // Swap the page first so the new tab appears immediately; the rest is cheap chrome updates.
            ApplyWebViewVisibility(tab);
            UpdateContentBackground();
            HideTabPreview();
            _suppressSuggestionRefresh = true;
            TxtAddressBar.Text = GetAddressBarText(tab.Url);
            _suppressSuggestionRefresh = false;
            UpdateNavigationButtons();
            UpdateWindowTitle();
            HideAddressSuggestions();
            ScrollTabIntoView(tab);
            if (!_tabDragActive) AnimateSelectedTab(tab);
            // The bookmark star needs a database lookup: run it after the switch has painted.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_selectedTab, tab)) UpdateBookmarkVisualState();
            }), DispatcherPriority.Background);
        }
        /// <summary>Solid dark fill behind the page area, so a resize (sidebar open/close) never shows the wallpaper through the gap. The new-tab page stays transparent on purpose.</summary>
        private void UpdateContentBackground()
        {
            bool newTab = string.Equals(SelectedTab?.Url, NewTabUrl, StringComparison.OrdinalIgnoreCase);
            TabContentGrid.Background = newTab ? Brushes.Transparent : (Brush)FindResource("MintSurfaceBrush");
        }

        /// <summary>Taskbar / Alt-Tab title follows the active page.</summary>
        private void UpdateWindowTitle()
        {
            string? pageTitle = SelectedTab?.Title;
            Title = string.IsNullOrWhiteSpace(pageTitle) ? "Mint Browser" : $"{pageTitle} - Mint Browser";
        }
        private void AnimateSelectedTab(BrowserTab tab)
        {
            // Do this from code-behind after layout instead of a ControlTemplate Storyboard.
            // WPF template storyboards cannot reliably resolve a Freezable transform by name here.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Tabs.Contains(tab)) return;
                if (TabListBox.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem item) return;
                ScaleTransform? scale = GetTabScaleTransform(item);
                if (scale == null) return;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 0.975;
                scale.ScaleY = 0.975;
                var ease = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                };
                var animX = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0.975,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(80),
                    EasingFunction = ease
                };
                var animY = animX.Clone();
                scale.BeginAnimation(
                    ScaleTransform.ScaleXProperty,
                    animX,
                    System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
                scale.BeginAnimation(
                    ScaleTransform.ScaleYProperty,
                    animY,
                    System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
            }), DispatcherPriority.Render);
        }
        // ------------------------------------------------------------------
        // Tab hover card + thumbnails
        // ------------------------------------------------------------------
        private readonly HashSet<BrowserTab> _capturingTabs = new HashSet<BrowserTab>();
        private async Task CaptureThumbnailAsync(BrowserTab? tab)
        {
            WebView2? webView = tab?.WebView;
            if (tab == null || webView == null || webView.Visibility != Visibility.Visible) return;
            if (!_capturingTabs.Add(tab)) return; // never overlap captures of the same tab
            try
            {
                // Read CoreWebView2 inside the try: it throws if the control was disposed meanwhile.
                CoreWebView2? core = webView.CoreWebView2;
                if (core == null) return;
                // The new-tab page has no background of its own (the wallpaper shows through), so a JPEG of it is solid
                // black. Capture it as PNG (keeps transparency) and lay it over the wallpaper instead.
                bool transparentPage = IsBuiltInPage(tab.Url);
                Point origin = webView.TranslatePoint(new Point(0, 0), this);
                Size size = new Size(webView.ActualWidth, webView.ActualHeight);
                using var stream = new MemoryStream();
                await core.CapturePreviewAsync(
                    transparentPage ? CoreWebView2CapturePreviewImageFormat.Png : CoreWebView2CapturePreviewImageFormat.Jpeg,
                    stream);
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (!transparentPage) bitmap.DecodePixelWidth = 460;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                BitmapSource result = transparentPage ? ComposeOverWallpaper(bitmap, origin, size) : bitmap;
                if (Tabs.Contains(tab)) _thumbnails[tab] = result;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Thumbnail capture failed", ex);
            }
            finally
            {
                _capturingTabs.Remove(tab);
            }
        }
        /// <summary>Draws the window background (wallpaper) cropped to the page area, then the page on top, at thumbnail size.</summary>
        private BitmapSource ComposeOverWallpaper(BitmapSource page, Point origin, Size size)
        {
            if (size.Width < 1 || size.Height < 1 || ActualWidth < 1 || ActualHeight < 1) return page;
            double scale = 460.0 / size.Width;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, size.Width, size.Height)));
                dc.PushTransform(new TranslateTransform(-origin.X, -origin.Y));
                if (Background != null) dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
                dc.Pop();
                dc.DrawImage(page, new Rect(0, 0, size.Width, size.Height));
                dc.Pop();
                dc.Pop();
            }
            var target = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Round(size.Width * scale)),
                Math.Max(1, (int)Math.Round(size.Height * scale)),
                96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            return target;
        }
        private async Task CaptureThumbnailLaterAsync(BrowserTab tab)
        {
            await Task.Delay(1500); // let the page paint first
            if (ReferenceEquals(SelectedTab, tab)) await CaptureThumbnailAsync(tab);
        }
        private void TabItem_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_tabDragActive) return; // no hover card while a tab is being dragged
            if (sender is not ListBoxItem item || item.DataContext is not BrowserTab tab) return;
            _previewTarget = item;
            _previewTab = tab;
            if (TabPreviewPopup.IsOpen) ShowTabPreview(); // sliding between tabs: switch instantly
            else _previewTimer.Start();                   // first hover: short delay like Firefox
        }
        private void TabItem_MouseLeave(object sender, MouseEventArgs e) => HideTabPreview();
        private void TabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e) => HideTabPreview();
        private void ShowTabPreview()
        {
            _previewTimer.Stop();
            if (_previewTab == null || _previewTarget == null || !Tabs.Contains(_previewTab)) return;
            PreviewTitle.Text = _previewTab.Title;
            string domain = "";
            if (!IsBuiltInPage(_previewTab.Url) && !string.IsNullOrEmpty(_previewTab.Url))
            {
                domain = Uri.TryCreate(_previewTab.Url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host)
                    ? (uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host)
                    : _previewTab.Url;
            }
            PreviewDomain.Text = domain;
            PreviewDomain.Visibility = domain.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            // Like Firefox: thumbnail only for background tabs.
            if (!ReferenceEquals(_previewTab, _selectedTab) && _thumbnails.TryGetValue(_previewTab, out BitmapSource? thumb))
            {
                PreviewImage.Source = thumb;
                PreviewImageHost.Visibility = Visibility.Visible;
            }
            else
            {
                PreviewImage.Source = null;
                PreviewImageHost.Visibility = Visibility.Collapsed;
            }
            // Position relative to the tab list; changing the offsets moves an open popup in place (no flicker).
            Point below = _previewTarget.TranslatePoint(new Point(0, _previewTarget.ActualHeight), TabListBox);
            TabPreviewPopup.PlacementTarget = TabListBox;
            TabPreviewPopup.HorizontalOffset = Math.Max(0, below.X);
            TabPreviewPopup.VerticalOffset = below.Y + 6;
            TabPreviewPopup.IsOpen = true;
        }
        private void HideTabPreview()
        {
            _previewTimer.Stop();
            TabPreviewPopup.IsOpen = false;
            _previewTab = null;
        }
    }
}


