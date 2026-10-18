using System.Globalization;
using System.Text;
using System.Xml;

namespace TronIPTV.Core;

/// <summary>Programme guide indexed by XMLTV channel id, with name-based fallback matching.</summary>
public sealed class EpgData
{
    private readonly Dictionary<string, List<Programme>> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nameToId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Programme>?> _resolved = new(StringComparer.Ordinal);

    public static EpgData Empty { get; } = new();

    public int ChannelCount => _byId.Count;
    public int ProgrammeCount => _byId.Values.Sum(l => l.Count);

    internal void AddChannelName(string id, string displayName)
    {
        var key = EpgNames.Normalize(displayName);
        if (key.Length > 0)
            _nameToId.TryAdd(key, id);
    }

    internal void Add(string channelId, Programme p)
    {
        if (!_byId.TryGetValue(channelId, out var list))
            _byId[channelId] = list = [];
        list.Add(p);
    }

    internal void Seal()
    {
        foreach (var list in _byId.Values)
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public void Merge(EpgData other)
    {
        foreach (var (id, list) in other._byId)
        {
            if (!_byId.TryGetValue(id, out var existing))
                _byId[id] = list;
            else if (existing.Count == 0)
                _byId[id] = list;
        }
        foreach (var (name, id) in other._nameToId)
            _nameToId.TryAdd(name, id);
        _resolved.Clear();
    }

    public IReadOnlyList<Programme> GetSchedule(Channel channel)
    {
        if (!_resolved.TryGetValue(channel.Key, out var list))
        {
            list = Resolve(channel);
            _resolved[channel.Key] = list;
        }
        return list ?? (IReadOnlyList<Programme>)[];
    }

    private List<Programme>? Resolve(Channel channel)
    {
        if (channel.TvgId is { } id && _byId.TryGetValue(id, out var byId))
            return byId;
        foreach (var name in new[] { channel.TvgId, channel.TvgName, channel.Name })
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (_nameToId.TryGetValue(EpgNames.Normalize(name), out var mapped) && _byId.TryGetValue(mapped, out var byName))
                return byName;
        }
        return null;
    }

    public (Programme? Now, Programme? Next) NowNext(Channel channel, DateTimeOffset time)
    {
        var list = GetSchedule(channel);
        if (list.Count == 0)
            return (null, null);
        // Binary search for the last programme starting at or before `time`.
        int lo = 0, hi = list.Count - 1, idx = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid].Start <= time) { idx = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        var now = idx >= 0 && list[idx].IsAiringAt(time) ? list[idx] : null;
        var next = idx + 1 < list.Count ? list[idx + 1] : null;
        return (now, next);
    }
}

public static class EpgNames
{
    private static readonly string[] QualitySuffixes = ["uhd", "fhd", "hd", "sd", "4k", "hevc"];

    /// <summary>Lower-case alphanumerics without short country prefixes ("UK: ", "DE | ") or quality suffixes.</summary>
    public static string Normalize(string name)
    {
        var s = name.Trim();
        var sep = s.IndexOfAny([':', '|']);
        if (sep is > 0 and <= 4)
            s = s[(sep + 1)..];
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        var result = sb.ToString();
        foreach (var suffix in QualitySuffixes)
        {
            if (result.Length > suffix.Length + 1 && result.EndsWith(suffix, StringComparison.Ordinal))
            {
                result = result[..^suffix.Length];
                break;
            }
        }
        return result;
    }
}

public static class XmltvParser
{
    /// <summary>
    /// Streams an XMLTV document, keeping programmes that overlap [from, to] for channels accepted by <paramref name="wantChannel"/>.
    /// </summary>
    public static EpgData Parse(Stream stream, DateTimeOffset from, DateTimeOffset to, Func<string, IReadOnlyList<string>, bool>? wantChannel = null, CancellationToken ct = default)
    {
        var data = new EpgData();
        var decisions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CheckCharacters = false,
        };

        using var reader = XmlReader.Create(stream, settings);
        var count = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (reader.Name == "channel")
            {
                var id = reader.GetAttribute("id");
                var names = new List<string>();
                using (var sub = reader.ReadSubtree())
                {
                    while (sub.Read())
                    {
                        if (sub.NodeType == XmlNodeType.Element && sub.Name == "display-name")
                        {
                            var text = ReadText(sub);
                            if (!string.IsNullOrWhiteSpace(text))
                                names.Add(text.Trim());
                        }
                    }
                }
                if (string.IsNullOrEmpty(id))
                    continue;
                var wanted = wantChannel?.Invoke(id, names) ?? true;
                decisions[id] = wanted;
                if (wanted)
                {
                    foreach (var n in names)
                        data.AddChannelName(id, n);
                    data.AddChannelName(id, id);
                }
            }
            else if (reader.Name == "programme")
            {
                if ((++count & 0x3FFF) == 0)
                    ct.ThrowIfCancellationRequested();

                var channel = reader.GetAttribute("channel");
                DateTimeOffset start = default, stop = default;
                var keep = false;
                if (channel != null)
                {
                    if (!decisions.TryGetValue(channel, out var wanted))
                        decisions[channel] = wanted = wantChannel?.Invoke(channel, []) ?? true;
                    keep = wanted
                        && TryParseTime(reader.GetAttribute("start"), out start)
                        && TryParseTime(reader.GetAttribute("stop"), out stop)
                        && stop > start && stop >= from && start <= to;
                }

                using var sub = reader.ReadSubtree();
                if (!keep)
                    continue;

                string? title = null, desc = null, category = null;
                sub.Read();
                while (sub.Read())
                {
                    if (sub.NodeType != XmlNodeType.Element || sub.Depth != 1)
                        continue;
                    switch (sub.Name)
                    {
                        case "title": title ??= ReadText(sub); break;
                        case "desc": desc ??= ReadText(sub); break;
                        case "category": category ??= ReadText(sub); break;
                    }
                }
                data.Add(channel!, new Programme(start, stop, title?.Trim() ?? "", desc?.Trim(), category?.Trim()));
            }
        }

        data.Seal();
        return data;
    }

    /// <summary>Reads text content without advancing past the element, so the caller's Read loop stays valid.</summary>
    private static string ReadText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
            return "";
        var depth = reader.Depth;
        var sb = new StringBuilder();
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                sb.Append(reader.Value);
        }
        return sb.ToString();
    }

    /// <summary>Parses XMLTV times such as <c>20231018120000 +0200</c>. A missing offset means UTC.</summary>
    public static bool TryParseTime(string? s, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        s = s.Trim();
        var digits = 0;
        while (digits < s.Length && digits < 14 && char.IsAsciiDigit(s[digits]))
            digits++;
        if (digits < 12)
            return false;
        var stamp = s[..digits].PadRight(14, '0');
        if (!DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return false;

        var rest = s[digits..].Trim();
        var offset = TimeSpan.Zero;
        if (rest.Length >= 5 && (rest[0] == '+' || rest[0] == '-')
            && int.TryParse(rest.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            && int.TryParse(rest.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m))
        {
            offset = new TimeSpan(h, m, 0);
            if (rest[0] == '-')
                offset = -offset;
        }
        try
        {
            value = new DateTimeOffset(local, offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
