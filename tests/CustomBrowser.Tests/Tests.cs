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
