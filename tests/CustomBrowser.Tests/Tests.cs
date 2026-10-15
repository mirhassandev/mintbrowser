using CustomBrowser.Helpers;
using CustomBrowser.Services.Streams;
using CustomBrowser.Services.Ytdl;
using Xunit;

namespace CustomBrowser.Tests;

public class SiteDomainTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("www.example.com", "example.com")]
    [InlineData("a.b.example.com", "example.com")]
    [InlineData("www.example.co.uk", "example.co.uk")]
    [InlineData("user.github.io", "user.github.io")]
    [InlineData("www.user.github.io", "user.github.io")]
    [InlineData("192.168.1.1", "192.168.1.1")]
    [InlineData("localhost", "localhost")]
    public void Registrable_returns_the_site(string host, string expected) =>
        Assert.Equal(expected, SiteDomain.Registrable(host));

    [Theory]
    [InlineData("cdn.example.com", "www.example.com", true)]
    [InlineData("example.com", "example.com", true)]
    [InlineData("evil-example.com", "example.com", false)]
    [InlineData("example.com.evil.com", "example.com", false)]
    [InlineData("a.github.io", "b.github.io", false)]
    [InlineData("", "example.com", false)]
    public void IsSameSite_decides_cookie_scope(string host, string site, bool expected) =>
        Assert.Equal(expected, SiteDomain.IsSameSite(host, site));
}

public class HlsParserTests
{
    [Fact]
    public void Master_playlist_lists_each_quality()
    {
        const string text = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,FRAME-RATE=30\nhigh.m3u8\n";
        Assert.True(HlsParser.IsMaster(text));
        var m = HlsParser.ParseMaster(text, "https://cdn.example.com/v/master.m3u8");
        Assert.Equal(2, m.Tracks.Count);
        Assert.Equal(720, m.Tracks[1].Height);
        Assert.Equal("https://cdn.example.com/v/high.m3u8", m.Tracks[1].PlaylistUrl);
    }

    [Fact]
    public void Media_playlist_reads_segments_and_endlist()
    {
        const string text = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:5\n#EXTINF:4.0,\na.ts\n#EXTINF:4.0,\nb.ts\n#EXT-X-ENDLIST\n";
        var t = new TrackSpec();
        HlsParser.ParseMedia(text, "https://cdn.example.com/v/index.m3u8", t);
        Assert.Equal(2, t.Segments.Count);
        Assert.Equal(5, t.Segments[0].Sequence);
        Assert.Equal(8.0, t.DurationSeconds, 3);
        Assert.Equal(string.Empty, t.Error);
    }

    [Fact]
    public void Playlist_without_endlist_is_reported_as_live()
    {
        var t = new TrackSpec();
        HlsParser.ParseMedia("#EXTM3U\n#EXTINF:4.0,\na.ts\n", "https://x.com/i.m3u8", t);
        Assert.Contains("live", t.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sample_aes_is_reported_as_protected()
    {
        var t = new TrackSpec();
        HlsParser.ParseMedia("#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"k\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n", "https://x.com/i.m3u8", t);
        Assert.Contains("DRM", t.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://x.com/a/b.m3u8?token=1", ".m3u8")]
    [InlineData("https://x.com/a/b", "")]
    public void ExtensionOf_ignores_query(string url, string expected) =>
        Assert.Equal(expected, HlsParser.ExtensionOf(url));
}

public class DashParserTests
{
    [Theory]
    [InlineData("PT1H2M3S", 3723)]
    [InlineData("PT30S", 30)]
    [InlineData("P1DT0S", 86400)]
    [InlineData("", 0)]
    [InlineData("garbage", 0)]
    public void ParseDuration_handles_iso8601(string text, double seconds) =>
        Assert.Equal(seconds, DashParser.ParseDuration(text), 3);

    [Fact]
    public void Dtd_in_manifest_is_rejected_not_expanded()
    {
        const string evil = "<?xml version=\"1.0\"?><!DOCTYPE MPD [<!ENTITY x \"boom\">]><MPD>&x;</MPD>";
        var m = DashParser.Parse(evil, "https://x.com/a.mpd");
        Assert.False(string.IsNullOrEmpty(m.Error));
    }

    [Fact]
    public void Dynamic_manifest_is_reported_as_live()
    {
        var m = DashParser.Parse("<MPD type=\"dynamic\"></MPD>", "https://x.com/a.mpd");
        Assert.True(m.IsLive);
    }
}

public class YtdlStoreTests
{
    [Theory]
    [InlineData("clip.f137.mp4", "clip", true)]
    [InlineData("clip.mp4.part", "clip", true)]
    [InlineData("clip.mp4.ytdl", "clip", true)]
    [InlineData("clip.mp4", "clip", false)]
    [InlineData("clip.final.mp4", "clip", false)]
    [InlineData("other.mp4.part", "clip", false)]
    public void IsPartialFile_matches_only_leftovers(string file, string baseName, bool expected) =>
        Assert.Equal(expected, YtdlStore.IsPartialFile(file, baseName));
}



public class SearchUrlsTests
{
    private static readonly string[] Templates =
    {
        "https://www.google.com/search?q={0}",
        "https://www.bing.com/search?q={0}",
        "https://duckduckgo.com/?q={0}",
        "https://developer.mozilla.org/en-US/search?q={0}",
        "https://www.perplexity.ai/search?q={0}",
        "https://en.wikipedia.org/wiki/Special:Search?search={0}"
    };

    [Theory]
    [InlineData("https://www.google.com/search?q=cats", true)]
    [InlineData("https://duckduckgo.com/?q=cats", true)]
    [InlineData("https://developer.mozilla.org/en-US/search?q=fetch", true)]
    [InlineData("https://en.wikipedia.org/wiki/Special:Search?search=cats", true)]
    [InlineData("https://en.wikipedia.org/wiki/Cat", false)]            // an article is a site, not a search
    [InlineData("https://developer.mozilla.org/en-US/docs/Web/API", false)]
    [InlineData("https://www.google.com/", false)]
    [InlineData("", false)]
    public void Matches_only_search_pages(string url, bool expected) =>
        Assert.Equal(expected, SearchUrls.Matches(url, Templates));
}

public class SecretProtectorTests
{
    [Fact]
    public void Protect_then_Unprotect_returns_the_original()
    {
        string stored = SecretProtector.Protect("sid=abc123; token=xyz");
        Assert.StartsWith(SecretProtector.Prefix, stored);
        Assert.DoesNotContain("abc123", stored);
        Assert.Equal("sid=abc123; token=xyz", SecretProtector.Unprotect(stored));
    }

    [Fact]
    public void Old_unprotected_values_are_read_as_they_are() =>
        Assert.Equal("sid=old", SecretProtector.Unprotect("sid=old"));

    [Fact]
    public void Empty_stays_empty()
    {
        Assert.Equal(string.Empty, SecretProtector.Protect(""));
        Assert.Equal(string.Empty, SecretProtector.Unprotect(""));
    }

    [Fact]
    public void A_value_that_cannot_be_opened_gives_empty_not_an_exception() =>
        Assert.Equal(string.Empty, SecretProtector.Unprotect(SecretProtector.Prefix + "not-base64!!"));

    [Fact]
    public void Protecting_twice_does_not_wrap_twice()
    {
        string once = SecretProtector.Protect("a=b");
        Assert.Equal(once, SecretProtector.Protect(once));
    }
}

public class ClosedTabListTests
{
    [Fact]
    public void Newest_closed_tab_comes_back_first()
    {
        var list = new CustomBrowser.Services.ClosedTabList(null);
        list.Push("https://a.example/");
        list.Push("https://b.example/");
        Assert.Equal("https://b.example/", list.Pop());
        Assert.Equal("https://a.example/", list.Pop());
        Assert.Null(list.Pop());
    }

    [Fact]
    public void Same_address_is_kept_once_and_the_list_is_capped()
    {
        var list = new CustomBrowser.Services.ClosedTabList(null);
        list.Push("https://a.example/");
        list.Push("https://a.example/");
        Assert.Equal(1, list.Count);
        for (int i = 0; i < 25; i++) list.Push($"https://site{i}.example/");
        Assert.Equal(CustomBrowser.Services.ClosedTabList.MaxItems, list.Count);
        Assert.Equal("https://site24.example/", list.Pop());
    }

    [Fact]
    public void The_list_survives_a_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), "mint-closed-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new CustomBrowser.Services.ClosedTabList(path).Push("https://a.example/");
            var reopened = new CustomBrowser.Services.ClosedTabList(path);
            Assert.Equal("https://a.example/", reopened.Pop());
        }
        finally { File.Delete(path); }
    }
}

public class BrowserSettingsStoreTests
{
    [Fact]
    public void Search_engine_and_recent_searches_survive_a_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), "mint-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var first = new CustomBrowser.Services.BrowserSettingsStore(path);
            first.SetSearchEngine("DuckDuckGo");
            first.AddRecentSearch("  hello   world ");
            first.AddRecentSearch("x"); // too short to be worth remembering

            var second = new CustomBrowser.Services.BrowserSettingsStore(path);
            Assert.Equal("DuckDuckGo", second.SearchEngineName);
            Assert.Equal(new[] { "hello world" }, second.GetRecentSearches());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Changing_the_engine_raises_the_event_once_and_only_when_it_changed()
    {
        var store = new CustomBrowser.Services.BrowserSettingsStore(null);
        int raised = 0;
        store.SearchEngineChanged += () => raised++;
        store.SetSearchEngine("Bing");
        store.SetSearchEngine("Bing");
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Recent_searches_are_capped_and_repeat_searches_move_to_the_end()
    {
        var store = new CustomBrowser.Services.BrowserSettingsStore(null);
        for (int i = 0; i < 40; i++) store.AddRecentSearch("query " + i);
        store.AddRecentSearch("query 20");
        var list = store.GetRecentSearches();
        Assert.Equal(CustomBrowser.Services.BrowserSettingsStore.MaxRecentSearches, list.Count);
        Assert.Equal("query 20", list[^1]);
        Assert.Single(list, x => x == "query 20");
    }
}

public class HistoryAndDownloadStorageTests
{
    [Fact]
    public void A_title_change_alone_never_creates_a_history_entry()
    {
        using var db = CustomBrowser.Services.DatabaseService.CreateInMemory();
        db.UpdateTitle("https://mistyped.example/", "Server not found");
        Assert.Empty(db.GetHistoryList());
    }

    [Fact]
    public void A_title_change_updates_an_existing_entry()
    {
        using var db = CustomBrowser.Services.DatabaseService.CreateInMemory();
        db.AddHistory("Old title", "https://site.example/page");
        db.UpdateTitle("https://site.example/page", "New title");
        Assert.Equal("New title", Assert.Single(db.GetHistoryList()).Title);
    }

    [Fact]
    public void Download_cookies_are_stored_protected_and_read_back_plain()
    {
        using var db = CustomBrowser.Services.DatabaseService.CreateInMemory();
        var item = new CustomBrowser.Models.DownloadItem
        {
            Id = "dl1",
            Url = "https://files.example/a.zip",
            SavePath = @"C:\Temp\a.zip",
            CookieHeader = "sid=secret",
            Status = CustomBrowser.Models.DownloadStatus.Paused
        };
        db.SaveDownload(item);

        var stored = Assert.Single(db.GetAllDownloads());
        Assert.StartsWith(SecretProtector.Prefix, stored.CookieHeader);
        Assert.DoesNotContain("secret", stored.CookieHeader);
        Assert.Equal("sid=secret", SecretProtector.Unprotect(stored.CookieHeader));
    }

    [Fact]
    public void A_finished_download_keeps_no_cookie()
    {
        using var db = CustomBrowser.Services.DatabaseService.CreateInMemory();
        var item = new CustomBrowser.Models.DownloadItem
        {
            Id = "dl2",
            Url = "https://files.example/b.zip",
            SavePath = @"C:\Temp\b.zip",
            CookieHeader = "sid=secret",
            Status = CustomBrowser.Models.DownloadStatus.Completed
        };
        db.SaveDownload(item);
        Assert.Equal(string.Empty, Assert.Single(db.GetAllDownloads()).CookieHeader);
    }
}



public class ShortcutUrlsTests
{
    [Theory]
    [InlineData("github.com", "https://github.com/")]
    [InlineData("https://github.com/x", "https://github.com/x")]
    [InlineData("localhost:3000", "https://localhost:3000/")]
    [InlineData("http://intranet/", "http://intranet/")]        // single word + explicit scheme = a name on the own network
    [InlineData("intranet:8080", "https://intranet:8080/")]     // single word + port = same
    [InlineData("192.168.1.1", "https://192.168.1.1/")]
    public void Accepts_real_addresses(string input, string expected)
    {
        Assert.True(ShortcutUrls.TryNormalize(input, out string url, out string error), error);
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("", "Enter a web address")]
    [InlineData("my site.com", "can't contain spaces")]
    [InlineData("ftp://example.com", "Only http")]
    [InlineData("mailto:a@b.com", "Only http")]
    [InlineData("javascript:alert(1)", "Only http")]
    [InlineData("github", "isn't a full web address")]          // a bare word is almost always a typo
    public void Rejects_with_a_reason_that_says_what_is_wrong(string input, string expectedPart)
    {
        Assert.False(ShortcutUrls.TryNormalize(input, out string url, out string error));
        Assert.Equal(string.Empty, url);
        Assert.Contains(expectedPart, error);
    }
}

public class ErrorPageHtmlTests
{
    [Fact]
    public void Page_names_the_site_and_offers_to_try_again()
    {
        string html = ErrorPageHtml.Build("https://nope.example.com/page", ErrorPageKind.NameNotResolved);
        Assert.Contains("nope.example.com", html);
        Assert.Contains("Try again", html);
        Assert.Contains("href=\"https://nope.example.com/page\"", html);
        Assert.Contains("ERR_NAME_NOT_RESOLVED", html);
    }

    [Fact]
    public void Nothing_from_the_address_can_inject_markup_or_script()
    {
        string html = ErrorPageHtml.Build("https://x.example/?a=\"><script>alert(1)</script>", ErrorPageKind.CannotConnect);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"https://x.example/?a=\">", html);
    }

    [Theory]
    [InlineData(ErrorPageKind.NameNotResolved)]
    [InlineData(ErrorPageKind.CannotConnect)]
    [InlineData(ErrorPageKind.Timeout)]
    [InlineData(ErrorPageKind.ConnectionReset)]
    [InlineData(ErrorPageKind.Offline)]
    public void Every_kind_has_a_complete_page(ErrorPageKind kind)
    {
        string html = ErrorPageHtml.Build("https://site.example/", kind);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.EndsWith("</html>", html);
        Assert.Contains("<h1>", html);
        Assert.Contains(ErrorPageHtml.ErrorCode(kind), html);
    }
}

public class DownloadFeedbackFormattingTests
{
    private static void WithInvariantCulture(Action test)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { test(); }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Speed_uses_the_unit_that_fits() => WithInvariantCulture(() =>
    {
        Assert.Equal("-", CustomBrowser.Models.DownloadItem.FormatSpeed(0));
        Assert.Equal("512 B/s", CustomBrowser.Models.DownloadItem.FormatSpeed(512));
        Assert.Equal("850 KB/s", CustomBrowser.Models.DownloadItem.FormatSpeed(850 * 1024));
        Assert.Equal("12.4 MB/s", CustomBrowser.Models.DownloadItem.FormatSpeed(12.4 * 1024 * 1024));
    });

    [Theory]
    [InlineData(3, "A few seconds left")]
    [InlineData(42, "42 sec left")]
    [InlineData(7 * 60, "7 min left")]
    [InlineData(65 * 60, "1 hr 5 min left")]
    [InlineData(3 * 24 * 3600, "More than a day left")]
    public void Time_left_reads_naturally(int seconds, string expected) =>
        Assert.Equal(expected, CustomBrowser.Models.DownloadItem.FormatEta(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Time_left_is_only_known_while_downloading_with_a_known_size_and_speed()
    {
        var item = new CustomBrowser.Models.DownloadItem
        {
            Status = CustomBrowser.Models.DownloadStatus.Downloading,
            TotalBytes = 10_000,
            ReceivedBytes = 0,
            SpeedBytesPerSecond = 100
        };
        Assert.Equal(100, item.Eta!.Value.TotalSeconds, 3);
        Assert.Contains("min left", item.StatusText);

        item.Status = CustomBrowser.Models.DownloadStatus.Paused;
        Assert.Null(item.Eta);
        Assert.Equal(0, item.SpeedBytesPerSecond);
    }

    [Fact]
    public void A_missing_speed_is_not_shown_as_a_dash()
    {
        var item = new CustomBrowser.Models.DownloadItem
        {
            Status = CustomBrowser.Models.DownloadStatus.Downloading,
            TotalBytes = 10_000,
            ReceivedBytes = 500,
            Speed = "-"
        };
        Assert.DoesNotContain(" - -", item.StatusText);
    }
}

public class BlockedPopupTests
{
    [Fact]
    public void Each_address_is_remembered_once_and_the_list_is_capped()
    {
        var tab = new CustomBrowser.Models.BrowserTab();
        Assert.True(tab.AddBlockedPopup("https://ads.example/a"));
        Assert.False(tab.AddBlockedPopup("HTTPS://ADS.EXAMPLE/a"));
        for (int i = 0; i < 20; i++) tab.AddBlockedPopup($"https://ads{i}.example/");
        Assert.Equal(8, tab.BlockedPopups.Count);
    }

    [Fact]
    public void Blocked_pop_ups_can_be_removed_or_cleared()
    {
        var tab = new CustomBrowser.Models.BrowserTab();
        tab.AddBlockedPopup("https://a.example/");
        tab.AddBlockedPopup("https://b.example/");
        tab.RemoveBlockedPopup("https://a.example/");
        Assert.Equal(new[] { "https://b.example/" }, tab.BlockedPopups);
        tab.ClearBlockedPopups();
        Assert.Empty(tab.BlockedPopups);
    }
}

public class SidebarPreferenceTests
{
    [Fact]
    public void Sort_order_and_width_survive_a_restart_and_the_width_stays_in_range()
    {
        string path = Path.Combine(Path.GetTempPath(), "mint-sidebar-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var first = new CustomBrowser.Services.BrowserSettingsStore(path);
            first.SetHistorySort("MostVisited");
            first.SetSidebarWidth(5000);

            var second = new CustomBrowser.Services.BrowserSettingsStore(path);
            Assert.Equal("MostVisited", second.HistorySort);
            Assert.Equal(600, second.SidebarWidth);
        }
        finally { File.Delete(path); }
    }
}
