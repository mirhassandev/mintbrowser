using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using CustomBrowser.Helpers;
using CustomBrowser.Models;
using CustomBrowser.Views;

namespace CustomBrowser
{
    /// <summary>
    /// The "extensions" page (https://local.browser/extensions.html, shown as mint://extensions): list the extensions loaded in the
    /// browser, turn each one on or off, remove the ones the user added, and add new ones from a .crx / .zip / manifest.json file
    /// or straight from the official uBlock Origin release on GitHub.
    ///
    /// Where extensions live:
    ///   * "Built-in"   = shipped next to the exe (Extensions\ folder). They can be turned off, never removed.
    ///   * "Installed"  = %LocalAppData%\CustomBrowser\Extensions\&lt;name&gt;. Both kinds are loaded again on every start.
    /// The browser engine loads UNPACKED extensions only, so a .crx / .zip is unpacked into that folder first.
    ///
    /// The on / off choice is stored in %LocalAppData%\CustomBrowser\extensions-state.json, because the engine switches an extension
    /// back on whenever it is added again (which happens at every start).
    /// Like the library page, this page can only ask: every removal and every download is confirmed in a native dialog first.
    /// </summary>
    public partial class MainWindow
    {
        private const string ExtensionsUrl = "https://local.browser/extensions.html";

        /// <summary>Messages the extensions page may send through the web-message bridge.</summary>
        private static readonly HashSet<string> ExtensionMessageTypes = new(StringComparer.Ordinal)
        {
            "get-extensions", "set-extension-enabled", "remove-extension",
            "install-extension", "install-ublock", "open-extension-options", "install-from-store", "open-store"
        };

        private static bool IsExtensionsPage(string? url) =>
            !string.IsNullOrEmpty(url) && url.StartsWith(ExtensionsUrl, StringComparison.OrdinalIgnoreCase);

        /// <summary>mint://extensions, chrome://extensions, edge://extensions, about:extensions.</summary>
        private static bool IsExtensionsAlias(string input)
        {
            input = input.Trim().TrimEnd('/');
            foreach (string prefix in new[] { "mint://", "chrome://", "edge://", "about:" })
            {
                if (!input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string name = input[prefix.Length..].Trim().ToLowerInvariant();
                if (name is "extensions" or "extension") return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Known extensions (filled while they are loaded) and the saved on/off state
        // ------------------------------------------------------------------

        /// <summary>An extension this app loaded from a folder. Key = "bundled:&lt;folder&gt;" or "user:&lt;folder&gt;" (stable even when the app is moved).</summary>
        private sealed record ManagedExtension(string Key, string Folder, bool BuiltIn);

        private static readonly ConcurrentDictionary<string, ManagedExtension> ExtensionFolders = new(StringComparer.Ordinal);

        /// <summary>Written into a folder that could not be deleted right away (a file was still in use); it is deleted at the next start.</summary>
        private const string ExtensionRemovedMarker = ".removed";

        private sealed class ExtensionStateFile
        {
            public List<string> Disabled { get; set; } = new();
            public List<string> Removed { get; set; } = new();
        }

        private static readonly object ExtensionStateLock = new();
        private static string ExtensionStatePath => Path.Combine(GetUserDataFolder(), "extensions-state.json");
        private static string UserExtensionsRoot => Path.Combine(GetUserDataFolder(), "Extensions");

        private static HashSet<string> LoadDisabledExtensionKeys()
        {
            lock (ExtensionStateLock)
            {
                try
                {
                    string path = ExtensionStatePath;
                    if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
                    ExtensionStateFile? state = JsonSerializer.Deserialize<ExtensionStateFile>(File.ReadAllText(path));
                    return new HashSet<string>(state?.Disabled ?? new List<string>(), StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Could not read the saved extension on/off state - all extensions start turned on", ex);
                    return new HashSet<string>(StringComparer.Ordinal);
                }
            }
        }

        private static void SetExtensionDisabled(string key, bool disabled)
        {
            lock (ExtensionStateLock)
            {
                try
                {
                    HashSet<string> keys = LoadDisabledExtensionKeys();
                    bool changed = disabled ? keys.Add(key) : keys.Remove(key);
                    if (!changed) return;
                    string path = ExtensionStatePath;
                    string temp = path + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(new ExtensionStateFile { Disabled = keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), Removed = LoadRemovedExtensionKeys().OrderBy(k => k, StringComparer.Ordinal).ToList() }));
                    File.Move(temp, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Could not save the extension on/off state", ex);
                }
            }
        }

        // ------------------------------------------------------------------
        // Opening the page
        // ------------------------------------------------------------------

        /// <summary>Built-in extensions the user removed. They live next to the exe, so they are skipped at start-up instead of deleted.</summary>
        private static HashSet<string> LoadRemovedExtensionKeys()
        {
            lock (ExtensionStateLock)
            {
                try
                {
                    string path = ExtensionStatePath;
                    if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
                    ExtensionStateFile? state = JsonSerializer.Deserialize<ExtensionStateFile>(File.ReadAllText(path));
                    return new HashSet<string>(state?.Removed ?? new List<string>(), StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Could not read the list of removed built-in extensions", ex);
                    return new HashSet<string>(StringComparer.Ordinal);
                }
            }
        }

        private static void SetExtensionRemoved(string key, bool removed)
        {
            lock (ExtensionStateLock)
            {
                try
                {
                    HashSet<string> keys = LoadRemovedExtensionKeys();
                    bool changed = removed ? keys.Add(key) : keys.Remove(key);
                    if (!changed) return;
                    string path = ExtensionStatePath;
                    string temp = path + ".tmp";
                    var state = new ExtensionStateFile
                    {
                        Disabled = LoadDisabledExtensionKeys().OrderBy(k => k, StringComparer.Ordinal).ToList(),
                        Removed = keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
                    };
                    File.WriteAllText(temp, JsonSerializer.Serialize(state));
                    File.Move(temp, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Could not save the list of removed built-in extensions", ex);
                }
            }
        }

        private void OpenExtensionsTab() => _ = OpenExtensionsTabAsync();

        private async Task OpenExtensionsTabAsync()
        {
            try
            {
                BrowserTab? existing = Tabs.FirstOrDefault(tab => IsExtensionsPage(tab.Url));
                if (existing != null)
                {
                    SelectedTab = existing;
                    return;
                }
                await AddNewTabAsync(ExtensionsUrl);
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not open the extensions tab", ex);
                ThemedDialog.Show($"Could not open the Extensions page: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------
        // Manifest reading (name / version / description / icon / options page)
        // ------------------------------------------------------------------

        private sealed class ManifestInfo
        {
            public string Name = string.Empty;
            public string Version = string.Empty;
            public string Description = string.Empty;
            public string? IconDataUri;
            public string? OptionsPage;
            public bool IsValid;
        }

        private static readonly JsonDocumentOptions LenientJson = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static string? ManifestString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static ManifestInfo ReadManifest(string folder)
        {
            var info = new ManifestInfo();
            try
            {
                string manifestPath = Path.Combine(folder, "manifest.json");
                if (!File.Exists(manifestPath)) return info;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath), LenientJson);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return info;

                string? locale = ManifestString(root, "default_locale");
                info.Name = LocalizeManifestText(folder, locale, ManifestString(root, "name"));
                info.Version = ManifestString(root, "version") ?? string.Empty;
                info.Description = LocalizeManifestText(folder, locale, ManifestString(root, "description"));
                info.IsValid = !string.IsNullOrWhiteSpace(info.Name) && !string.IsNullOrWhiteSpace(info.Version)
                               && root.TryGetProperty("manifest_version", out _);

                if (root.TryGetProperty("options_ui", out JsonElement optionsUi))
                {
                    info.OptionsPage = ManifestString(optionsUi, "page");
                }
                info.OptionsPage ??= ManifestString(root, "options_page");
                info.IconDataUri = ReadBestIcon(folder, root);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not read an extension manifest", ex);
            }
            return info;
        }

        /// <summary>"__MSG_extName__" -> the text from _locales/&lt;locale&gt;/messages.json. Anything else is returned as it is.</summary>
        private static string LocalizeManifestText(string folder, string? defaultLocale, string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (!text.StartsWith("__MSG_", StringComparison.Ordinal) || !text.EndsWith("__", StringComparison.Ordinal) || text.Length <= 8)
            {
                return text;
            }
            string key = text[6..^2];
            foreach (string locale in new[] { defaultLocale, "en", "en_US" }.OfType<string>().Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    // A locale name is a plain folder name, never a path.
                    if (!string.Equals(Path.GetFileName(locale!), locale, StringComparison.Ordinal)) continue;
                    string file = Path.Combine(folder, "_locales", locale!, "messages.json");
                    if (!File.Exists(file)) continue;
                    using JsonDocument messages = JsonDocument.Parse(File.ReadAllText(file), LenientJson);
                    if (messages.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (JsonProperty entry in messages.RootElement.EnumerateObject())
                    {
                        if (!string.Equals(entry.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                        string? message = ManifestString(entry.Value, "message");
                        if (!string.IsNullOrEmpty(message)) return message;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug("Could not read an extension's translated text", ex);
                }
            }
            return key;
        }

        /// <summary>The largest icon in the manifest as a data: address (the built-in pages cannot load file:// pictures).</summary>
        private static string? ReadBestIcon(string folder, JsonElement manifest)
        {
            if (!manifest.TryGetProperty("icons", out JsonElement icons) || icons.ValueKind != JsonValueKind.Object) return null;
            int bestSize = -1;
            string? bestPath = null;
            foreach (JsonProperty icon in icons.EnumerateObject())
            {
                if (!int.TryParse(icon.Name, out int size) || size > 256 || size <= bestSize) continue;
                if (icon.Value.ValueKind != JsonValueKind.String) continue;
                bestSize = size;
                bestPath = icon.Value.GetString();
            }
            if (string.IsNullOrWhiteSpace(bestPath)) return null;
            try
            {
                string folderFull = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string file = Path.GetFullPath(Path.Combine(folderFull, bestPath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar)));
                if (!file.StartsWith(folderFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return null;
                if (new FileInfo(file).Length > 256 * 1024) return null;
                string? mime = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".svg" => "image/svg+xml",
                    ".webp" => "image/webp",
                    _ => null
                };
                return mime == null ? null : "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(file));
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not read an extension icon", ex);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Messages from / to the page
        // ------------------------------------------------------------------

        private static void PostExtensionResult(CoreWebView2 core, bool ok, string message)
        {
            try
            {
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "extension-result", ok, message }, BridgeJsonOptions));
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not answer the extensions page (tab closed?)", ex);
            }
        }

        private static void PostExtensionBusy(CoreWebView2 core, bool busy, string message)
        {
            try
            {
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "extension-busy", busy, message }, BridgeJsonOptions));
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not update the extensions page (tab closed?)", ex);
            }
        }

        private static async Task<string> BuildExtensionsMessageAsync(CoreWebView2 core)
        {
            // The page can ask before the start-up load has finished; wait for it so the list is complete.
            Task? loading = _extensionsTask;
            if (loading != null)
            {
                try { await loading; } catch (Exception ex) { AppLog.Debug("Extension start-up load had failed", ex); }
            }

            var installed = await core.Profile.GetBrowserExtensionsAsync();
            var rows = new List<object>();
            foreach (CoreWebView2BrowserExtension ext in installed.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                ExtensionFolders.TryGetValue(ext.Id, out ManagedExtension? known);
                ManifestInfo manifest = known != null ? ReadManifest(known.Folder) : new ManifestInfo();
                rows.Add(new
                {
                    id = ext.Id,
                    name = ext.Name,
                    enabled = ext.IsEnabled,
                    version = manifest.Version,
                    description = manifest.Description,
                    icon = manifest.IconDataUri,
                    builtIn = known?.BuiltIn ?? false,
                    removable = known != null,
                    hasOptions = !string.IsNullOrWhiteSpace(manifest.OptionsPage)
                });
            }
            bool ublock = installed.Any(e => e.Name.Contains("uBlock", StringComparison.OrdinalIgnoreCase));
            return JsonSerializer.Serialize(new { type = "extensions-data", data = rows, ublockInstalled = ublock }, BridgeJsonOptions);
        }

        private static async Task PostExtensionsListAsync(CoreWebView2 core)
        {
            try
            {
                core.PostWebMessageAsJson(await BuildExtensionsMessageAsync(core));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not list the extensions", ex);
                PostExtensionResult(core, false, "The extension list could not be read: " + ex.Message);
            }
        }

        private static int _extensionTaskRunning;

        private async Task HandleExtensionsMessageAsync(CoreWebView2 core, string msgType, JsonElement root)
        {
            // Leave the WebView2 event callback first: the questions below are modal dialogs.
            await Task.Yield();
            try
            {
                switch (msgType)
                {
                    case "get-extensions":
                        await PostExtensionsListAsync(core);
                        break;

                    case "set-extension-enabled":
                    {
                        string? id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
                        bool? enable = null;
                        if (root.TryGetProperty("enabled", out JsonElement enEl) &&
                            (enEl.ValueKind == JsonValueKind.True || enEl.ValueKind == JsonValueKind.False))
                        {
                            enable = enEl.GetBoolean();
                        }
                        if (string.IsNullOrEmpty(id) || enable == null)
                        {
                            PostExtensionResult(core, false, "That request was not understood.");
                            break;
                        }
                        CoreWebView2BrowserExtension? ext = await FindExtensionAsync(core, id);
                        if (ext == null)
                        {
                            PostExtensionResult(core, false, "That extension is no longer installed.");
                            await PostExtensionsListAsync(core);
                            break;
                        }
                        await ext.EnableAsync(enable.Value);
                        if (ExtensionFolders.TryGetValue(ext.Id, out ManagedExtension? known))
                        {
                            SetExtensionDisabled(known.Key, disabled: !enable.Value);
                        }
                        AppLog.Info($"Extension '{ext.Name}' turned {(enable.Value ? "on" : "off")}");
                        PostExtensionResult(core, true, $"{ext.Name} is {(enable.Value ? "on" : "off")}. Reload open pages for the change to apply.");
                        await PostExtensionsListAsync(core);
                        break;
                    }

                    case "remove-extension":
                    {
                        string? id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
                        CoreWebView2BrowserExtension? ext = string.IsNullOrEmpty(id) ? null : await FindExtensionAsync(core, id);
                        if (ext == null)
                        {
                            PostExtensionResult(core, false, "That extension is no longer installed.");
                            await PostExtensionsListAsync(core);
                            break;
                        }
                        if (!ExtensionFolders.TryGetValue(ext.Id, out ManagedExtension? known))
                        {
                            PostExtensionResult(core, false, "The browser engine provides this extension, so it cannot be removed. You can turn it off instead.");
                            break;
                        }
                        if (!ConfirmDestructive("Remove extension",
                                known.BuiltIn ? $"Remove \"{ext.Name}\" from Mint Browser?\n\nIt will no longer load when the browser starts. You can add it back later from the extensions page." : $"Remove \"{ext.Name}\" from Mint Browser?\n\nIts files and settings will be deleted. This cannot be undone."))
                        {
                            PostExtensionResult(core, false, string.Empty);
                            break;
                        }
                        string name = ext.Name;
                        await ext.RemoveAsync();
                        ExtensionFolders.TryRemove(ext.Id, out _);
                        SetExtensionDisabled(known.Key, disabled: false);
                        if (known.BuiltIn) SetExtensionRemoved(known.Key, true); else RemoveExtensionFolder(known.Folder);
                        AppLog.Info($"Extension '{name}' removed");
                        PostExtensionResult(core, true, $"{name} was removed.");
                        await PostExtensionsListAsync(core);
                        break;
                    }

                    case "open-store":
                    {
                        string? storeKey = root.TryGetProperty("store", out JsonElement storeKeyEl) ? storeKeyEl.GetString() : null;
                        string storeUrl = string.Equals(storeKey, "edge", StringComparison.Ordinal)
                            ? "https://microsoftedge.microsoft.com/addons"
                            : "https://chromewebstore.google.com/category/extensions";
                        await AddNewTabAsync(storeUrl);
                        break;
                    }

                    case "open-extension-options":
                    {
                        string? id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
                        if (string.IsNullOrEmpty(id) || !ExtensionFolders.TryGetValue(id, out ManagedExtension? known))
                        {
                            PostExtensionResult(core, false, "This extension has no settings page.");
                            break;
                        }
                        string? page = ReadManifest(known.Folder).OptionsPage;
                        if (string.IsNullOrWhiteSpace(page))
                        {
                            PostExtensionResult(core, false, "This extension has no settings page.");
                            break;
                        }
                        await AddNewTabAsync($"chrome-extension://{id}/{page.TrimStart('/')}");
                        break;
                    }

                    case "install-extension":
                    case "install-ublock":
                    case "install-from-store":
                    {
                        // One download / install at a time: a double click must not unpack the same package twice.
                        if (Interlocked.Exchange(ref _extensionTaskRunning, 1) == 1)
                        {
                            PostExtensionResult(core, false, "Another extension task is still running.");
                            break;
                        }
                        try
                        {
                            if (msgType == "install-ublock") await InstallUBlockOriginAsync(core);
                            else if (msgType == "install-from-store")
                            {
                                string? storeInput = root.TryGetProperty("input", out JsonElement storeInputEl) && storeInputEl.ValueKind == JsonValueKind.String
                                    ? storeInputEl.GetString()
                                    : null;
                                await InstallFromStoreAsync(core, storeInput);
                            }
                            else await InstallExtensionFromFileAsync(core);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _extensionTaskRunning, 0);
                            PostExtensionBusy(core, false, string.Empty);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"Extensions page request '{msgType}' failed", ex);
                PostExtensionResult(core, false, ex.Message);
                await PostExtensionsListAsync(core);
            }
        }

        private static async Task<CoreWebView2BrowserExtension?> FindExtensionAsync(CoreWebView2 core, string id)
        {
            foreach (CoreWebView2BrowserExtension ext in await core.Profile.GetBrowserExtensionsAsync())
            {
                if (string.Equals(ext.Id, id, StringComparison.Ordinal)) return ext;
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Adding extensions
        // ------------------------------------------------------------------

        private const long MaxExtensionPackageBytes = 100L * 1024 * 1024;
        private const long MaxExtensionUnpackedBytes = 300L * 1024 * 1024;
        private const int MaxExtensionFiles = 20000;

        /// <summary>Official release of uBlock Origin on GitHub. Used when the "latest release" lookup fails.</summary>
        private const string UBlockFallbackVersion = "1.75.0";
        private const string UBlockFallbackUrl = "https://github.com/gorhill/uBlock/releases/download/1.75.0/uBlock0_1.75.0.chromium.crx";
        private const string UBlockLatestReleaseApi = "https://api.github.com/repos/gorhill/uBlock/releases/latest";

        private static readonly HttpClient ExtensionHttp = CreateExtensionHttpClient();

        private static HttpClient CreateExtensionHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MintBrowser-Extensions/1.0");
            return client;
        }

        private async Task InstallExtensionFromFileAsync(CoreWebView2 core)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Add extension",
                Filter = "Extension package (*.crx;*.zip)|*.crx;*.zip|Extension manifest (manifest.json)|manifest.json",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true)
            {
                PostExtensionResult(core, false, string.Empty);
                return;
            }
            PostExtensionBusy(core, true, "Adding extension...");
            string? tempRoot = null;
            try
            {
                (string manifestRoot, string root) = await Task.Run(() => StageExtension(dialog.FileName));
                tempRoot = root;
                await InstallStagedExtensionAsync(core, manifestRoot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warn("Adding an extension from a file failed", ex);
                PostExtensionResult(core, false, "Could not add the extension: " + ex.Message);
            }
            finally
            {
                if (tempRoot != null) ExtensionTryDelete(tempRoot);
                await PostExtensionsListAsync(core);
            }
        }

        // ------------------------------------------------------------------
        // Extension store: Chrome Web Store first, Edge Add-ons as the fallback, or a direct .crx link
        // ------------------------------------------------------------------

        /// <summary>Hosts a store download may be redirected to. A direct .crx link the user pastes is the one exception (it is confirmed by host first).</summary>
        private static readonly string[] StoreDownloadHosts = { "google.com", "googleusercontent.com", "gvt1.com", "microsoft.com", "azureedge.net" };

        private static bool IsTrustedStoreHost(string host)
        {
            foreach (string suffix in StoreDownloadHosts)
            {
                if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>An extension ID is 32 letters from a to p. Store links end with it, so the last match wins.</summary>
        private static string? FindExtensionId(string text)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), "(?<![a-p])[a-p]{32}(?![a-p])");
            return matches.Count == 0 ? null : matches[matches.Count - 1].Value;
        }

        private static Uri BuildChromeStoreCrxUrl(string id) => new(
            "https://clients2.google.com/service/update2/crx?response=redirect&os=win&arch=x64&os_arch=x86_64&nacl_arch=x86-64" +
            "&prod=chromecrx&prodchannel=&prodversion=140.0.0.0&lang=en-US&acceptformat=crx3" +
            "&x=id%3D" + id + "%26installsource%3Dondemand%26uc");

        private static Uri BuildEdgeStoreCrxUrl(string id) => new(
            "https://edge.microsoft.com/extensionwebstorebase/v1/crx?response=redirect" +
            "&x=id%3D" + id + "%26installsource%3Dondemand%26uc");

        private static bool LooksLikeCrx(string path)
        {
            try
            {
                using FileStream file = File.OpenRead(path);
                byte[] head = new byte[4];
                return file.Read(head, 0, 4) == 4 && head[0] == 'C' && head[1] == 'r' && head[2] == '2' && head[3] == '4';
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not read the downloaded package header", ex);
                return false;
            }
        }

        /// <summary>
        /// Downloads a .crx. Returns false when the source simply does not have the extension (so the next store can be tried);
        /// throws for real failures. With requireStoreHost the final address (after redirects) must be a known store host.
        /// </summary>
        private static async Task<bool> DownloadCrxAsync(Uri url, string destination, bool requireStoreHost, CancellationToken ct)
        {
            using HttpResponseMessage response = await ExtensionHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                response.StatusCode == System.Net.HttpStatusCode.NoContent ||
                response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return false;
            }
            response.EnsureSuccessStatusCode();

            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri == null || finalUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("The download was redirected to an unexpected address.");
            }
            if (requireStoreHost && !IsTrustedStoreHost(finalUri.Host))
            {
                throw new InvalidDataException("The download was redirected to an unexpected address.");
            }
            if (response.Content.Headers.ContentLength is long declared)
            {
                if (declared == 0) return false;
                if (declared > MaxExtensionPackageBytes) throw new InvalidDataException("The extension package is unexpectedly large.");
            }

            await CopyCappedAsync(response, destination, ct); // the file is closed again before its header is read
            return LooksLikeCrx(destination);
        }

        /// <summary>Streams a response body to a file and stops with an error as soon as it grows past <see cref="MaxExtensionPackageBytes"/> (a server may lie about, or omit, Content-Length).</summary>
        private static async Task CopyCappedAsync(HttpResponseMessage response, string destination, CancellationToken ct)
        {
            await using Stream network = await response.Content.ReadAsStreamAsync(ct);
            await using FileStream file = File.Create(destination);
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await network.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxExtensionPackageBytes) throw new InvalidDataException("The extension package is unexpectedly large.");
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        private async Task InstallFromStoreAsync(CoreWebView2 core, string? input)
        {
            input = input?.Trim() ?? string.Empty;
            string? id = null;
            Uri? direct = null;
            if (Uri.TryCreate(input, UriKind.Absolute, out Uri? link) && link.Scheme == Uri.UriSchemeHttps)
            {
                if (link.AbsolutePath.EndsWith(".crx", StringComparison.OrdinalIgnoreCase)) direct = link;
                else id = FindExtensionId(link.AbsolutePath);
            }
            else if (!input.Contains("://", StringComparison.Ordinal))
            {
                id = FindExtensionId(input);
            }

            if (direct == null && id == null)
            {
                PostExtensionResult(core, false, "That is not a store link, an extension ID or a direct .crx link.");
                return;
            }

            string question = direct != null
                ? $"Download an extension package from {direct.Host} and add it to Mint Browser?\n\n" +
                  "Only continue if you trust this site. Like every extension it can read and change the pages you visit."
                : $"Download the extension with ID {id} from the Chrome Web Store (or Edge Add-ons when it is not there) and add it to Mint Browser?\n\n" +
                  "Like every extension it can read and change the pages you visit. Some store extensions need features this browser engine does not have, so not every one will work.";
            bool yes = ThemedDialog.Show(this, question, "Add extension", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
            if (!yes)
            {
                PostExtensionResult(core, false, string.Empty);
                return;
            }

            PostExtensionBusy(core, true, "Downloading extension...");
            string tempFile = Path.Combine(Path.GetTempPath(), "MintBrowserExt_" + Guid.NewGuid().ToString("N") + ".crx");
            string? tempRoot = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                bool found;
                if (direct != null)
                {
                    found = await DownloadCrxAsync(direct, tempFile, requireStoreHost: false, cts.Token);
                }
                else
                {
                    found = await DownloadCrxAsync(BuildChromeStoreCrxUrl(id!), tempFile, requireStoreHost: true, cts.Token);
                    if (!found)
                    {
                        PostExtensionBusy(core, true, "Not in the Chrome Web Store - trying Edge Add-ons...");
                        found = await DownloadCrxAsync(BuildEdgeStoreCrxUrl(id!), tempFile, requireStoreHost: true, cts.Token);
                    }
                }
                if (!found)
                {
                    throw new InvalidDataException("no package was found for that link or ID in the Chrome Web Store or Edge Add-ons.");
                }

                AppLog.Info($"Extension package downloaded ({(direct != null ? direct.Host : id)})");
                PostExtensionBusy(core, true, "Installing extension...");
                (string manifestRoot, string root) = await Task.Run(() => StageExtension(tempFile));
                tempRoot = root;
                await InstallStagedExtensionAsync(core, manifestRoot);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Adding an extension from the store failed", ex);
                string reason = ex is OperationCanceledException ? "the download took too long" : ex.Message;
                PostExtensionResult(core, false, "Could not add the extension: " + reason);
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch (Exception ex) { AppLog.Debug("Could not delete the downloaded package", ex); }
                if (tempRoot != null) ExtensionTryDelete(tempRoot);
                await PostExtensionsListAsync(core);
            }
        }

        private async Task InstallUBlockOriginAsync(CoreWebView2 core)
        {
            bool yes = ThemedDialog.Show(this,
                "Download uBlock Origin from its official release on GitHub (gorhill/uBlock) and add it to Mint Browser?\n\n" +
                "Like every extension it can read and change the pages you visit.",
                "Add uBlock Origin", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
            if (!yes)
            {
                PostExtensionResult(core, false, string.Empty);
                return;
            }

            PostExtensionBusy(core, true, "Downloading uBlock Origin...");
            string tempFile = Path.Combine(Path.GetTempPath(), "MintBrowserExt_" + Guid.NewGuid().ToString("N") + ".crx");
            string? tempRoot = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                (Uri url, string version) = await ResolveUBlockPackageAsync(cts.Token);
                AppLog.Info($"Downloading uBlock Origin {version} from {url}");
                await DownloadExtensionPackageAsync(url, tempFile, cts.Token);
                PostExtensionBusy(core, true, "Installing uBlock Origin...");
                (string manifestRoot, string root) = await Task.Run(() => StageExtension(tempFile));
                tempRoot = root;
                await InstallStagedExtensionAsync(core, manifestRoot);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Adding uBlock Origin failed", ex);
                string reason = ex is OperationCanceledException ? "the download took too long" : ex.Message;
                PostExtensionResult(core, false, "Could not add uBlock Origin: " + reason);
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch (Exception ex) { AppLog.Debug("Could not delete the downloaded package", ex); }
                if (tempRoot != null) ExtensionTryDelete(tempRoot);
                await PostExtensionsListAsync(core);
            }
        }

        private static async Task<(Uri Url, string Version)> ResolveUBlockPackageAsync(CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, UBlockLatestReleaseApi);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using HttpResponseMessage response = await ExtensionHttp.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                using JsonDocument release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                string tag = ManifestString(release.RootElement, "tag_name") ?? string.Empty;
                if (release.RootElement.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        string? name = ManifestString(asset, "name");
                        string? link = ManifestString(asset, "browser_download_url");
                        if (name == null || link == null) continue;
                        if (!name.EndsWith(".chromium.crx", StringComparison.OrdinalIgnoreCase)) continue;
                        if (Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps &&
                            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                        {
                            return (uri, tag);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warn("Could not look up the latest uBlock Origin release - using the pinned release", ex);
            }
            return (new Uri(UBlockFallbackUrl), UBlockFallbackVersion);
        }

        private static async Task DownloadExtensionPackageAsync(Uri url, string destination, CancellationToken ct)
        {
            using HttpResponseMessage response = await ExtensionHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // GitHub redirects release files to its own download servers; accept nothing else.
            Uri? finalUri = response.RequestMessage?.RequestUri;
            bool trustedHost = finalUri != null && finalUri.Scheme == Uri.UriSchemeHttps &&
                (string.Equals(finalUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                 finalUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
            if (!trustedHost) throw new InvalidDataException("The download was redirected to an unexpected address.");

            if (response.Content.Headers.ContentLength is long declared && declared > MaxExtensionPackageBytes)
            {
                throw new InvalidDataException("The extension package is unexpectedly large.");
            }

            await CopyCappedAsync(response, destination, ct);
        }

        /// <summary>
        /// Turns a .crx, .zip or manifest.json into a temporary folder that holds the unpacked extension and returns the folder
        /// that contains manifest.json together with the temporary root (to delete afterwards). Runs on a background thread. Nothing is trusted: paths are checked and sizes are capped.
        /// </summary>
        private static (string ManifestRoot, string TempRoot) StageExtension(string sourcePath)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "MintBrowserExt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (extension is ".crx" or ".zip")
                {
                    FileInfo info = new(sourcePath);
                    if (info.Length > MaxExtensionPackageBytes) throw new InvalidDataException("The extension package is unexpectedly large.");
                    byte[] data = File.ReadAllBytes(sourcePath);
                    int offset = extension == ".crx" ? GetCrxPayloadOffset(data) : 0;
                    using var zipStream = new MemoryStream(data, offset, data.Length - offset, writable: false);
                    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                    ExtractZipSafely(archive, tempRoot);
                }
                else if (string.Equals(Path.GetFileName(sourcePath), "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    string? sourceFolder = Path.GetDirectoryName(sourcePath);
                    if (string.IsNullOrEmpty(sourceFolder)) throw new InvalidDataException("The manifest.json folder could not be read.");
                    int copied = 0;
                    ExtensionCopyDirectory(sourceFolder, tempRoot, ref copied);
                }
                else
                {
                    throw new InvalidDataException("Choose a .crx or .zip package, or the manifest.json of an unpacked extension.");
                }
                return (FindManifestRoot(tempRoot), tempRoot);
            }
            catch
            {
                ExtensionTryDelete(tempRoot);
                throw;
            }
        }

        /// <summary>A .crx is a ZIP with a header in front. Returns where the ZIP data starts.</summary>
        private static int GetCrxPayloadOffset(byte[] data)
        {
            if (data.Length < 16 || data[0] != (byte)'C' || data[1] != (byte)'r' || data[2] != (byte)'2' || data[3] != (byte)'4')
            {
                throw new InvalidDataException("This is not a valid .crx file.");
            }
            uint version = BitConverter.ToUInt32(data, 4);
            long offset;
            if (version == 3)
            {
                offset = 12L + BitConverter.ToUInt32(data, 8);
            }
            else if (version == 2)
            {
                offset = 16L + BitConverter.ToUInt32(data, 8) + BitConverter.ToUInt32(data, 12);
            }
            else
            {
                throw new InvalidDataException("This .crx format version is not supported.");
            }
            if (offset <= 0 || offset >= data.Length) throw new InvalidDataException("This .crx file is damaged.");
            return (int)offset;
        }

        private static void ExtractZipSafely(ZipArchive archive, string destinationRoot)
        {
            string rootFull = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long total = 0;
            int files = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // a folder entry
                if (++files > MaxExtensionFiles) throw new InvalidDataException("The package contains too many files.");
                total += entry.Length;
                if (total > MaxExtensionUnpackedBytes) throw new InvalidDataException("The package is unexpectedly large once unpacked.");

                string target = Path.GetFullPath(Path.Combine(rootFull, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The package contains an unsafe file path.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
            if (files == 0) throw new InvalidDataException("The package is empty.");
        }

        private static void ExtensionCopyDirectory(string source, string destination, ref int copied)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                if (++copied > MaxExtensionFiles) throw new InvalidDataException("The folder contains too many files.");
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }
            foreach (string directory in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(directory);
                if (name.StartsWith('.')) continue; // .git and similar
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue; // never follow links
                ExtensionCopyDirectory(directory, Path.Combine(destination, name), ref copied);
            }
        }

        /// <summary>manifest.json sits at the top of the package, or inside the one folder that wraps everything.</summary>
        private static string FindManifestRoot(string unpackedRoot)
        {
            if (File.Exists(Path.Combine(unpackedRoot, "manifest.json"))) return unpackedRoot;
            string[] children = Directory.GetDirectories(unpackedRoot);
            if (children.Length == 1 && File.Exists(Path.Combine(children[0], "manifest.json"))) return children[0];
            throw new InvalidDataException("No manifest.json was found, so this is not a browser extension.");
        }

        private static async Task InstallStagedExtensionAsync(CoreWebView2 core, string stagedFolder)
        {
            ManifestInfo manifest = ReadManifest(stagedFolder);
            if (!manifest.IsValid)
            {
                throw new InvalidDataException("The manifest.json of this package is missing a name, a version or a manifest version.");
            }

            string slug = ExtensionSlug(manifest.Name);
            string target = Path.Combine(UserExtensionsRoot, slug);
            string targetFull = Path.GetFullPath(target);

            // The same extension twice would run twice: refuse when a copy from another folder (for example the built-in one) is already loaded.
            foreach (CoreWebView2BrowserExtension existing in await core.Profile.GetBrowserExtensionsAsync())
            {
                if (!string.Equals(existing.Name, manifest.Name, StringComparison.OrdinalIgnoreCase)) continue;
                bool sameFolder = ExtensionFolders.TryGetValue(existing.Id, out ManagedExtension? known) &&
                                  string.Equals(Path.GetFullPath(known.Folder), targetFull, StringComparison.OrdinalIgnoreCase);
                if (sameFolder) continue; // an update of an extension the user added earlier
                PostExtensionResult(core, false,
                    $"{manifest.Name} is already installed" + (known?.BuiltIn == true ? " (built-in). Turn that copy on instead." : "."));
                return;
            }

            Directory.CreateDirectory(UserExtensionsRoot);
            bool createdNew = !Directory.Exists(target);
            try
            {
                if (!createdNew) Directory.Delete(target, recursive: true);
                int copied = 0;
                ExtensionCopyDirectory(stagedFolder, target, ref copied);
                CoreWebView2BrowserExtension added = await core.Profile.AddBrowserExtensionAsync(target);
                ExtensionFolders[added.Id] = new ManagedExtension("user:" + slug, target, BuiltIn: false);
                SetExtensionDisabled("user:" + slug, disabled: false);
                AppLog.Info($"Extension added: {added.Name} {manifest.Version} ({target})");
                string versionText = string.IsNullOrEmpty(manifest.Version) ? string.Empty : " " + manifest.Version;
                PostExtensionResult(core, true, $"{added.Name}{versionText} was added and is on.");
            }
            catch (Exception ex)
            {
                // Do not leave a half-installed folder that would be loaded (and fail) at every start.
                RemoveExtensionFolder(target);
                string hint = ex is System.Runtime.InteropServices.COMException
                    ? " The browser engine could not load it; it may use a manifest version this browser does not support."
                    : string.Empty;
                throw new InvalidOperationException(ex.Message + hint, ex);
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string ExtensionSlug(string name)
        {
            var sb = new System.Text.StringBuilder();
            bool lastDash = true;
            foreach (char c in name.ToLowerInvariant())
            {
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                {
                    sb.Append(c);
                    lastDash = false;
                }
                else if (!lastDash)
                {
                    sb.Append('-');
                    lastDash = true;
                }
                if (sb.Length >= 48) break;
            }
            string slug = sb.ToString().Trim('-');
            return slug.Length == 0 ? "extension" : slug;
        }

        private static void ExtensionTryDelete(string folder)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not delete a temporary extension folder", ex);
            }
        }

        /// <summary>Deletes an installed extension's folder. When a file is still in use the folder is marked and removed at the next start.</summary>
        private static void RemoveExtensionFolder(string folder)
        {
            try
            {
                // Only ever delete inside the user's own extensions folder, never a built-in or arbitrary path.
                string rootFull = Path.GetFullPath(UserExtensionsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string folderFull = Path.GetFullPath(folder);
                if (!folderFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return;
                if (!Directory.Exists(folderFull)) return;
                try
                {
                    Directory.Delete(folderFull, recursive: true);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Extension folder is still in use - it will be deleted at the next start", ex);
                    File.WriteAllText(Path.Combine(folderFull, ExtensionRemovedMarker), string.Empty);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not delete an extension folder", ex);
            }
        }
    }
}
