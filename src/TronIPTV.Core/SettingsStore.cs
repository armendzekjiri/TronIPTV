using System.Text.Json;
using System.Text.Json.Serialization;

namespace TronIPTV.Core;

public sealed class AppSettings
{
    public List<PlaylistSource> Sources { get; set; } = [];
    public Guid? LastSourceId { get; set; }
    public string? LastChannelKey { get; set; }
    /// <summary>Favorite keys, formatted as <c>{sourceId}|{channelKey}</c>.</summary>
    public HashSet<string> Favorites { get; set; } = [];
    /// <summary>Most-recent-first watch history, same key format as favorites.</summary>
    public List<string> Recent { get; set; } = [];
    public int Volume { get; set; } = 80;
    public int NetworkCachingMs { get; set; } = 1500;
    public bool AutoPlayLastChannel { get; set; } = true;
}

/// <summary>Loads and atomically saves settings JSON, keeping the previous file as a backup.</summary>
public sealed class SettingsStore(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.DataDir, "settings.json");

    public AppSettings Load()
    {
        foreach (var candidate in new[] { Path, Path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate))
                    continue;
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate), Options);
                if (settings != null)
                    return settings;
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                // Fall through to the backup copy.
            }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        if (File.Exists(Path))
            File.Replace(temp, Path, Path + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(temp, Path);
    }
}
