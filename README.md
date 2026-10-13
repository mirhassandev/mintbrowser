# Mint Browser

A desktop web browser for Windows, built with C# / .NET 8, WPF, and WebView2.

## Requirements

| Tool | Notes |
|------|-------|
| Windows 10 / 11 | arm64 and x64 supported |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Required to build |
| [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) | Usually pre-installed on Windows 11 |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) *(optional)* | Enables stream/video downloading |
| [FFmpeg](https://ffmpeg.org/) *(optional)* | Used by yt-dlp for muxing |

## Build and run

```powershell
# Build
dotnet build src\CustomBrowser.csproj

# Run from source (debug)
dotnet run --project src\CustomBrowser.csproj

# Run the compiled binary
.\artifacts\bin\CustomBrowser\debug\CustomBrowser.exe
```

> **Note:** Close any running instance before rebuilding — WebView2 locks its data folder.

## Repository layout

```
CustomBrowser.sln
Directory.Build.props         build output → artifacts/
src/
  Views/
    MainWindow/               browser window split into partial classes by topic:
      MainWindow.xaml(.cs)    window chrome, tab strip, address bar, popups
      MainWindow.Tabs.cs      tab lifecycle, WebView2 init, event wiring
      MainWindow.Navigation.cs address bar, suggestions, search engines, shortcuts
      MainWindow.Sidebar.cs   history/bookmarks/passwords sidebar
      MainWindow.Library.cs   host side of the full-page history/bookmarks/passwords manager
      MainWindow.Downloads.cs download interception, queue, dropdown
      MainWindow.DragDrop.cs  tab reorder, tear-off, cross-window transfer
      MainWindow.Streams.cs   HLS/DASH stream detection
      MainWindow.Ytdl.cs      yt-dlp integration
    Dialogs/                  ThemedDialog (dark MessageBox replacement)
  Services/
    DatabaseService.cs        LiteDB history & bookmarks (Firefox Places-style)
    DirectDownloadService.cs  parallel download queue (IDM-style); .External.cs = stream / yt-dlp engines
    Streams/                  HLS / DASH sniffer
    Ytdl/                     yt-dlp subprocess wrapper
  Models/                     BrowserTab, HistoryEntry, BookmarkEntry, DownloadItem, …
  Helpers/                    FaviconCache, LetterTile, AppLog, converters
  Themes/                     Dark theme resource dictionaries
  asset/                      internal pages served at https://local.browser
    newtab.html               new-tab page (shortcuts, search)
    downloads.html            downloads manager page
    library.html              history / bookmarks / passwords manager page (mint://history, mint://bookmarks, mint://passwords)
third_party/Extensions/       uBlock Origin (do not edit)
tools/                        code-dump helper scripts
artifacts/                    build output (git-ignored)
```

## Drag and Drop

| Action | Target | Result |
|--------|--------|--------|
| Drag tab inside strip | Tab strip | Reorders tab smoothly with animations |
| Drag tab away from strip | Desktop | Tears off tab into a new browser window |
| Drag tab onto another window | Other tab strip | Transfers tab to destination window |
| Drag text / search terms | Title / Tab bar | Opens a new tab searching for the text |
| Drag links / URLs | Title / Tab bar | Opens the URL in a new tab (inserted at drop position) |
| Drag images from web page | Title / Tab bar | Opens the image URL in a new tab |
| Drag files / `.url` shortcuts | Title / Tab bar | Opens local files (HTML, PDF, media, etc.) or web shortcuts |

## Keyboard shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+T` | New tab |
| `Ctrl+W` / `Ctrl+F4` | Close tab |
| `Ctrl+Shift+T` | Reopen last closed tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab |
| `Ctrl+1…8` | Switch to tab by position |
| `Ctrl+9` | Switch to last tab |
| `Ctrl+L` / `Alt+D` / `F6` | Focus address bar |
| `Ctrl+D` | Bookmark / edit bookmark |
| `Ctrl+H` | Toggle History sidebar |
| `Ctrl+B` | Toggle Bookmarks sidebar |
| `Ctrl+J` | Open Downloads tab |
| `Ctrl+R` / `F5` | Reload page |
| `Alt+Left` / `Alt+Right` | Back / Forward |
| `Esc` | Stop loading / close address suggestions |
| `F11` *(from page)* | HTML fullscreen |

## Data on disk

All user data lives under `%LocalAppData%\CustomBrowser`:

| Path | Contents |
|------|----------|
| `WebView2Data\` | WebView2 profile (cookies, cache, saved passwords) |
| `Extensions\` | User-installed browser extensions |
| `MintPlaces.db` | Browsing history, bookmarks and the download list (LiteDB) |
| `download-settings.json` | Download queue limit and speed cap |
| `shortcuts.json` | New-tab page shortcut tiles |
| `logs\app.log` | Application log |

## Tools

```powershell
# Produce a single-file code dump (default output: %USERPROFILE%\Downloads\_code_review.txt)
pwsh tools\New-CodeDump.ps1

# Copy directly to clipboard (ready to Ctrl+V into Claude in browser)
pwsh tools\New-CodeDump.ps1 -ToClipboard

# Target a specific folder or subsystem to stay well under token limits
pwsh tools\New-CodeDump.ps1 -Path src\Views\MainWindow -ToClipboard
pwsh tools\New-CodeDump.ps1 -Path src\Services -ToClipboard
pwsh tools\New-CodeDump.ps1 -Path tools -ToClipboard

# Output in Markdown code blocks or XML format
pwsh tools\New-CodeDump.ps1 -Format Markdown
pwsh tools\New-CodeDump.ps1 -Format Xml
```

## Conventions

- **Comments** state what the code does and why, never its history (that belongs in the commit message). Public members get an XML `<summary>`; note the thread they must run on when it matters (UI thread vs. any).
- **Analyzers** run on every build and the project builds with 0 warnings. CS1591 (missing XML docs) is hidden until the public API is fully documented.
- **Tests**: `dotnet test tests\CustomBrowser.Tests\CustomBrowser.Tests.csproj`. Pure logic (parsers, site matching, file-name rules) belongs in tests; UI code is checked by hand.
- **Build output** is `artifacts\bin\CustomBrowser\<debug|release>\CustomBrowser.exe`.
- **Commits**: one concern per commit, build and tests green before each.
