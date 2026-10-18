using TronIPTV.Core;

namespace TronIPTV.Core.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    private readonly string _previousCache = AppPaths.CacheDir;

    public LibraryTests() => AppPaths.CacheDir = _dir.FullName;

    public void Dispose()
    {
        AppPaths.CacheDir = _previousCache;
        _dir.Delete(true);
    }

    // Port 1 refuses connections, so any network access fails fast.
    private static PlaylistSource DeadSource()
    {
        var source = new PlaylistSource { Name = "Dead", Kind = SourceKind.M3uUrl };
        source.Url = "http://127.0.0.1:1/playlist.m3u";
        return source;
    }

    private static PlaylistContent Sample()
    {
        var content = M3uParser.Parse("""
            #EXTM3U url-tvg="http://epg.example/g.xml"
            #EXTINF:-1 tvg-id="a" group-title="News",Channel A
            http://stream.example/live/u/secret-password/1.ts
            """);
        content.AccountInfo = "Status: Active";
        return content;
    }

    [Fact]
    public void SnapshotRoundTripsEncrypted()
    {
        var source = DeadSource();
        SnapshotStore.Save(source, Sample());

        var raw = File.ReadAllBytes(SnapshotStore.PathFor(source.Id));
        Assert.DoesNotContain("secret-password", System.Text.Encoding.UTF8.GetString(raw));

        var loaded = SnapshotStore.TryLoad(source);
        Assert.NotNull(loaded);
        var channel = Assert.Single(loaded.Channels);
        Assert.Equal("Channel A", channel.Name);
        Assert.Equal("a", channel.TvgId);
        Assert.Equal("http://stream.example/live/u/secret-password/1.ts", channel.StreamUrl);
        Assert.Equal(["http://epg.example/g.xml"], loaded.EpgUrls);
        Assert.Equal("Status: Active", loaded.AccountInfo);
    }

    [Fact]
    public void EditingSourceInvalidatesSnapshot()
    {
        var source = DeadSource();
        SnapshotStore.Save(source, Sample());
        source.Url = "http://127.0.0.1:1/other.m3u";
        Assert.Null(SnapshotStore.TryLoad(source));
    }

    [Fact]
    public async Task StartupUsesSavedLibraryWithoutNetwork()
    {
        var source = DeadSource();
        SnapshotStore.Save(source, Sample());

        var result = await PlaylistLoader.LoadAsync(source, refresh: false, CancellationToken.None);

        Assert.True(result.FromLibrary);
        Assert.False(result.RefreshFailed);
        Assert.Single(result.Content.Channels);
        Assert.NotNull(result.SavedAt);
    }

    [Fact]
    public async Task FailedRefreshKeepsSavedLibrary()
    {
        var source = DeadSource();
        SnapshotStore.Save(source, Sample());

        var result = await PlaylistLoader.LoadAsync(source, refresh: true, CancellationToken.None);

        Assert.True(result.RefreshFailed);
        Assert.NotNull(result.RefreshError);
        Assert.Single(result.Content.Channels);
    }

    [Fact]
    public async Task FirstLoadWithoutLibraryReportsError()
    {
        await Assert.ThrowsAsync<IptvException>(() => PlaylistLoader.LoadAsync(DeadSource(), refresh: false, CancellationToken.None));
    }
}
