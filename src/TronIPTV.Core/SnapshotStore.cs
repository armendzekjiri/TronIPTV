using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TronIPTV.Core;

/// <summary>The last successfully loaded library of a source, reused until the user refreshes.</summary>
public sealed class PlaylistSnapshot
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>Hash of the settings that produced this library; edits to the source invalidate it.</summary>
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }
    public string? AccountInfo { get; set; }
    public List<string> EpgUrls { get; set; } = [];
    public List<Channel> Channels { get; set; } = [];
}

/// <summary>
/// Persists playlist libraries as gzip-compressed JSON, DPAPI-encrypted for the current user because
/// Xtream and many M3U stream URLs embed account credentials.
/// </summary>
public static class SnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Dir => Path.Combine(AppPaths.CacheDir, "library");

    public static string PathFor(Guid sourceId) => Path.Combine(Dir, sourceId.ToString("N") + ".bin");

    public static string Fingerprint(PlaylistSource source)
    {
        var text = $"{source.Kind}\n{source.Url}\n{source.Username}\n{source.Password}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Returns the saved library, or null when missing, unreadable, outdated, or from different source settings.</summary>
    public static PlaylistSnapshot? TryLoad(PlaylistSource source)
    {
        var path = PathFor(source.Id);
        if (!File.Exists(path))
            return null;
        try
        {
            var compressed = SecretProtector.UnprotectBytes(File.ReadAllBytes(path));
            if (compressed == null)
                return null;
            using var gzip = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
            var snapshot = JsonSerializer.Deserialize<PlaylistSnapshot>(gzip, Options);
            if (snapshot == null || snapshot.Version != PlaylistSnapshot.CurrentVersion || snapshot.Fingerprint != Fingerprint(source))
                return null;
            return snapshot;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static PlaylistSnapshot Save(PlaylistSource source, PlaylistContent content)
    {
        var snapshot = new PlaylistSnapshot
        {
            Fingerprint = Fingerprint(source),
            SavedAt = DateTimeOffset.Now,
            AccountInfo = content.AccountInfo,
            EpgUrls = [.. content.EpgUrls],
            Channels = content.Channels,
        };

        var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            JsonSerializer.Serialize(gzip, snapshot, Options);
        var protectedBytes = SecretProtector.ProtectBytes(buffer.ToArray());

        Directory.CreateDirectory(Dir);
        var path = PathFor(source.Id);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, path, overwrite: true);
        return snapshot;
    }

    public static void Delete(Guid sourceId)
    {
        try { File.Delete(PathFor(sourceId)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static PlaylistContent ToContent(PlaylistSnapshot snapshot)
    {
        var content = new PlaylistContent { AccountInfo = snapshot.AccountInfo };
        content.Channels.AddRange(snapshot.Channels);
        content.EpgUrls.AddRange(snapshot.EpgUrls);
        return content;
    }
}
