namespace TronIPTV.Core;

/// <summary>Streaming parser for extended M3U / M3U8 IPTV playlists.</summary>
public static class M3uParser
{
    private static readonly string[] VodExtensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".flv", ".webm"];

    public static PlaylistContent Parse(string text) => Parse(new StringReader(text));

    public static PlaylistContent Parse(TextReader reader)
    {
        var content = new PlaylistContent();
        Dictionary<string, string>? attrs = null;
        string? title = null;
        string? extGroup = null;
        string? userAgent = null;
        string? referrer = null;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                var header = ParseAttributes(line.AsSpan(7), out _);
                foreach (var key in new[] { "url-tvg", "x-tvg-url", "tvg-url" })
                {
                    if (header.TryGetValue(key, out var urls))
                        content.EpgUrls.AddRange(urls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                attrs = ParseAttributes(line.AsSpan(8), out title);
                extGroup = null;
                userAgent = null;
                referrer = null;
            }
            else if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                extGroup = line[8..].Trim();
            }
            else if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
            {
                var option = line[11..].Trim();
                var eq = option.IndexOf('=');
                if (eq > 0)
                {
                    var name = option[..eq].Trim().ToLowerInvariant();
                    var value = option[(eq + 1)..].Trim();
                    if (name == "http-user-agent")
                        userAgent = value;
                    else if (name is "http-referrer" or "http-referer")
                        referrer = value;
                }
            }
            else if (line[0] == '#')
            {
                // Unsupported directive (#KODIPROP, #EXTBYT, ...).
            }
            else
            {
                var url = line;
                attrs ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                attrs.TryGetValue("tvg-name", out var tvgName);
                var name = !string.IsNullOrWhiteSpace(title) ? title.Trim()
                    : !string.IsNullOrWhiteSpace(tvgName) ? tvgName.Trim()
                    : url;
                attrs.TryGetValue("group-title", out var group);
                group = !string.IsNullOrWhiteSpace(group) ? group.Trim() : extGroup ?? "";
                attrs.TryGetValue("tvg-id", out var tvgId);
                attrs.TryGetValue("tvg-logo", out var logo);
                int? number = null;
                if (attrs.TryGetValue("tvg-chno", out var chno) && int.TryParse(chno, out var n))
                    number = n;

                content.Channels.Add(new Channel
                {
                    Key = "m3u:" + url,
                    Name = name,
                    Group = group,
                    LogoUrl = string.IsNullOrWhiteSpace(logo) ? null : logo.Trim(),
                    StreamUrl = url,
                    TvgId = string.IsNullOrWhiteSpace(tvgId) ? null : tvgId.Trim(),
                    TvgName = tvgName,
                    Kind = DetectKind(url),
                    Number = number,
                    UserAgent = userAgent ?? (attrs.TryGetValue("user-agent", out var ua) ? ua : null),
                    Referrer = referrer,
                });

                attrs = null;
                title = null;
                extGroup = null;
                userAgent = null;
                referrer = null;
            }
        }

        return content;
    }

    internal static ContentKind DetectKind(string url)
    {
        if (url.Contains("/movie/", StringComparison.OrdinalIgnoreCase))
            return ContentKind.Movie;
        if (url.Contains("/series/", StringComparison.OrdinalIgnoreCase))
            return ContentKind.Episode;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath;
            if (VodExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                return ContentKind.Movie;
        }
        return ContentKind.Live;
    }

    /// <summary>
    /// Parses <c>-1 key="value" key2=value2,Title</c>. The title starts after the first comma outside quotes.
    /// </summary>
    internal static Dictionary<string, string> ParseAttributes(ReadOnlySpan<char> s, out string? title)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        title = null;
        var i = 0;
        // Skip the duration token.
        while (i < s.Length && s[i] != ' ' && s[i] != ',' && s[i] != '\t')
            i++;

        while (i < s.Length)
        {
            var c = s[i];
            if (c == ',')
            {
                title = s[(i + 1)..].ToString().Trim();
                return result;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var keyStart = i;
            while (i < s.Length && s[i] != '=' && s[i] != ',' && !char.IsWhiteSpace(s[i]))
                i++;
            var key = s[keyStart..i].ToString();
            if (i >= s.Length || s[i] != '=')
                continue;
            i++; // '='

            string value;
            if (i < s.Length && (s[i] == '"' || s[i] == '\''))
            {
                var quote = s[i++];
                var valueStart = i;
                while (i < s.Length && s[i] != quote)
                    i++;
                value = s[valueStart..i].ToString();
                if (i < s.Length)
                    i++; // closing quote
            }
            else
            {
                var valueStart = i;
                while (i < s.Length && s[i] != ',' && !char.IsWhiteSpace(s[i]))
                    i++;
                value = s[valueStart..i].ToString();
            }
            if (key.Length > 0)
                result[key] = value;
        }

        return result;
    }
}
