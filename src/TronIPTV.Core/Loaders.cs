using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace TronIPTV.Core;

public static class AppPaths
{
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TronIPTV");

    public static string CacheDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TronIPTV", "cache");
}

public sealed record CachedFile(string Path, bool IsStale);

/// <summary>Downloads remote files into a per-URL cache, falling back to a stale copy when the server is unreachable.</summary>
public static class CachedDownloader
{
    public static string CachePathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];
        return Path.Combine(AppPaths.CacheDir, hash + ".bin");
    }

    public static async Task<CachedFile> GetAsync(string url, string? userAgent, TimeSpan maxAge, bool force, CancellationToken ct)
    {
        var path = CachePathFor(url);
        var info = new FileInfo(path);
        if (!force && info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < maxAge)
            return new CachedFile(path, false);

        Directory.CreateDirectory(AppPaths.CacheDir);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var response = await IptvHttp.SendAsync(url, userAgent, ct).ConfigureAwait(false))
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
            return new CachedFile(path, false);
        }
        catch (IptvException) when (info.Exists && !ct.IsCancellationRequested)
        {
            return new CachedFile(path, true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static void Evict(string url) => TryDelete(CachePathFor(url));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Opens a cached file, transparently decompressing gzip by content rather than by URL.</summary>
    public static Stream OpenMaybeGzip(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Span<byte> magic = stackalloc byte[2];
        var read = file.Read(magic);
        file.Position = 0;
        return read == 2 && magic[0] == 0x1f && magic[1] == 0x8b
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
    }
}

public sealed record PlaylistLoadResult(PlaylistContent Content, DateTimeOffset? SavedAt, bool FromLibrary, bool RefreshFailed, string? RefreshError);

public static class PlaylistLoader
{
    /// <summary>
    /// Returns the saved library when one exists for the current source settings; otherwise (or when
    /// <paramref name="refresh"/> is set) downloads it and saves it. A failed refresh falls back to the saved library.
    /// </summary>
    public static async Task<PlaylistLoadResult> LoadAsync(PlaylistSource source, bool refresh, CancellationToken ct)
    {
        // Local files are cheap to re-read and are the user's own source of truth.
        if (source.Kind == SourceKind.M3uFile)
            return new PlaylistLoadResult(ApplyEpgOverride(source, await ReadFileAsync(source, ct).ConfigureAwait(false)), null, false, false, null);

        var saved = await Task.Run(() => SnapshotStore.TryLoad(source), ct).ConfigureAwait(false);
        if (saved != null && !refresh)
            return new PlaylistLoadResult(ApplyEpgOverride(source, SnapshotStore.ToContent(saved)), saved.SavedAt, true, false, null);

        PlaylistContent content;
        try
        {
            content = await DownloadAsync(source, ct).ConfigureAwait(false);
        }
        catch (IptvException e) when (saved != null && !ct.IsCancellationRequested)
        {
            return new PlaylistLoadResult(ApplyEpgOverride(source, SnapshotStore.ToContent(saved)), saved.SavedAt, true, true, e.Message);
        }

        var snapshot = await Task.Run(() => SnapshotStore.Save(source, content), ct).ConfigureAwait(false);
        return new PlaylistLoadResult(ApplyEpgOverride(source, content), snapshot.SavedAt, false, false, null);
    }

    private static async Task<PlaylistContent> DownloadAsync(PlaylistSource source, CancellationToken ct)
    {
        switch (source.Kind)
        {
            case SourceKind.M3uUrl:
                // Always fetch fresh; the raw download is discarded once parsed because the library snapshot replaces it.
                var file = await CachedDownloader.GetAsync(source.Url, source.UserAgent, TimeSpan.Zero, force: true, ct).ConfigureAwait(false);
                try
                {
                    var content = await Task.Run(() => ParseFile(file.Path), ct).ConfigureAwait(false);
                    if (content.Channels.Count == 0)
                        throw new IptvException("The playlist downloaded but contains no channels. Check the URL.");
                    return content;
                }
                finally
                {
                    CachedDownloader.Evict(source.Url);
                }

            case SourceKind.Xtream:
                return await CreateXtream(source).LoadAsync(ct).ConfigureAwait(false);

            default:
                throw new IptvException($"Unsupported source type {source.Kind}.");
        }
    }

    private static async Task<PlaylistContent> ReadFileAsync(PlaylistSource source, CancellationToken ct)
    {
        if (!File.Exists(source.Url))
            throw new IptvException($"Playlist file not found: {source.Url}");
        return await Task.Run(() => ParseFile(source.Url), ct).ConfigureAwait(false);
    }

    private static PlaylistContent ParseFile(string path)
    {
        using var stream = CachedDownloader.OpenMaybeGzip(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return M3uParser.Parse(reader);
    }

    private static PlaylistContent ApplyEpgOverride(PlaylistSource source, PlaylistContent content)
    {
        if (!string.IsNullOrWhiteSpace(source.EpgUrl))
        {
            content.EpgUrls.Clear();
            content.EpgUrls.Add(source.EpgUrl.Trim());
        }
        return content;
    }

    public static XtreamClient CreateXtream(PlaylistSource source) =>
        new(source.Url, source.Username ?? "", source.Password, source.UserAgent);

    /// <summary>Removes everything cached for a source: its library snapshot and downloaded guides.</summary>
    public static void EvictCache(PlaylistSource source, IEnumerable<string> epgUrls)
    {
        SnapshotStore.Delete(source.Id);
        if (source.Kind == SourceKind.M3uUrl)
            CachedDownloader.Evict(source.Url);
        foreach (var url in epgUrls)
            CachedDownloader.Evict(url);
    }
}

public static class EpgLoader
{
    public static readonly TimeSpan EpgMaxAge = TimeSpan.FromHours(12);

    public static async Task<(EpgData Data, List<string> Warnings)> LoadAsync(IReadOnlyList<string> urls, IEnumerable<Channel> channels, string? userAgent, bool force, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in channels)
        {
            if (c.Kind != ContentKind.Live)
                continue;
            if (!string.IsNullOrWhiteSpace(c.TvgId))
                ids.Add(c.TvgId);
            names.Add(EpgNames.Normalize(c.TvgName ?? c.Name));
            names.Add(EpgNames.Normalize(c.Name));
        }

        bool Want(string id, IReadOnlyList<string> displayNames) =>
            ids.Contains(id) || names.Contains(EpgNames.Normalize(id)) || displayNames.Any(n => names.Contains(EpgNames.Normalize(n)));

        var now = DateTimeOffset.Now;
        var merged = new EpgData();
        var warnings = new List<string>();
        foreach (var url in urls.Distinct())
        {
            try
            {
                var file = await CachedDownloader.GetAsync(url, userAgent, EpgMaxAge, force, ct).ConfigureAwait(false);
                if (file.IsStale)
                    warnings.Add($"EPG from {IptvHttp.SafeHost(url)} is offline; showing cached guide.");
                var data = await Task.Run(() =>
                {
                    using var stream = CachedDownloader.OpenMaybeGzip(file.Path);
                    return XmltvParser.Parse(stream, now.AddHours(-6), now.AddDays(3), Want, ct);
                }, ct).ConfigureAwait(false);
                merged.Merge(data);
            }
            catch (Exception e) when (e is IptvException or System.Xml.XmlException or IOException or InvalidDataException)
            {
                warnings.Add($"EPG from {IptvHttp.SafeHost(url)} failed: {e.Message}");
            }
        }
        return (merged, warnings);
    }
}
