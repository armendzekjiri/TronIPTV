using System.IO.Compression;
using System.Text;
using TronIPTV.Core;

namespace TronIPTV.Core.Tests;

public class M3uParserTests
{
    [Fact]
    public void ParsesAttributesGroupsOptionsAndHeaderEpg()
    {
        const string playlist = """
            #EXTM3U url-tvg="http://epg.example/guide.xml.gz" x-tvg-url="http://b.example/a.xml,http://b.example/b.xml"
            #EXTINF:-1 tvg-id="bbc1.uk" tvg-name="BBC One" tvg-logo="http://logo/bbc1.png" group-title="UK, News" tvg-chno="101",BBC One HD
            #EXTVLCOPT:http-user-agent=MyAgent/1.0
            #EXTVLCOPT:http-referrer=http://ref.example/
            http://stream.example/live/1.ts
            #EXTINF:0,No Attributes, With Comma
            #EXTGRP:Sports
            http://stream.example/live/2.m3u8

            #EXTINF:-1 group-title="Movies",Some Film
            http://stream.example/movie/u/p/55.mkv
            #EXTINF:-1 group-title=Series,Show S01E01
            http://stream.example/series/u/p/77.mp4
            """;

        var content = M3uParser.Parse(playlist);

        Assert.Equal(["http://epg.example/guide.xml.gz", "http://b.example/a.xml", "http://b.example/b.xml"], content.EpgUrls);
        Assert.Equal(4, content.Channels.Count);

        var bbc = content.Channels[0];
        Assert.Equal("BBC One HD", bbc.Name);
        Assert.Equal("UK, News", bbc.Group);
        Assert.Equal("bbc1.uk", bbc.TvgId);
        Assert.Equal("http://logo/bbc1.png", bbc.LogoUrl);
        Assert.Equal(101, bbc.Number);
        Assert.Equal("MyAgent/1.0", bbc.UserAgent);
        Assert.Equal("http://ref.example/", bbc.Referrer);
        Assert.Equal(ContentKind.Live, bbc.Kind);

        var second = content.Channels[1];
        Assert.Equal("No Attributes, With Comma", second.Name);
        Assert.Equal("Sports", second.Group);
        Assert.Null(second.UserAgent);

        Assert.Equal(ContentKind.Movie, content.Channels[2].Kind);
        Assert.Equal("Series", content.Channels[3].Group);
        Assert.Equal(ContentKind.Episode, content.Channels[3].Kind);
    }

    [Fact]
    public void UrlWithoutExtinfUsesUrlAsName()
    {
        var content = M3uParser.Parse("http://a.example/x.ts\n");
        Assert.Equal("http://a.example/x.ts", Assert.Single(content.Channels).Name);
    }
}

public class XmltvParserTests
{
    private const string Guide = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE tv SYSTEM "xmltv.dtd">
        <tv>
          <channel id="bbc1.uk"><display-name lang="en">BBC One</display-name></channel>
          <channel id="other"><display-name>Unwanted</display-name></channel>
          <programme start="20261018120000 +0200" stop="20261018130000 +0200" channel="bbc1.uk">
            <title lang="en">Midday News</title><desc>Headlines.</desc><category>News</category>
          </programme>
          <programme start="20261018130000 +0200" stop="20261018143000 +0200" channel="bbc1.uk">
            <title>Afternoon Film</title>
          </programme>
          <programme start="20261018120000 +0200" stop="20261018130000 +0200" channel="other">
            <title>Filtered</title>
          </programme>
          <programme start="20201018120000 +0000" stop="20201018130000 +0000" channel="bbc1.uk">
            <title>Too Old</title>
          </programme>
        </tv>
        """;

    private static readonly DateTimeOffset Noon = new(2026, 10, 18, 12, 30, 0, TimeSpan.FromHours(2));

    [Fact]
    public void ParsesFiltersAndFindsNowNext()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Guide));
        var data = XmltvParser.Parse(stream, Noon.AddHours(-6), Noon.AddDays(1), (id, _) => id == "bbc1.uk");

        Assert.Equal(1, data.ChannelCount);
        Assert.Equal(2, data.ProgrammeCount);

        var channel = new Channel { Key = "k", Name = "BBC One", TvgId = "bbc1.uk" };
        var (now, next) = data.NowNext(channel, Noon);
        Assert.Equal("Midday News", now?.Title);
        Assert.Equal("Headlines.", now?.Description);
        Assert.Equal("News", now?.Category);
        Assert.Equal("Afternoon Film", next?.Title);
    }

    [Fact]
    public void MatchesByDisplayNameWhenTvgIdMissing()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Guide));
        var data = XmltvParser.Parse(stream, Noon.AddHours(-6), Noon.AddDays(1));

        var channel = new Channel { Key = "k2", Name = "UK: BBC One FHD" };
        Assert.Equal("Midday News", data.NowNext(channel, Noon).Now?.Title);
    }

    [Fact]
    public async Task ReadsGzipByContent()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "guide.bin");
            await using (var file = File.Create(path))
            await using (var gz = new GZipStream(file, CompressionLevel.Fastest))
                await gz.WriteAsync(Encoding.UTF8.GetBytes(Guide));

            using var stream = CachedDownloader.OpenMaybeGzip(path);
            var data = XmltvParser.Parse(stream, Noon.AddHours(-6), Noon.AddDays(1));
            Assert.Equal(2, data.ChannelCount);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Theory]
    [InlineData("20261018120000 +0200", "2026-10-18T10:00:00Z")]
    [InlineData("20261018120000 -0130", "2026-10-18T13:30:00Z")]
    [InlineData("20261018120000", "2026-10-18T12:00:00Z")]
    [InlineData("202610181200 +0000", "2026-10-18T12:00:00Z")]
    public void ParsesXmltvTimes(string input, string expectedUtc)
    {
        Assert.True(XmltvParser.TryParseTime(input, out var value));
        Assert.Equal(DateTimeOffset.Parse(expectedUtc), value.ToUniversalTime());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("20261399120000")]
    public void RejectsInvalidTimes(string? input) => Assert.False(XmltvParser.TryParseTime(input, out _));
}

public class XtreamTests
{
    [Theory]
    [InlineData("example.com:8080", "http://example.com:8080")]
    [InlineData("http://example.com:8080/", "http://example.com:8080")]
    [InlineData("http://example.com/get.php?username=a&password=b&type=m3u", "http://example.com")]
    [InlineData("https://example.com/player_api.php", "https://example.com")]
    [InlineData("http://example.com/cdn", "http://example.com/cdn")]
    public void NormalizesServer(string input, string expected) => Assert.Equal(expected, XtreamClient.NormalizeServer(input));
}

public class SettingsTests
{
    [Fact]
    public void RoundTripsAndProtectsSecrets()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new SettingsStore(Path.Combine(dir.FullName, "settings.json"));
            var source = new PlaylistSource { Name = "Test", Kind = SourceKind.Xtream, Username = "user" };
            source.Url = "http://example.com";
            source.Password = "s3cret";
            var settings = new AppSettings { Sources = [source], Favorites = ["a|b"] };

            store.Save(settings);
            store.Save(settings); // second save exercises the replace-with-backup path

            var json = File.ReadAllText(store.Path);
            Assert.DoesNotContain("s3cret", json);
            Assert.True(File.Exists(store.Path + ".bak"));

            var loaded = store.Load();
            var s = Assert.Single(loaded.Sources);
            Assert.Equal("s3cret", s.Password);
            Assert.Equal("http://example.com", s.Url);
            Assert.Equal(SourceKind.Xtream, s.Kind);
            Assert.Contains("a|b", loaded.Favorites);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
