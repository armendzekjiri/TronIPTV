using System.Globalization;
using System.Text.Json;

namespace TronIPTV.Core;

/// <summary>Client for the Xtream Codes compatible <c>player_api.php</c> API.</summary>
public sealed class XtreamClient
{
    private readonly string _server;
    private readonly string _username;
    private readonly string _password;
    private readonly string? _userAgent;
    private string _liveExtension = "ts";

    public XtreamClient(string server, string username, string password, string? userAgent)
    {
        _server = NormalizeServer(server);
        _username = username;
        _password = password;
        _userAgent = userAgent;
    }

    public static string NormalizeServer(string server)
    {
        server = server.Trim().TrimEnd('/');
        if (!server.Contains("://", StringComparison.Ordinal))
            server = "http://" + server;
        // Users often paste the full API or panel URL.
        foreach (var suffix in new[] { "/player_api.php", "/get.php", "/xmltv.php" })
        {
            var idx = server.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (idx > server.IndexOf("://", StringComparison.Ordinal) + 3)
            {
                server = server[..idx];
                break;
            }
        }
        return server.TrimEnd('/');
    }

    private string User => Uri.EscapeDataString(_username);
    private string Pass => Uri.EscapeDataString(_password);
    private string Api(string? action, string? extra = null) =>
        $"{_server}/player_api.php?username={User}&password={Pass}" + (action is null ? "" : $"&action={action}") + extra;

    public string EpgUrl => $"{_server}/xmltv.php?username={User}&password={Pass}";

    public async Task<PlaylistContent> LoadAsync(CancellationToken ct)
    {
        var content = new PlaylistContent();
        content.AccountInfo = await AuthenticateAsync(ct).ConfigureAwait(false);

        var liveCats = CategoriesAsync("get_live_categories", ct);
        var vodCats = CategoriesAsync("get_vod_categories", ct);
        var seriesCats = CategoriesAsync("get_series_categories", ct);
        var live = GetJsonAsync(Api("get_live_streams"), ct);
        var vod = GetJsonAsync(Api("get_vod_streams"), ct);
        var series = GetJsonAsync(Api("get_series"), ct);

        // Live is required; VOD and series are optional on many subscriptions.
        using (var liveDoc = await live.ConfigureAwait(false))
            AddLive(content, liveDoc.RootElement, await liveCats.ConfigureAwait(false));

        await AddOptionalAsync(vod, vodCats, (root, cats) => AddVod(content, root, cats)).ConfigureAwait(false);
        await AddOptionalAsync(series, seriesCats, (root, cats) => AddSeries(content, root, cats)).ConfigureAwait(false);

        content.EpgUrls.Add(EpgUrl);
        return content;
    }

    private static async Task AddOptionalAsync(Task<JsonDocument> docTask, Task<Dictionary<string, string>> catsTask, Action<JsonElement, Dictionary<string, string>> add)
    {
        Dictionary<string, string> cats;
        try { cats = await catsTask.ConfigureAwait(false); }
        catch (IptvException) { cats = []; }
        try
        {
            using var doc = await docTask.ConfigureAwait(false);
            add(doc.RootElement, cats);
        }
        catch (IptvException) { }
    }

    private async Task<string> AuthenticateAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(Api(null), ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("user_info", out var info) || info.ValueKind != JsonValueKind.Object)
            throw new IptvException("The server did not return Xtream account information. Check the server URL.");
        if (Str(info, "auth") == "0")
            throw new IptvException("Login failed: wrong username or password.");

        var status = Str(info, "status") ?? "Unknown";
        if (!status.Equals("Active", StringComparison.OrdinalIgnoreCase))
            throw new IptvException($"Account status is '{status}'.");

        if (info.TryGetProperty("allowed_output_formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            var list = formats.EnumerateArray().Select(f => f.GetString()).Where(f => f != null).ToList();
            if (list.Count > 0 && !list.Contains("ts"))
                _liveExtension = list.Contains("m3u8") ? "m3u8" : list[0]!;
        }

        var parts = new List<string> { $"Status: {status}" };
        if (long.TryParse(Str(info, "exp_date"), out var exp) && exp > 0)
            parts.Add("Expires: " + DateTimeOffset.FromUnixTimeSeconds(exp).ToLocalTime().ToString("d", CultureInfo.CurrentCulture));
        var max = Str(info, "max_connections");
        if (!string.IsNullOrEmpty(max))
            parts.Add($"Connections: {Str(info, "active_cons") ?? "0"}/{max}");
        return string.Join("  •  ", parts);
    }

    private async Task<Dictionary<string, string>> CategoriesAsync(string action, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(Api(action), ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var id = Str(c, "category_id");
                if (id != null)
                    map[id] = Str(c, "category_name") ?? id;
            }
        }
        return map;
    }

    private void AddLive(PlaylistContent content, JsonElement root, Dictionary<string, string> cats)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new IptvException("The server returned an invalid live stream list.");
        foreach (var s in root.EnumerateArray())
        {
            var id = Str(s, "stream_id");
            if (id == null)
                continue;
            content.Channels.Add(new Channel
            {
                Key = "xt:live:" + id,
                Name = Str(s, "name")?.Trim() ?? id,
                Group = Category(cats, s),
                LogoUrl = NullIfEmpty(Str(s, "stream_icon")),
                StreamUrl = $"{_server}/live/{User}/{Pass}/{id}.{_liveExtension}",
                TvgId = NullIfEmpty(Str(s, "epg_channel_id")),
                Kind = ContentKind.Live,
                Number = int.TryParse(Str(s, "num"), out var n) ? n : null,
                UserAgent = _userAgent,
            });
        }
    }

    private void AddVod(PlaylistContent content, JsonElement root, Dictionary<string, string> cats)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return;
        foreach (var s in root.EnumerateArray())
        {
            var id = Str(s, "stream_id");
            if (id == null)
                continue;
            var ext = NullIfEmpty(Str(s, "container_extension")) ?? "mp4";
            content.Channels.Add(new Channel
            {
                Key = "xt:vod:" + id,
                Name = Str(s, "name")?.Trim() ?? id,
                Group = Category(cats, s),
                LogoUrl = NullIfEmpty(Str(s, "stream_icon")),
                StreamUrl = $"{_server}/movie/{User}/{Pass}/{id}.{ext}",
                Kind = ContentKind.Movie,
                Rating = NullIfEmpty(Str(s, "rating")),
                UserAgent = _userAgent,
            });
        }
    }

    private static void AddSeries(PlaylistContent content, JsonElement root, Dictionary<string, string> cats)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return;
        foreach (var s in root.EnumerateArray())
        {
            var id = Str(s, "series_id");
            if (id == null)
                continue;
            content.Channels.Add(new Channel
            {
                Key = "xt:series:" + id,
                Name = Str(s, "name")?.Trim() ?? id,
                Group = Category(cats, s),
                LogoUrl = NullIfEmpty(Str(s, "cover")),
                Kind = ContentKind.Series,
                SeriesId = id,
                Plot = NullIfEmpty(Str(s, "plot")),
                Rating = NullIfEmpty(Str(s, "rating")),
            });
        }
    }

    public async Task<List<Channel>> GetEpisodesAsync(string seriesId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(Api("get_series_info", "&series_id=" + Uri.EscapeDataString(seriesId)), ct).ConfigureAwait(false);
        var result = new List<Channel>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("episodes", out var episodes))
            return result;

        IEnumerable<JsonElement> seasonLists = episodes.ValueKind switch
        {
            JsonValueKind.Object => episodes.EnumerateObject().Select(p => p.Value),
            JsonValueKind.Array => episodes.EnumerateArray(),
            _ => [],
        };

        foreach (var season in seasonLists)
        {
            var items = season.ValueKind == JsonValueKind.Array ? season.EnumerateArray().ToList() : [season];
            foreach (var e in items)
            {
                if (e.ValueKind != JsonValueKind.Object)
                    continue;
                var id = Str(e, "id");
                if (id == null)
                    continue;
                var seasonNum = Str(e, "season") ?? "?";
                var epNum = Str(e, "episode_num") ?? "?";
                var title = Str(e, "title")?.Trim();
                var ext = NullIfEmpty(Str(e, "container_extension")) ?? "mp4";
                string? plot = null, logo = null;
                if (e.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
                {
                    plot = NullIfEmpty(Str(info, "plot"));
                    logo = NullIfEmpty(Str(info, "movie_image"));
                }
                result.Add(new Channel
                {
                    Key = "xt:episode:" + id,
                    Name = string.IsNullOrEmpty(title) ? $"S{seasonNum}E{epNum}" : title,
                    Group = "Season " + seasonNum,
                    LogoUrl = logo,
                    StreamUrl = $"{_server}/series/{User}/{Pass}/{id}.{ext}",
                    Kind = ContentKind.Episode,
                    Number = int.TryParse(epNum, out var n) ? n : null,
                    Plot = plot,
                    UserAgent = _userAgent,
                });
            }
        }
        return result;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await IptvHttp.SendAsync(url, _userAgent, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            throw new IptvException($"{IptvHttp.SafeHost(url)} returned a response that is not valid JSON.", e);
        }
    }

    private static string Category(Dictionary<string, string> cats, JsonElement s)
    {
        var id = Str(s, "category_id");
        return id != null && cats.TryGetValue(id, out var name) ? name : "Uncategorized";
    }

    /// <summary>Xtream panels mix strings, numbers, and nulls for the same field.</summary>
    internal static string? Str(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null,
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
