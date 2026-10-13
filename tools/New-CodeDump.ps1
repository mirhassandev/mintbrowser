#requires -Version 7
<#
.SYNOPSIS
    Generates a consolidated repository dump for code review and LLM handoff (Claude, ChatGPT, etc.).

.DESCRIPTION
    Scans the repository, collects source code files according to the project's tech stack,
    prepends an architectural summary and git metadata, and outputs a single text file
    (or copies directly to clipboard for pasting into Claude in browser).

.PARAMETER Out
    Destination file path. Defaults to $HOME\Downloads\_code_review.txt.

.PARAMETER Path
    Optional path filter to include only specific directories or files (e.g. -Path src/Views/MainWindow).

.PARAMETER ToClipboard
    If specified, copies the entire dump directly to the Windows clipboard for instant pasting into browser LLM chats.

.PARAMETER Format
    Formatting style for the files: 'Standard' (delimited), 'Markdown' (fenced code blocks), or 'Xml' (repomix style). Default is 'Standard'.

.EXAMPLE
    pwsh tools\New-CodeDump.ps1
    pwsh tools\New-CodeDump.ps1 -ToClipboard
    pwsh tools\New-CodeDump.ps1 -Path src/Views/MainWindow -ToClipboard
    pwsh tools\New-CodeDump.ps1 -Format Markdown
#>
[CmdletBinding()]
param(
    [string]$Out = (Join-Path $HOME 'Downloads\_code_review.txt'),
    [string[]]$Path = @(),
    [switch]$ToClipboard,
    [ValidateSet('Standard', 'Markdown', 'Xml')]
    [string]$Format = 'Standard'
)

$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root

# File extensions relevant to the MintBrowser stack (.NET 8 WPF, C# 12, XAML, HTML/CSS/JS assets, tools)
$exts = @(
    '.cs', '.xaml', '.csproj', '.sln', '.slnx', '.props', '.targets',
    '.config', '.manifest', '.resx', '.settings', '.json',
    '.html', '.css', '.js', '.md', '.txt', '.xml', '.ps1', '.cmd', '.bat', '.yaml', '.yml',
    '.editorconfig', '.gitignore', '.gitattributes'
)

# Skip binary, generated build artifacts, vendor libraries, caches, and dumps
$skip = '(?i)(^|/)(Extensions|Extentions|_backups|bin|obj|artifacts|third_party|\.vs|\.git|\.idea|\.vscode|\.agents|\.gemini|node_modules)/' +
        '|(^|/)(install_|Apply-)[^/]*\.ps1$' +
        '|(^|/)(_code_review|_codebase_dump|repomix-output|test_dump)[^/]*\.(txt|xml|md)$' +
        '|(^|/)package-lock\.json$' +
        '|\.min\.(js|css)$' +
        '|\.(ico|png|jpg|jpeg|gif|webp|svg|wasm|wat|mp3|mp4|dll|exe|pdb|pma|db|sqlite)$'

# Discover tracked and staged files
$allFiles = git -c core.quotepath=off ls-files --cached --others --exclude-standard
$files = $allFiles |
    Where-Object {
        $_ -notmatch $skip -and (
            $exts -contains [IO.Path]::GetExtension($_).ToLower() -or
            $exts -contains [IO.Path]::GetFileName($_).ToLower()
        )
    } |
    Where-Object {
        if ($Path.Count -gt 0) {
            $f = $_
            $matched = $false
            foreach ($p in $Path) {
                $normP = $p.Replace('\', '/')
                if ($f.StartsWith($normP, [StringComparison]::OrdinalIgnoreCase) -or $f -like "*$normP*") {
                    $matched = $true
                    break
                }
            }
            $matched
        } else {
            $true
        }
    } |
    Where-Object {
        (Test-Path -LiteralPath $_) -and ((Get-Item -LiteralPath $_).Length -le 2MB)
    } |
    Sort-Object

$sb = [System.Text.StringBuilder]::new()

# Header: Project Context & LLM System Info
[void]$sb.AppendLine('================================================================================')
[void]$sb.AppendLine('MINT BROWSER - REPOSITORY CODE DUMP & ARCHITECTURAL HANDOFF')
[void]$sb.AppendLine('================================================================================')
[void]$sb.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$currentBranch = (git rev-parse --abbrev-ref HEAD 2>$null)
if (-not $currentBranch) { $currentBranch = 'unknown' }
[void]$sb.AppendLine("Branch: $currentBranch")
$dirty = if (git status --porcelain 2>$null) { 'HAS UNCOMMITTED CHANGES' } else { 'clean' }
[void]$sb.AppendLine("Working Tree: $dirty")
[void]$sb.AppendLine('')
[void]$sb.AppendLine('--- TECH STACK & ARCHITECTURE ---')
[void]$sb.AppendLine('- Platform: Windows Desktop (x64 / arm64)')
[void]$sb.AppendLine('- Runtime & Language: .NET 8 (net8.0-windows), C# 12, WPF, XAML')
[void]$sb.AppendLine('- Web Engine: Microsoft.Web.WebView2 (Chromium)')
[void]$sb.AppendLine('- Database: LiteDB (embedded document store for history & bookmarks in MintPlaces.db)')
[void]$sb.AppendLine('- Downloads: Downloader NuGet + custom background queue (DirectDownloadService)')
[void]$sb.AppendLine('- Media Sniffer & Downloader: HLS/DASH parser (StreamSniffer) + yt-dlp wrapper')
[void]$sb.AppendLine('- Bundled Extensions: uBlock Origin (in third_party/Extensions)')
[void]$sb.AppendLine('- Build Output: Centralized in artifacts/ via Directory.Build.props')
[void]$sb.AppendLine('- Key Architecture Patterns:')
[void]$sb.AppendLine('    * MainWindow partial classes: MainWindow.Tabs.cs, MainWindow.Navigation.cs,')
[void]$sb.AppendLine('      MainWindow.Downloads.cs, MainWindow.DragDrop.cs, MainWindow.Sidebar.cs,')
[void]$sb.AppendLine('      MainWindow.Streams.cs, MainWindow.Ytdl.cs')
[void]$sb.AppendLine('    * Internal pages hosted at https://local.browser (newtab.html, downloads.html)')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('--- BUILD INSTRUCTIONS ---')
[void]$sb.AppendLine('Build:   dotnet build src\CustomBrowser.csproj')
[void]$sb.AppendLine('Run:     dotnet run --project src\CustomBrowser.csproj')
[void]$sb.AppendLine('Artifact: .\artifacts\bin\CustomBrowser\debug\CustomBrowser.exe')
[void]$sb.AppendLine('')

# Git Branches & History
$mainBranch = if (git rev-parse --verify master 2>$null) { 'master' } elseif (git rev-parse --verify main 2>$null) { 'main' } else { '' }
if ($mainBranch) {
    [void]$sb.AppendLine("--- BRANCHES MERGED INTO $mainBranch ---")
    git branch --merged $mainBranch 2>$null | ForEach-Object { [void]$sb.AppendLine($_) }
    [void]$sb.AppendLine('')
}

[void]$sb.AppendLine('--- LOCAL BRANCHES ---')
git branch -vv 2>$null | ForEach-Object { [void]$sb.AppendLine($_) }
[void]$sb.AppendLine('')

[void]$sb.AppendLine('--- RECENT COMMITS ---')
git log --oneline -n 25 2>$null | ForEach-Object { [void]$sb.AppendLine($_) }
[void]$sb.AppendLine('')

# Included Files Manifest
[void]$sb.AppendLine("--- FILE LIST ($(@($files).Count) files) ---")
$files | ForEach-Object { [void]$sb.AppendLine($_) }
[void]$sb.AppendLine('')

# File Contents
foreach ($f in $files) {
    $fullPath = Join-Path $root $f
    $content = [IO.File]::ReadAllText($fullPath).Replace("`r`n", "`n")

    switch ($Format) {
        'Markdown' {
            $ext = [IO.Path]::GetExtension($f).TrimStart('.').ToLower()
            $lang = switch ($ext) {
                'cs' { 'csharp' }
                'xaml' { 'xml' }
                'csproj' { 'xml' }
                'props' { 'xml' }
                'targets' { 'xml' }
                'config' { 'xml' }
                'js' { 'javascript' }
                'ps1' { 'powershell' }
                'md' { 'markdown' }
                default { $ext }
            }
            [void]$sb.AppendLine("### File: ``$f```n")
            [void]$sb.AppendLine("````$lang`n$content`n`````n")
        }
        'Xml' {
            [void]$sb.AppendLine("<file path=`"$f`">`n$content`n</file>`n")
        }
        default {
            [void]$sb.Append("`n$('=' * 80)`nFILE: $f`n$('=' * 80)`n")
            [void]$sb.AppendLine($content)
        }
    }
}

$dumpText = $sb.ToString()
[IO.File]::WriteAllText($Out, $dumpText, [Text.UTF8Encoding]::new($false))

$fileInfo = Get-Item $Out
$sizeKb = $fileInfo.Length / 1KB
$estTokens = [Math]::Round($dumpText.Length / 3.8)

if ($ToClipboard) {
    try {
        Set-Clipboard -Value $dumpText
        $clipMsg = ' [COPIED TO CLIPBOARD]'
    } catch {
        $clipMsg = ' [Clipboard copy failed: $_]'
    }
} else {
    $clipMsg = ''
}

Write-Host ("{0} files, {1:N0} KB (~{2:N0} tokens) -> {3}{4}" -f @($files).Count, $sizeKb, $estTokens, $Out, $clipMsg) -ForegroundColor Cyan

