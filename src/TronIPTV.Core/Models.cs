using System.Text.Json.Serialization;

namespace TronIPTV.Core;

public enum SourceKind
{
    M3uUrl,
    M3uFile,
    Xtream,
}

public enum ContentKind
{
    Live,
    Movie,
    Series,
    Episode,
}

/// <summary>A configured playlist provider. Secrets are stored DPAPI-protected for the current Windows user.</summary>
public sealed class PlaylistSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public SourceKind Kind { get; set; }

    /// <summary>Protected M3U URL, M3U file path, or Xtream server URL.</summary>
    public string? ProtectedUrl { get; set; }
    public string? Username { get; set; }
    public string? ProtectedPassword { get; set; }

    /// <summary>Optional XMLTV URL. When empty, the playlist header or Xtream xmltv.php is used.</summary>
    public string? EpgUrl { get; set; }

    /// <summary>Optional User-Agent for playlist, EPG, and stream requests.</summary>
    public string? UserAgent { get; set; }

    [JsonIgnore]
    public string Url
    {
        get => SecretProtector.Unprotect(ProtectedUrl) ?? "";
        set => ProtectedUrl = SecretProtector.Protect(value);
    }

    [JsonIgnore]
    public string Password
    {
        get => SecretProtector.Unprotect(ProtectedPassword) ?? "";
        set => ProtectedPassword = SecretProtector.Protect(value);
    }

    public override string ToString() => Name;
}

public sealed class Channel
{
    /// <summary>Stable identity within its source, used for favorites and resume.</summary>
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string Group { get; init; } = "";
    public string? LogoUrl { get; init; }
    /// <summary>Playable URL. Null for series containers, whose episodes are fetched on demand.</summary>
    public string? StreamUrl { get; init; }
    public string? TvgId { get; init; }
    public string? TvgName { get; init; }
    public ContentKind Kind { get; init; }
    public int? Number { get; init; }
    public string? SeriesId { get; init; }
    public string? UserAgent { get; init; }
    public string? Referrer { get; init; }
    public string? Plot { get; init; }
    public string? Rating { get; init; }
}

public sealed record Programme(DateTimeOffset Start, DateTimeOffset Stop, string Title, string? Description, string? Category)
{
    public bool IsAiringAt(DateTimeOffset time) => Start <= time && time < Stop;
}

public sealed class PlaylistContent
{
    public List<Channel> Channels { get; } = [];
    /// <summary>EPG URLs advertised by the playlist (url-tvg / x-tvg-url) or the Xtream panel.</summary>
    public List<string> EpgUrls { get; } = [];
    public string? AccountInfo { get; set; }
}
