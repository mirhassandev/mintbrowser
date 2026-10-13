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
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private static Task<CoreWebView2Environment>? _environmentTask;
        private static Task? _extensionsTask;
        private readonly DirectDownloadService _directDownloadService;
        private static readonly DatabaseService _dbService = DatabaseService.Instance;
        /// <summary>Called once from App.OnExit, after every window is closed.</summary>
        internal static void ShutdownServices() => _dbService.Dispose();
        private BrowserTab? _selectedTab;
        private BrowserTab? _activeTab; // the tab whose WebView is currently the visible one
        // Tab hover card
        private readonly Dictionary<BrowserTab, BitmapSource> _thumbnails = new Dictionary<BrowserTab, BitmapSource>();
        private readonly DispatcherTimer _previewTimer;
        private BrowserTab? _previewTab;
        private ListBoxItem? _previewTarget;
        private readonly DispatcherTimer _thumbnailTimer;
        private DateTime _allTabsClosedAt = DateTime.MinValue;
        private DateTime _downloadsPopupClosedAt = DateTime.MinValue;
        private const double DownloadsPopupWidth = 340; // keep in sync with the Border width in MainWindow.xaml
        private double _tabWidth = MaxTabWidth;
        private const string NewTabUrl = "https://local.browser/newtab.html";
        private const string DownloadsUrl = "https://local.browser/downloads.html";
        /// <summary>Pages that ship inside the app (served from the asset folder): no history, no site favicon, no bookmarking.</summary>
        private static bool IsBuiltInPage(string? url) =>
            !string.IsNullOrEmpty(url) && url.StartsWith("https://local.browser/", StringComparison.OrdinalIgnoreCase);
        private static bool IsDownloadsPage(string? url) =>
            !string.IsNullOrEmpty(url) && url.StartsWith(DownloadsUrl, StringComparison.OrdinalIgnoreCase);
        /// <summary>What the address bar shows for a tab: blank for the new-tab page, mint://downloads for the downloads page.</summary>
        private static string GetAddressBarText(string? url)
        {
            if (string.IsNullOrEmpty(url) || url == NewTabUrl) return string.Empty;
            if (IsLibraryPage(url)) return "mint://" + LibrarySectionOf(url);
            return IsDownloadsPage(url) ? "mint://downloads" : url;
        }
        private static bool IsDownloadsAlias(string input)
        {
            input = input.Trim().TrimEnd('/');
            return input.Equals("mint://downloads", StringComparison.OrdinalIgnoreCase) ||
                   input.Equals("chrome://downloads", StringComparison.OrdinalIgnoreCase) ||
                   input.Equals("edge://downloads", StringComparison.OrdinalIgnoreCase) ||
                   input.Equals("about:downloads", StringComparison.OrdinalIgnoreCase);
        }
        private const double MaxTabWidth = 200;
        private const double MinTabWidth = 76; // Firefox-like minimum; beyond this the strip scrolls
        // Tab dragging (reorder / tear-off / cross-window reattach)
        private const double TearOffDistance = 36; // how far the pointer must leave the tab strip to detach a tab
        private readonly string? _startUrl;
        private readonly BrowserTab? _transferredTab;
        private BrowserTab? _dragTab;
        private Point _dragStartPoint;
        private Point _dragPointerOffset;
        private bool _tabDragActive;
        private DispatcherTimer? _externalTabDragTimer;
        private MainWindow? _externalDraggedWindow;
        private MainWindow? _externalDropTarget;
        // WebView2 controls keep their event handlers when moved between windows. Routing those
        // handlers through the current owner keeps a torn-off tab fully functional after reattachment.
        private static readonly Dictionary<BrowserTab, MainWindow> _tabOwners = new();
        private static readonly HashSet<MainWindow> _openBrowserWindows = new();
        // Handlers for the shared static DatabaseService events. They are kept in fields so MainWindow_Closed can
        // unsubscribe them: an inline lambda cannot be removed, and the static service would keep every closed window alive.
        private Action? _historyChangedHandler;
        private Action? _bookmarksChangedHandler;
        // Window placement persistence key in user registry/settings
        private const string PlacementRegKey = @"SOFTWARE\MintBrowser\WindowPlacement";
        // Address bar / new-tab rich suggestions.
        private static readonly HttpClient _suggestionClient = CreateSuggestionClient();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapImage> _iconCache = new();
        private CancellationTokenSource? _suggestionCts;
        private bool _suppressSuggestionRefresh;
        private readonly List<string> _recentQueries = new();
        private string _suggestionsQuery = string.Empty;      // the query the visible suggestion list was built for
        private bool _suggestionArrowNavigated;               // user moved the highlight with Up/Down
        private string _arrowNavOriginalText = string.Empty;  // typed text saved before arrow-key navigation
        private static readonly JsonSerializerOptions BridgeJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase // the new-tab script reads title/url/iconUrl/...
        };
        // Tabs that were closed; lets a tab that is still initializing be discarded quietly.
        private static readonly ConditionalWeakTable<BrowserTab, object> _closedTabs = new();
        private static readonly object ClosedMarker = new();
        // Set true when the current CloseTab call was triggered by window.close() (WindowCloseRequested)
        // rather than by the user clicking the tab X button. Prevents quitting when a popup (e.g. OAuth) closes itself.
        private bool _scriptRequestedClose;
        // Ctrl+Shift+T: reopen recently closed tabs (per-window, capped at 10)
        private readonly Stack<string> _closedTabUrls = new();
        private const int MaxClosedTabHistory = 10;
        public ObservableCollection<AddressSuggestion> AddressSuggestions { get; } = new();

        // Injected on every page: detects drags of links, images, and selected text,
        // bridging them to C# so dropping on the title bar / tab strip can navigate or search.
        private static readonly string DragStartBridgeScript = @"
(() => {
    if (window.__mintDragBridgeInstalled) return;
    window.__mintDragBridgeInstalled = true;

    let currentPayload = '';

    document.addEventListener('dragstart', (ev) => {
        currentPayload = '';
        try {
            // 1. Check if dragging an anchor link
            let a = ev.target;
            while (a && a.tagName !== 'A' && a !== document.body) a = a.parentElement;
            if (a && a.href && a.href.length > 0) {
                currentPayload = a.href;
            }

            // 2. Check if dragging an image
            if (!currentPayload) {
                let img = ev.target;
                while (img && img.tagName !== 'IMG' && img !== document.body) img = img.parentElement;
                if (img && img.src && img.src.length > 0) {
                    currentPayload = img.src;
                }
            }

            // 3. Check selected text
            if (!currentPayload) {
                const text = window.getSelection()?.toString() || '';
                if (text && text.trim().length > 0) {
                    currentPayload = text.trim();
                }
            }

            // 4. Check dataTransfer fallback
            if (!currentPayload && ev.dataTransfer) {
                const uri = ev.dataTransfer.getData('text/uri-list');
                const plain = ev.dataTransfer.getData('text/plain');
                if (uri && uri.trim().length > 0) currentPayload = uri.trim();
                else if (plain && plain.trim().length > 0) currentPayload = plain.trim();
            }
        } catch (e) {}

        if (!currentPayload || !window.chrome?.webview) return;
        window.chrome.webview.postMessage(JSON.stringify({ type: 'page-drag-start', text: currentPayload }));
    }, { capture: true, passive: true });

    document.addEventListener('dragend', (ev) => {
        if (!currentPayload || !window.chrome?.webview) return;
        const payloadToSend = currentPayload;
        currentPayload = '';
        window.chrome.webview.postMessage(JSON.stringify({ type: 'page-drag-end', text: payloadToSend }));
    }, { capture: true, passive: true });
})();
";

        private static readonly string[] KnownSchemes =
        {
            "http", "https", "file", "about", "chrome", "edge", "chrome-extension", "view-source", "data"
        };
        public event PropertyChangedEventHandler? PropertyChanged;
        public ObservableCollection<BrowserTab> Tabs { get; } = new ObservableCollection<BrowserTab>();
        public sealed class SearchEngineInfo
        {
            public string Name { get; init; } = string.Empty;
            public string SearchUrl { get; init; } = string.Empty;
            public System.Windows.Media.ImageSource? Icon { get; init; }
            public string IconUrl { get; init; } = string.Empty;
        }
        public static readonly SearchEngineInfo EngineGoogle = new()
        {
            Name = "Google",
            SearchUrl = "https://www.google.com/search?q={0}",
            Icon = EngineIcons.Google
        };
        public static readonly SearchEngineInfo EngineBing = new()
        {
            Name = "Bing",
            SearchUrl = "https://www.bing.com/search?q={0}",
            Icon = EngineIcons.Bing
        };
        public static readonly SearchEngineInfo EngineDuckDuckGo = new()
        {
            Name = "DuckDuckGo",
            SearchUrl = "https://duckduckgo.com/?q={0}",
            Icon = EngineIcons.DuckDuckGo
        };
        public static readonly SearchEngineInfo EngineMDN = new()
        {
            Name = "MDN Web Docs",
            SearchUrl = "https://developer.mozilla.org/en-US/search?q={0}",
            Icon = EngineIcons.Mdn
        };
        public static readonly SearchEngineInfo EnginePerplexity = new()
        {
            Name = "Perplexity",
            SearchUrl = "https://www.perplexity.ai/search?q={0}",
            Icon = EngineIcons.Perplexity
        };
        public static readonly SearchEngineInfo EngineWikipedia = new()
        {
            Name = "Wikipedia (en)",
            SearchUrl = "https://en.wikipedia.org/wiki/Special:Search?search={0}",
            Icon = EngineIcons.Wikipedia
        };
        private SearchEngineInfo? _activeSearchEngine;
        public sealed class AddressSuggestion
        {
            public string Title { get; init; } = string.Empty;
            public string Subtitle { get; init; } = string.Empty;
            public string TrailingText { get; init; } = string.Empty;
            public string Url { get; init; } = string.Empty;
            public string InputText { get; init; } = string.Empty;
            public string IconUrl { get; init; } = string.Empty;
            public string IconGlyph { get; init; } = "\uE721";
            public bool HasIcon { get; init; }
            public bool IsNavigation { get; init; }
            public BrowserTab? Tab { get; init; } // set for "Open tab" rows so they switch instead of re-navigating
            // Pre-loaded BitmapImage for instant display (no lazy WPF HTTP download).
            public BitmapImage? CachedIcon { get; set; }
        }
        /// <summary>Width of every tab; recalculated so tabs shrink instead of overflowing the tab strip.</summary>
        public double TabWidth
        {
            get => _tabWidth;
            private set
            {
                if (Math.Abs(_tabWidth - value) < 0.5) return;
                _tabWidth = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabWidth)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCloseOnAllTabs)));
            }
        }
        /// <summary>Wide tabs always show their close button; narrow ones only on hover / when selected.</summary>
        public bool ShowCloseOnAllTabs => _tabWidth >= 120;
        public BrowserTab? SelectedTab
        {
            get => _selectedTab;
            set
            {
                // Ignore tabs that aren't (yet) in this window; the old "already selected" shortcut could leave
                // the ListBox without a selection (blank window) after a tab transfer.
                if (value != null && !Tabs.Contains(value)) return;
                _selectedTab = value;
                if (TabListBox != null && !ReferenceEquals(TabListBox.SelectedItem, value))
                {
                    TabListBox.SelectedItem = value;
                }
            }
        }
        public MainWindow() : this(null, null)
        {
        }
        /// <param name="startUrl">Page to open in the first tab.</param>
        public MainWindow(string? startUrl) : this(null, startUrl)
        {
        }
        // Used by tab tear-off so the same WebView2/Browsing state can travel to a new window.
        private MainWindow(BrowserTab? transferredTab) : this(transferredTab, null)
        {
        }
        private MainWindow(BrowserTab? transferredTab, string? startUrl)
        {
            _startUrl = startUrl;
            _transferredTab = transferredTab;
            InitializeComponent();
            DataContext = this;
            _openBrowserWindows.Add(this);
            if (transferredTab != null) _tabOwners[transferredTab] = this;
            _directDownloadService = DownloadStore.Service;
            Tabs.CollectionChanged += (_, _) => UpdateTabWidth();
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _previewTimer.Tick += (_, _) => ShowTabPreview();
            // Keep the active tab's thumbnail fresh so background tabs always have one ready.
            _thumbnailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _thumbnailTimer.Tick += (_, _) =>
            {
                if (IsActive && WindowState != WindowState.Minimized && !IsBuiltInPage(SelectedTab?.Url))
                {
                    _ = CaptureThumbnailAsync(SelectedTab);
                }
            };
            _thumbnailTimer.Start();
            _historyChangedHandler = () =>
            {
                if (!_openBrowserWindows.Contains(this)) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (SidebarPanel.Visibility == Visibility.Visible && _currentSidebarView == "History")
                    {
                        RefreshSidebarHistory(TxtSidebarSearch.Text.Trim());
                    }
                }), DispatcherPriority.Background);
            };
            _bookmarksChangedHandler = () =>
            {
                if (!_openBrowserWindows.Contains(this)) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (SidebarPanel.Visibility == Visibility.Visible && _currentSidebarView == "Bookmarks")
                    {
                        RefreshSidebarBookmarks(TxtSidebarSearch.Text.Trim());
                    }
                    UpdateBookmarkVisualState();
                }), DispatcherPriority.Background);
            };
            _dbService.OnHistoryChanged += _historyChangedHandler;
            _dbService.OnBookmarksChanged += _bookmarksChangedHandler;
            AllTabsPopup.Closed += (_, _) => _allTabsClosedAt = DateTime.UtcNow;
            DownloadsPopup.Closed += (_, _) => _downloadsPopupClosedAt = DateTime.UtcNow;
            DownloadsPopup.Opened += (_, _) => RefreshDownloadStatusTexts();
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            PreviewMouseMove += MainWindow_PreviewMouseMove;
            PreviewMouseLeftButtonUp += MainWindow_PreviewMouseLeftButtonUp;
            TabBarBorder.MouseLeftButtonDown += TabBarBorder_MouseLeftButtonDown;
            LostMouseCapture += MainWindow_LostMouseCapture;
            // Popups are top-most windows: close them when the browser loses focus or moves, otherwise they
            // float over other apps / stay behind at the old position.
            Deactivated += (_, _) => { HideTabPreview(); HideAddressSuggestions(); };
            LocationChanged += (_, _) => { HideTabPreview(); HideAddressSuggestions(); DownloadsPopup.IsOpen = false; };
            SetWindowIcon();
            Loaded += MainWindow_Loaded;
            StateChanged += MainWindow_StateChanged;
            SizeChanged += (_, _) => UpdateChromeTint();
            TxtAddressBar.TextChanged += (_, _) => UpdateAddressBarDisplay();
            TxtAddressBar.IsKeyboardFocusWithinChanged += (_, _) => UpdateAddressBarDisplay();
            Closed += MainWindow_Closed;
            InitStreams();
            Closing += (_, _) => { if (_transferredTab == null) SaveWindowPlacement(); }; // handle still alive here, so RestoreBounds is accurate
        }
        // ------------------------------------------------------------------
        // Paths / assets
        // ------------------------------------------------------------------
        private static string GetUserDataFolder()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CustomBrowser"
            );
            Directory.CreateDirectory(folder);
            return folder;
        }
        /// <summary>Finds the "asset" folder next to the exe, or in the working directory as a fallback.</summary>
        private static string GetAssetFolder()
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "asset");
            if (!Directory.Exists(folder))
            {
                folder = Path.Combine(Directory.GetCurrentDirectory(), "asset");
            }
            return folder;
        }
        private static string GetLocalFaviconFileUri()
        {
            string iconPath = Path.Combine(GetAssetFolder(), "favicon.ico");
            return new Uri(Path.GetFullPath(iconPath)).AbsoluteUri;
        }
        private void SetWindowIcon()
        {
            try
            {
                string iconPath = Path.Combine(GetAssetFolder(), "favicon.ico");
                if (File.Exists(iconPath))
                {
                    Icon = new BitmapImage(new Uri(Path.GetFullPath(iconPath), UriKind.Absolute));
                }
            }
            catch (Exception iconEx)
            {
                AppLog.Debug("Could not set the window icon", iconEx);
            }
        }
        // ------------------------------------------------------------------
        // Window lifecycle / chrome
        // ------------------------------------------------------------------
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_transferredTab == null) RestoreWindowPlacement(); // only the main window follows the saved placement; a torn-off tab keeps the spot where it was dropped
                LoadSavedWallpaper();
                if (_transferredTab != null)
                {
                    ReceiveTransferredTab(_transferredTab, -1, animate: false);
                }
                else
                {
                    await AddNewTabAsync(_startUrl ?? NewTabUrl);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Startup failed", ex);
                ThemedDialog.Show($"Startup Error: {ex.Message}\n\nDetails were saved to the log file:\n{AppLog.LogFilePath}", "Browser Initialization Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Saves window size, position and state to the Windows registry so they survive restarts.</summary>
        private void SaveWindowPlacement()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(PlacementRegKey);
                if (key == null) return;
                // Save the normal (restored) bounds so we don't save the maximized screen coords
                var bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                key.SetValue("Left",   (int)bounds.Left);
                key.SetValue("Top",    (int)bounds.Top);
                key.SetValue("Width",  (int)bounds.Width);
                key.SetValue("Height", (int)bounds.Height);
                key.SetValue("State",  (int)(WindowState == WindowState.Minimized ? WindowState.Normal : WindowState));
            }
            catch (Exception ex)
            {
                AppLog.Debug("SaveWindowPlacement failed", ex);
            }
        }

        /// <summary>Restores window size, position and state saved by SaveWindowPlacement.</summary>
        private void RestoreWindowPlacement()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PlacementRegKey);
                if (key == null) return; // first run — keep the default Maximized from XAML

                int left   = (int)(key.GetValue("Left")   ?? 0);
                int top    = (int)(key.GetValue("Top")    ?? 0);
                int width  = (int)(key.GetValue("Width")  ?? 1300);
                int height = (int)(key.GetValue("Height") ?? 850);
                int state  = (int)(key.GetValue("State")  ?? (int)WindowState.Maximized);

                // Validate that the saved bounds overlap the virtual screen (all monitors combined)
                double vLeft   = SystemParameters.VirtualScreenLeft;
                double vTop    = SystemParameters.VirtualScreenTop;
                double vRight  = vLeft + SystemParameters.VirtualScreenWidth;
                double vBottom = vTop  + SystemParameters.VirtualScreenHeight;
                bool onScreen  = left < vRight - 100 && (left + width) > vLeft + 100 &&
                                 top  < vBottom - 50 && top >= vTop - 10;

                if (onScreen)
                {
                    WindowState = WindowState.Normal; // un-maximize first so the window can move to the saved monitor
                    Left   = left;
                    Top    = top;
                    Width  = Math.Max(width,  (int)MinWidth);
                    Height = Math.Max(height, (int)MinHeight);
                }
                WindowState = (WindowState)state;
            }
            catch (Exception ex)
            {
                AppLog.Debug("RestoreWindowPlacement failed", ex);
                WindowState = WindowState.Maximized; // safe fallback
            }
        }
        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            HideTabPreview();
            HideAddressSuggestions();
            // A borderless WindowChrome window overflows the screen by the resize-border size when
            // maximized (tab bar / caption buttons get clipped). Pad the content to compensate.
            if (WindowState == WindowState.Maximized)
            {
                RootGrid.Margin = new Thickness(0); // exact work-area fit comes from WM_GETMINMAXINFO
                BtnMaximize.Content = "\uE923"; // Restore glyph
                BtnMaximize.ToolTip = "Restore Down";
            }
            else
            {
                RootGrid.Margin = new Thickness(0);
                BtnMaximize.Content = "\uE922"; // Maximize glyph
                BtnMaximize.ToolTip = "Maximize";
            }
        }
        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _thumbnailTimer.Stop();
            _previewTimer.Stop();
            _suggestionCts?.Cancel();
            _externalTabDragTimer?.Stop();
            HideDropTargetIndicator();
            // The shared DatabaseService is NOT disposed here: it is disposed once in App.OnExit,
            // so a window closing (or a tab being moved between windows) can never close the DB under another window.
            _openBrowserWindows.Remove(this);
            // Unsubscribe from the shared static DatabaseService so this closed window can be garbage collected.
            _dbService.OnHistoryChanged -= _historyChangedHandler;
            _dbService.OnBookmarksChanged -= _bookmarksChangedHandler;
            foreach (var tab in Tabs.ToList())
            {
                _tabOwners.Remove(tab);
                try { tab.WebView?.Dispose(); } catch (Exception disposeEx) { AppLog.Debug("WebView dispose while closing window", disposeEx); }
            }
        }
        // ------------------------------------------------------------------
        // Maximize fits the usable screen area (not over the taskbar); HTML fullscreen uses the whole monitor
        // ------------------------------------------------------------------
        private bool _htmlFullscreen;
        private WindowState _stateBeforeFullscreen = WindowState.Normal;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public Win32Point ptReserved;
            public Win32Point ptMaxSize;
            public Win32Point ptMaxPosition;
            public Win32Point ptMinTrackSize;
            public Win32Point ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfoNative
        {
            public int cbSize;
            public Win32Rect rcMonitor;
            public Win32Rect rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoNative lpmi);

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (_htmlFullscreen) Topmost = true;
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            if (_htmlFullscreen) Topmost = false;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource? source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowMessageHook);
            if (source != null) InstallTabHitTestOverride(source.Handle);

            // Windows 11: rounded window corners and a dark frame. On Windows 10 these calls just return an error code.
            IntPtr dwmHandle = new WindowInteropHelper(this).Handle;
            int dwmDark = 1, dwmRound = 2; // 2 = DWMWCP_ROUND
            _ = DwmSetWindowAttribute(dwmHandle, 20, ref dwmDark, sizeof(int));
            _ = DwmSetWindowAttribute(dwmHandle, 33, ref dwmRound, sizeof(int));
        }

        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int GWLP_WNDPROC = -4;

        private delegate IntPtr NativeWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private NativeWndProc? _tabHitProc;      // kept in a field so the GC can't collect the native callback
        private IntPtr _previousWndProc;

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndProc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Subclasses the window so it sees WM_NCHITTEST before WPF's WindowChrome (hook order is not reliable enough).
        /// The full strip height over a tab, the window's top edge included, answers HTCLIENT: it is the tab, never
        /// title bar or resize border.
        /// </summary>
        private void InstallTabHitTestOverride(IntPtr hwnd)
        {
            try
            {
                _tabHitProc = (h, m, w, l) =>
                    m == WM_NCHITTEST && IsScreenPointOverTab(l)
                        ? new IntPtr(HTCLIENT)
                        : CallWindowProc(_previousWndProc, h, m, w, l);
                _previousWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_tabHitProc));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not install the tab hit-test override", ex);
            }
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                ApplyMaximizeBounds(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        /// <summary>Finds the tab whose full-height column contains x (TabListBox coordinates).</summary>
        private bool TryGetTabAtX(double x, out BrowserTab? tab, out double left)
        {
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (TabListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item || item.ActualWidth <= 0) continue;
                double l = GetTabLayoutLeft(item);
                if (x >= l && x < l + item.ActualWidth)
                {
                    tab = Tabs[i];
                    left = l;
                    return true;
                }
            }
            tab = null;
            left = 0;
            return false;
        }

        // Fallback: if a press in the tab column lands on the bare strip (not on a tab element), still treat it as a tab press.
        // Bubbling event, so it only runs when the tab handler did not already take the press.
        private void TabBarBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled || _dragTab != null) return;
            if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
            Point p = e.GetPosition(TabListBox);
            if (p.Y < -12 || p.Y > TabListBox.ActualHeight + 2) return;
            if (!TryGetTabAtX(p.X, out BrowserTab? tab, out double left) || tab == null) return;
            _dragTab = tab;
            _dragStartPoint = e.GetPosition(this);
            _dragPointerOffset = new Point(p.X - left, Math.Max(0, p.Y));
            _tabDragActive = false;
            e.Handled = true;
            SelectedTab = tab;
        }

        /// <summary>True when the screen point packed in a WM_NCHITTEST lParam is inside a tab's full-height column.</summary>
        private bool IsScreenPointOverTab(IntPtr lParam)
        {
            try
            {
                if (!TabListBox.IsVisible || TabListBox.ActualHeight <= 0) return false;
                long l = lParam.ToInt64();
                int x = unchecked((short)(l & 0xFFFF));
                int y = unchecked((short)((l >> 16) & 0xFFFF));
                Point p = TabListBox.PointFromScreen(new Point(x, y)); // device pixels in, DIPs out
                if (p.Y < -12 || p.Y > TabListBox.ActualHeight + 2) return false;
                if (TryGetTabAtX(p.X, out _, out _)) return true;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Tab hit-test failed", ex);
            }
            return false;
        }

        private void ApplyMaximizeBounds(IntPtr hwnd, IntPtr lParam)
        {
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return;

            var info = new MonitorInfoNative { cbSize = Marshal.SizeOf<MonitorInfoNative>() };
            if (!GetMonitorInfo(monitor, ref info)) return;

            MinMaxInfo mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            Win32Rect area = _htmlFullscreen ? info.rcMonitor : info.rcWork;
            mmi.ptMaxPosition.X = area.Left - info.rcMonitor.Left;
            mmi.ptMaxPosition.Y = area.Top - info.rcMonitor.Top;
            mmi.ptMaxSize.X = area.Right - area.Left;
            mmi.ptMaxSize.Y = area.Bottom - area.Top;
            mmi.ptMaxTrackSize.X = area.Right - area.Left;
            mmi.ptMaxTrackSize.Y = area.Bottom - area.Top;

            if (!_htmlFullscreen)
            {
                double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * scale);
                mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * scale);
            }
            else
            {
                mmi.ptMinTrackSize.X = 0;
                mmi.ptMinTrackSize.Y = 0;
            }

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        [ComImport, Guid("602D4995-B13A-429b-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList2
        {
            [PreserveSig] int HrInit();
            [PreserveSig] int AddTab(IntPtr hwnd);
            [PreserveSig] int DeleteTab(IntPtr hwnd);
            [PreserveSig] int ActivateTab(IntPtr hwnd);
            [PreserveSig] int SetActiveAlt(IntPtr hwnd);
            [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        private class TaskbarListCom { }

        private System.Windows.Shell.WindowChrome? _savedChrome;
        private Rect _boundsBeforeFullscreen;
        private ResizeMode _resizeModeBeforeFullscreen = ResizeMode.CanResize;

        /// <summary>Esc while a page is fullscreen: end fullscreen in the page and restore the browser UI.</summary>
        private void ExitHtmlFullscreen()
        {
            AppLog.Info("Esc pressed: leaving HTML fullscreen");
            _ = SelectedTab?.WebView?.CoreWebView2?.ExecuteScriptAsync("if (document.fullscreenElement) document.exitFullscreen();");
            SetHtmlFullscreen(false);
        }

        /// <summary>Video/page fullscreen (YouTube fullscreen button etc.): hide the browser chrome and cover the whole monitor.</summary>
        private void SetHtmlFullscreen(bool on)
        {
            if (on == _htmlFullscreen) return;
            HideTabPreview();
            HideAddressSuggestions();
            _htmlFullscreen = on;
            AppLog.Info($"HTML fullscreen {(on ? "ON" : "OFF")} (state={WindowState}, topmost={Topmost})");

            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            if (on)
            {
                _stateBeforeFullscreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
                _boundsBeforeFullscreen = new Rect(Left, Top, Width, Height);
                _resizeModeBeforeFullscreen = ResizeMode;

                // Hide tabs and address bar
                RootGrid.RowDefinitions[0].Height = new GridLength(0);
                RootGrid.RowDefinitions[1].Height = new GridLength(0);

                // Save and remove WindowChrome so it doesn't restrict or margin the window
                _savedChrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);

                // Fullscreen in Windows is borderless Normal state (NOT WS_MAXIMIZE, which reserves taskbar)
                ResizeMode = ResizeMode.NoResize;
                if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                }

                if (hwnd != IntPtr.Zero)
                {
                    IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                    if (monitor != IntPtr.Zero)
                    {
                        var info = new MonitorInfoNative { cbSize = Marshal.SizeOf<MonitorInfoNative>() };
                        if (GetMonitorInfo(monitor, ref info))
                        {
                            Win32Rect r = info.rcMonitor;

                            // DPI convert to WPF logical coordinates
                            var source = HwndSource.FromHwnd(hwnd);
                            double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                            double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                            Left = r.Left / dpiX;
                            Top = r.Top / dpiY;
                            Width = (r.Right - r.Left) / dpiX;
                            Height = (r.Bottom - r.Top) / dpiY;

                            // Explicitly place Win32 window at Topmost covering the entire monitor
                            SetWindowPos(hwnd, new IntPtr(-1), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0040 | 0x0020);
                        }
                    }

                    // Tell Windows Explorer Taskbar to drop below this fullscreen window
                    try
                    {
                        var taskbar = (ITaskbarList2)new TaskbarListCom();
                        taskbar.HrInit();
                        taskbar.MarkFullscreenWindow(hwnd, true);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("MarkFullscreenWindow failed", ex);
                    }
                }

                Topmost = true;
            }
            else
            {
                Topmost = false;

                if (hwnd != IntPtr.Zero)
                {
                    // Unmark fullscreen with shell
                    try
                    {
                        var taskbar = (ITaskbarList2)new TaskbarListCom();
                        taskbar.HrInit();
                        taskbar.MarkFullscreenWindow(hwnd, false);
                    }
                    catch (Exception taskbarEx) { AppLog.Debug("Could not unmark the fullscreen window with the shell taskbar", taskbarEx); }

                    // Remove TOPMOST
                    SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0020 | 0x0010);
                }

                // Restore tabs and address bar heights
                RootGrid.RowDefinitions[0].Height = new GridLength(44);
                RootGrid.RowDefinitions[1].Height = new GridLength(40);

                // Restore WindowChrome
                if (_savedChrome != null)
                {
                    System.Windows.Shell.WindowChrome.SetWindowChrome(this, _savedChrome);
                }
                else
                {
                    System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
                    {
                        CaptionHeight = 44,
                        CornerRadius = new CornerRadius(0),
                        GlassFrameThickness = new Thickness(0),
                        ResizeBorderThickness = new Thickness(6)
                    });
                }

                ResizeMode = _resizeModeBeforeFullscreen;

                if (_stateBeforeFullscreen == WindowState.Maximized)
                {
                    WindowState = WindowState.Maximized;
                }
                else
                {
                    WindowState = WindowState.Normal;
                    if (_boundsBeforeFullscreen.Width > 0 && _boundsBeforeFullscreen.Height > 0)
                    {
                        Left = _boundsBeforeFullscreen.Left;
                        Top = _boundsBeforeFullscreen.Top;
                        Width = _boundsBeforeFullscreen.Width;
                        Height = _boundsBeforeFullscreen.Height;
                    }
                }
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
        // ------------------------------------------------------------------
        // Wallpaper
        // ------------------------------------------------------------------
        private void LoadSavedWallpaper()
        {
            try
            {
                string savedPath = Path.Combine(GetUserDataFolder(), "wallpaper.jpg");
                if (File.Exists(savedPath))
                {
                    ApplyWallpaper(LoadBitmap(savedPath));
                }
            }
            catch (Exception wallpaperEx)
            {
                AppLog.Debug("Could not load the saved wallpaper - using the default background", wallpaperEx);
            }
        }
        private static BitmapImage LoadBitmap(string imagePath)
        {
            // A multi-megapixel wallpaper decoded at full size is re-uploaded and re-scaled on every window resize
            // (maximize / restore feels sluggish). Decode it once at roughly screen size instead.
            int decodeLongEdge = 0;
            try
            {
                using FileStream probe = File.OpenRead(imagePath);
                BitmapFrame frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                int target = (int)Math.Clamp(Math.Max(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight) * 1.5, 1920, 3840);
                if (Math.Max(frame.PixelWidth, frame.PixelHeight) > target) decodeLongEdge = target;
                decodeLongEdge = frame.PixelWidth >= frame.PixelHeight ? decodeLongEdge : -decodeLongEdge;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not read the wallpaper size - decoding at full size", ex);
            }
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            if (decodeLongEdge > 0) bitmap.DecodePixelWidth = decodeLongEdge;
            else if (decodeLongEdge < 0) bitmap.DecodePixelHeight = -decodeLongEdge;
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // releases the file handle
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        private void ApplyWallpaper(BitmapImage bitmap)
        {
            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            brush.Freeze();
            // Painted on the window itself (not RootGrid) so it sits behind the transparent tab bar and
            // toolbar too, and still fills the window when RootGrid gets its maximized padding.
            Background = brush;
            BuildWallpaperLuma(bitmap);
            UpdateChromeTint();
        }

        // ------------------------------------------------------------------
        // Firefox-style address text: domain bright, scheme and path muted (only while the box is not being edited)
        // ------------------------------------------------------------------
        private static readonly char[] UrlPathStartChars = { '/', '?', '#' };

        private void UpdateAddressBarDisplay()
        {
            string text = TxtAddressBar.Text;
            if (TxtAddressBar.IsKeyboardFocusWithin || string.IsNullOrEmpty(text))
            {
                AddressBarDisplay.Visibility = Visibility.Collapsed;
                AddressBarDisplay.Inlines.Clear();
                TxtAddressBar.ClearValue(Control.ForegroundProperty); // back to the style's colour for editing
                return;
            }

            var bright = (Brush)FindResource("MintTextBrush");
            var muted = (Brush)FindResource("MintTextMutedBrush");

            // scheme://host[:port]/path?query -> only the host part is bright. Anything that is not a URL (a typed search) stays bright.
            int hostStart = 0, hostEnd = text.Length;
            int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd > 0 && schemeEnd <= 10 && text.IndexOf(' ') < 0)
            {
                hostStart = schemeEnd + 3;
                int pathStart = text.IndexOfAny(UrlPathStartChars, hostStart);
                hostEnd = pathStart < 0 ? text.Length : pathStart;
            }

            AddressBarDisplay.Inlines.Clear();
            if (hostStart > 0) AddressBarDisplay.Inlines.Add(new System.Windows.Documents.Run(text.Substring(0, hostStart)) { Foreground = muted });
            AddressBarDisplay.Inlines.Add(new System.Windows.Documents.Run(text.Substring(hostStart, hostEnd - hostStart)) { Foreground = bright });
            if (hostEnd < text.Length) AddressBarDisplay.Inlines.Add(new System.Windows.Documents.Run(text.Substring(hostEnd)) { Foreground = muted });

            AddressBarDisplay.Visibility = Visibility.Visible;
            TxtAddressBar.Foreground = Brushes.Transparent; // the overlay draws the text; keep the TextBox's own copy invisible
        }

        // ------------------------------------------------------------------
        // Adaptive chrome: darken the tab bar / toolbar scrim on bright wallpapers so the icons and text stay visible
        // ------------------------------------------------------------------
        private const int LumaGridWidth = 160;
        private float[]? _lumaGrid;
        private int _lumaW, _lumaH;
        private double _wallpaperAspect = 1;
        private byte _tintTop = 0x59, _tintBottom = 0x1A;

        /// <summary>Samples the wallpaper into a small brightness grid (0 = black, 1 = white).</summary>
        private void BuildWallpaperLuma(BitmapSource source)
        {
            try
            {
                double scale = Math.Min(1.0, LumaGridWidth / (double)source.PixelWidth);
                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                int w = converted.PixelWidth, h = converted.PixelHeight;
                int stride = w * 4;
                var pixels = new byte[stride * h];
                converted.CopyPixels(pixels, stride, 0);
                var grid = new float[w * h];
                for (int i = 0; i < grid.Length; i++)
                {
                    int p = i * 4; // B, G, R, A
                    grid[i] = (0.299f * pixels[p + 2] + 0.587f * pixels[p + 1] + 0.114f * pixels[p]) / 255f;
                }
                _lumaGrid = grid;
                _lumaW = w;
                _lumaH = h;
                _wallpaperAspect = source.PixelWidth / (double)Math.Max(1, source.PixelHeight);
            }
            catch (Exception ex)
            {
                _lumaGrid = null;
                AppLog.Debug("Could not analyse the wallpaper brightness", ex);
            }
        }

        /// <summary>Brightness of the part of the wallpaper that sits behind the tab bar + toolbar (85th percentile, so bright patches count).</summary>
        private double ChromeStripLuma()
        {
            if (_lumaGrid == null || ActualWidth < 1 || ActualHeight < 1) return 0;
            // Wallpaper is UniformToFill + centred: work out which part of the image is actually visible.
            double winAspect = ActualWidth / ActualHeight;
            double x0 = 0, x1 = 1, y0 = 0, visibleH = 1;
            if (_wallpaperAspect > winAspect)
            {
                double visibleW = winAspect / _wallpaperAspect;
                x0 = (1 - visibleW) / 2;
                x1 = x0 + visibleW;
            }
            else
            {
                visibleH = _wallpaperAspect / winAspect;
                y0 = (1 - visibleH) / 2;
            }
            double stripFraction = Math.Min(1.0, (RootGrid.RowDefinitions[0].ActualHeight + RootGrid.RowDefinitions[1].ActualHeight) / ActualHeight);
            if (stripFraction <= 0) stripFraction = Math.Min(1.0, 84.0 / ActualHeight);
            int gx0 = Math.Clamp((int)(x0 * _lumaW), 0, _lumaW - 1);
            int gx1 = Math.Clamp((int)Math.Ceiling(x1 * _lumaW), gx0 + 1, _lumaW);
            int gy0 = Math.Clamp((int)(y0 * _lumaH), 0, _lumaH - 1);
            int gy1 = Math.Clamp((int)Math.Ceiling((y0 + visibleH * stripFraction) * _lumaH), gy0 + 1, _lumaH);

            var histogram = new int[64];
            int count = 0;
            for (int y = gy0; y < gy1; y++)
            {
                for (int x = gx0; x < gx1; x++)
                {
                    histogram[Math.Min(63, (int)(_lumaGrid[y * _lumaW + x] * 64))]++;
                    count++;
                }
            }
            if (count == 0) return 0;
            int target = (int)(count * 0.85), seen = 0;
            for (int bin = 0; bin < 64; bin++)
            {
                seen += histogram[bin];
                if (seen > target) return (bin + 0.5) / 64.0;
            }
            return 1;
        }

        /// <summary>Dark wallpaper: the original light scrim. Bright wallpaper: up to ~80% black behind the chrome.</summary>
        private void UpdateChromeTint()
        {
            if (_lumaGrid == null) return;
            double luma = ChromeStripLuma();
            double t = Math.Clamp((luma - 0.35) / 0.40, 0, 1);
            byte top = (byte)Math.Round(0x59 + (0xD0 - 0x59) * t);
            byte bottom = (byte)Math.Round(0x1A + (0xA8 - 0x1A) * t);
            if (Math.Abs(top - _tintTop) < 3 && Math.Abs(bottom - _tintBottom) < 3) return; // avoid re-rendering for tiny changes while resizing
            _tintTop = top;
            _tintBottom = bottom;
            var brush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(top, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(bottom, 0, 0, 0), 1)
                },
                new Point(0, 0), new Point(0, 1));
            brush.Freeze();
            Resources["ChromeTintBrush"] = brush;
        }
        private void BtnWallpaper_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Custom Browser Wallpaper",
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                // Decode FIRST: if the image is invalid we don't overwrite the previously saved wallpaper.
                BitmapImage bitmap = LoadBitmap(dialog.FileName);
                string destPath = Path.Combine(GetUserDataFolder(), "wallpaper.jpg");
                File.Copy(dialog.FileName, destPath, overwrite: true);
                ApplyWallpaper(bitmap);
            }
            catch (Exception ex)
            {
                ThemedDialog.Show($"Could not set wallpaper: {ex.Message}", "Wallpaper Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        // ------------------------------------------------------------------
        // WebView2 environment / extensions
        // ------------------------------------------------------------------
        private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            // NOTE: --ignore-certificate-errors was removed: it disables TLS validation for every site.
            string browserArgs = "--enable-gpu-rasterization --enable-zero-copy";
            var options = new CoreWebView2EnvironmentOptions(browserArgs)
            {
                AreBrowserExtensionsEnabled = true
            };
            string userDataFolder = Path.Combine(GetUserDataFolder(), "WebView2Data");
            Directory.CreateDirectory(userDataFolder);
            return await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
        }
        /// <summary>Creates the environment once; concurrent callers share the same task.</summary>
        private static async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            _environmentTask ??= CreateEnvironmentAsync();
            try
            {
                return await _environmentTask;
            }
            catch
            {
                _environmentTask = null; // allow a retry instead of caching the failure forever
                throw;
            }
        }
        /// <summary>Folders scanned for unpacked extensions (each extension = a sub-folder with a manifest.json).</summary>
        private static readonly string BundledExtensionsRoot = Path.Combine(AppContext.BaseDirectory, "Extensions");
        private static IEnumerable<string> GetExtensionRoots()
        {
            // Shipped with the app: copied next to the exe by the .csproj ("Extensions" folder).
            yield return BundledExtensionsRoot;
            // User-installed extensions.
            yield return Path.Combine(GetUserDataFolder(), "Extensions");
        }
        private static async Task AutoLoadExtensionsAsync(CoreWebView2Profile profile)
        {
            foreach (string root in GetExtensionRoots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (!File.Exists(Path.Combine(dir, "manifest.json"))) continue;
                    try
                    {
                        var ext = await profile.AddBrowserExtensionAsync(dir);
                        AppLog.Info($"Extension loaded: {ext.Name} ({dir})");
                        if (string.Equals(root, BundledExtensionsRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            await RemoveStaleDuplicatesAsync(profile, ext);
                        }
                    }
                    catch (Exception ex)
                    {
                        // One bad / already-installed extension must not stop the others from loading.
                        AppLog.Error($"Extension load failed for '{dir}'", ex);
                    }
                }
            }
        }
        /// <summary>
        /// An unpacked extension's ID depends on its folder path. If the same extension was loaded earlier
        /// from a different folder (e.g. via the "select manifest.json" dialog) it would run twice, so the
        /// older same-named copy is removed.
        /// </summary>
        private static async Task RemoveStaleDuplicatesAsync(CoreWebView2Profile profile, CoreWebView2BrowserExtension keep)
        {
            try
            {
                foreach (var other in await profile.GetBrowserExtensionsAsync())
                {
                    if (other.Id == keep.Id) continue;
                    if (!string.Equals(other.Name, keep.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    await other.RemoveAsync();
                    AppLog.Info($"Removed duplicate extension '{other.Name}' ({other.Id}) left over from an older folder.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not remove duplicate extensions", ex);
            }
        }
        // ------------------------------------------------------------------
        // Keyboard shortcuts
        // ------------------------------------------------------------------
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            Action? action = GetShortcutAction(key, Keyboard.Modifiers);
            if (action == null) return;
            e.Handled = true;
            if (e.IsRepeat) return; // holding Ctrl+T / Ctrl+W must not open or close a pile of tabs
            action();
        }
        private Action? GetShortcutAction(Key key, ModifierKeys mods)
        {
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;
            if (ctrl && !alt && !shift && key == Key.T) return () => { _ = NewTabAndFocusAsync(); };
            if (ctrl && !alt && shift && key == Key.T) return () => ReopenLastClosedTab();
            if (ctrl && !alt && !shift && key == Key.W) return () => { if (SelectedTab != null) CloseTab(SelectedTab); };
            if (ctrl && !alt && !shift && key == Key.D) return () => ToggleBookmarkCurrentPage();
            if (ctrl && !alt && !shift && key == Key.H) return () => ToggleSidebar("History");
            if (ctrl && !alt && !shift && key == Key.J) return () => OpenDownloadsTab();
            if (ctrl && !alt && shift && key == Key.Delete) return () => OpenLibraryTab("history"); // "Clear browsing data" like other browsers
            if (ctrl && !alt && !shift && key == Key.B) return () => ToggleSidebar("Bookmarks");
            if ((ctrl && !alt && !shift && key == Key.L) || (alt && !ctrl && key == Key.D) || key == Key.F6)
                return () => TxtAddressBar.Focus();
            if (ctrl && !alt && key == Key.Tab) return () => SwitchTab(shift ? -1 : 1);
            if (ctrl && !alt && !shift && key == Key.PageDown) return () => SwitchTab(1);
            if (ctrl && !alt && !shift && key == Key.PageUp) return () => SwitchTab(-1);
            if (ctrl && !alt && !shift && key == Key.F4) return () => { if (SelectedTab != null) CloseTab(SelectedTab); };
            if (ctrl && !alt && !shift && key >= Key.D1 && key <= Key.D9)
            {
                // Ctrl+1..8 = that tab, Ctrl+9 = last tab (standard browser behaviour)
                int slot = key == Key.D9 ? Tabs.Count - 1 : key - Key.D1;
                return () => { if (slot >= 0 && slot < Tabs.Count) SelectedTab = Tabs[slot]; };
            }
            if (_htmlFullscreen && key == Key.Escape) return () => ExitHtmlFullscreen();
            if (key == Key.Escape && !ctrl && !alt && !shift && SelectedTab?.IsLoading == true && !TxtAddressBar.IsKeyboardFocusWithin)
                return () => BtnReload_Click(this, new RoutedEventArgs());
            if (key == Key.F5 || (ctrl && !alt && !shift && key == Key.R)) return () => SelectedTab?.WebView?.CoreWebView2?.Reload();
            if (alt && !ctrl && !shift && key == Key.Left) return () => BtnBack_Click(this, new RoutedEventArgs());
            if (alt && !ctrl && !shift && key == Key.Right) return () => BtnForward_Click(this, new RoutedEventArgs());
            return null;
        }
        private void SwitchTab(int delta)
        {
            if (Tabs.Count < 2 || SelectedTab == null) return;
            int index = Tabs.IndexOf(SelectedTab);
            SelectedTab = Tabs[(index + delta + Tabs.Count) % Tabs.Count];
        }
        private async Task NewTabAndFocusAsync()
        {
            try
            {
                await AddNewTabAsync(NewTabUrl);
                TxtAddressBar.Focus();
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to open new tab", ex);
                ThemedDialog.Show($"Failed to open new tab: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void ReopenLastClosedTab()
        {
            if (_closedTabUrls.Count == 0) return;
            string url = _closedTabUrls.Pop();
            _ = OpenTabSafeAsync(url);
        }
        private void TabStripHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabWidth();
        private void UpdateTabWidth()
        {
            if (TabStripHost == null || Tabs.Count == 0) return;
            // Room for the tabs = strip - "+" (28 + 4 margin) - "list tabs" (28 + 8 margin) - breathing room.
            // Capping the ListBox here is what keeps both buttons on screen however many tabs are open.
            double listMax = Math.Max(0, TabStripHost.ActualWidth - 32 - 36 - 6);
            TabListBox.MaxWidth = listMax;
            double perTab = listMax / Tabs.Count - 2; // 2px item margin
            TabWidth = Math.Max(MinTabWidth, Math.Min(MaxTabWidth, perTab));
            // Width changes can shift the selected tab out of view.
            if (SelectedTab != null) ScrollTabIntoView(SelectedTab);
        }
        private void ScrollTabIntoView(BrowserTab tab)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Tabs.Contains(tab)) TabListBox.ScrollIntoView(tab);
            }), DispatcherPriority.Background);
        }
        // Mouse wheel over the tab strip scrolls it sideways (Chrome/Firefox behaviour).
        private void TabListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer? viewer = FindScrollViewer(TabListBox);
            if (viewer == null || viewer.ScrollableWidth <= 0) return;
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
            HideTabPreview();
            e.Handled = true;
        }
        // Middle-click closes a tab.
        private void TabListBox_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && source is not ListBoxItem)
            {
                source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }
            if (source is ListBoxItem item && item.DataContext is BrowserTab tab)
            {
                e.Handled = true;
                CloseTab(tab);
            }
        }
        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer viewer) return viewer;
                ScrollViewer? nested = FindScrollViewer(child);
                if (nested != null) return nested;
            }
            return null;
        }
        // "List all tabs" dropdown
        private void BtnAllTabs_Click(object sender, RoutedEventArgs e)
        {
            // Clicking the button while the popup is open first closes it (click-away); don't instantly re-open.
            if ((DateTime.UtcNow - _allTabsClosedAt).TotalMilliseconds < 250) return;
            HideTabPreview();
            AllTabsList.SelectedItem = SelectedTab;
            if (SelectedTab != null) AllTabsList.ScrollIntoView(SelectedTab);
            // Right-align the 300px card with the button.
            AllTabsPopup.HorizontalOffset = BtnAllTabs.ActualWidth - 300;
            AllTabsPopup.IsOpen = true;
        }
        private void AllTabsItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem item && item.DataContext is BrowserTab tab)
            {
                e.Handled = true;
                AllTabsPopup.IsOpen = false;
                SelectedTab = tab;
            }
        }
        private async void BtnNewTab_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await AddNewTabAsync(NewTabUrl);
                TxtAddressBar.Focus();
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to open new tab", ex);
                ThemedDialog.Show($"Failed to open new tab: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void BtnCloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is BrowserTab tabToClose)
            {
                CloseTab(tabToClose);
            }
        }
        private void CloseTab(BrowserTab tab)
        {
            int index = Tabs.IndexOf(tab);
            if (index < 0) return;
            if (_closedTabs.TryGetValue(tab, out _)) return; // already closing (double click on the X)
            _closedTabs.AddOrUpdate(tab, ClosedMarker);
            bool wasSelected = ReferenceEquals(_selectedTab, tab);
            WebView2? webView = tab.WebView;
            ListBoxItem? container = TabListBox.ItemContainerGenerator.ContainerFromItem(tab) as ListBoxItem;
            HideTabPreview();
            _thumbnails.Remove(tab);
            _tabOwners.Remove(tab);
            if (ReferenceEquals(_activeTab, tab)) _activeTab = null;
            if (container != null)
            {
                ScaleTransform? scale = GetTabScaleTransform(container);
                if (scale != null)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(1, 0.9, TimeSpan.FromMilliseconds(90)));
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(1, 0.9, TimeSpan.FromMilliseconds(90)));
                }
                var closeAnimation = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90));
                closeAnimation.Completed += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    RemoveTabAfterCloseAnimation(tab, wasSelected, webView);
                }), DispatcherPriority.Input);
                container.BeginAnimation(UIElement.OpacityProperty, closeAnimation);
                return;
            }
            RemoveTabAfterCloseAnimation(tab, wasSelected, webView);
        }
        private void RemoveTabAfterCloseAnimation(BrowserTab tab, bool wasSelected, WebView2? webView)
        {
            int index = Tabs.IndexOf(tab);
            if (index < 0) return;
            bool scriptClose = _scriptRequestedClose; // consume it now so it can never go stale
            _scriptRequestedClose = false;
            // Record the URL for Ctrl+Shift+T (reopen closed tab), skipping built-in/blank pages and script-initiated closes.
            string? closedUrl = tab.Url;
            if (!scriptClose && !string.IsNullOrEmpty(closedUrl) && !IsBuiltInPage(closedUrl))
            {
                _closedTabUrls.Push(closedUrl);
                while (_closedTabUrls.Count > MaxClosedTabHistory)
                {
                    // Stack has no TryPop on older runtimes; use the bottom-trim approach.
                    var tmp = _closedTabUrls.ToArray(); // newest first
                    _closedTabUrls.Clear();
                    for (int i = MaxClosedTabHistory - 1; i >= 0; i--)
                        _closedTabUrls.Push(tmp[i]);
                    break;
                }
            }
            // The user may have switched tabs during the 90 ms close animation.
            wasSelected |= ReferenceEquals(_selectedTab, tab);
            if (webView != null)
            {
                TabContentGrid.Children.Remove(webView);
            }
            Tabs.RemoveAt(index);
            // Dispose WebView safely: it might still have pending async callbacks from OAuth/popup flows.
            if (webView != null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try { webView.Dispose(); }
                    catch (Exception ex) { AppLog.Debug("WebView dispose after tab close", ex); }
                }));
            }
            if (Tabs.Count == 0)
            {
                // If this was a script-requested close (e.g. OAuth popup calling window.close()),
                // open a blank new tab so the browser stays open rather than quitting.
                // Only call Close() if the user explicitly closed the last tab (via the X button).
                if (scriptClose)
                {
                    _ = AddNewTabAsync();
                }
                else
                {
                    Close();
                }
                return;
            }
            if (wasSelected)
            {
                SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
            }
            UpdateTabWidth();
        }
    }
}

