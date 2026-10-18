using CommunityToolkit.Mvvm.ComponentModel;
using TronIPTV.Core;

namespace TronIPTV.ViewModels;

public enum BrowseTab
{
    Live,
    Movies,
    Series,
    Favorites,
    Recent,
}

public sealed partial class ChannelItem(Channel channel, Guid sourceId, bool canFavorite) : ObservableObject
{
    public Channel Channel { get; } = channel;
    public Guid SourceId { get; } = sourceId;
    public bool CanFavorite { get; } = canFavorite;
    public string FavoriteKey => $"{SourceId}|{Channel.Key}";

    public string Name => Channel.Name;
    public string? LogoUrl => Channel.LogoUrl;
    public string Initial => Channel.Name.Trim() is { Length: > 0 } n ? char.ToUpperInvariant(n[0]).ToString() : "?";
    public string? NumberText => Channel.Kind == ContentKind.Episode ? (Channel.Number is { } e ? $"E{e}" : null) : Channel.Number?.ToString();
    public bool IsSeries => Channel.Kind == ContentKind.Series;
    public bool IsLive => Channel.Kind == ContentKind.Live;

    /// <summary>Provider rating (usually 0–10), hidden when missing or zero.</summary>
    public string? RatingText =>
        double.TryParse(Channel.Rating, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0
            ? r.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)
            : null;

    public string KindLabel => Channel.Kind switch
    {
        ContentKind.Live => "Live",
        ContentKind.Movie => "Movie",
        ContentKind.Series => "Series",
        _ => "Episode",
    };

    /// <summary>Secondary line: the current EPG programme for live channels, otherwise the category.</summary>
    [ObservableProperty] private string? _subtitle;
    [ObservableProperty] private string? _remainingText;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isSelected;

    public void UpdateGuide(EpgData epg, DateTimeOffset now)
    {
        if (Channel.Kind != ContentKind.Live)
        {
            Subtitle = Channel.Group;
            RemainingText = null;
            Progress = 0;
            return;
        }
        var (current, _) = epg.NowNext(Channel, now);
        if (current == null)
        {
            Subtitle = Channel.Group;
            RemainingText = null;
            Progress = 0;
            return;
        }
        Subtitle = current.Title;
        var left = (int)Math.Ceiling((current.Stop - now).TotalMinutes);
        RemainingText = left >= 60 ? $"{left / 60}h {left % 60:00}m" : $"{left}m";
        var total = (current.Stop - current.Start).TotalSeconds;
        Progress = total > 0 ? Math.Clamp((now - current.Start).TotalSeconds / total, 0, 1) : 0;
    }
}

public sealed record GroupItem(string Name, int Count, bool IsAll)
{
    public string Display => IsAll ? "All" : Name;
    public string CountText => Count.ToString("N0");
}

public sealed record ProgrammeItem(Programme Programme, bool IsNow, bool IsPast)
{
    public string Time => $"{Programme.Start.ToLocalTime():HH:mm}";
    public string Day => Programme.Start.ToLocalTime().Date == DateTime.Today ? "" : Programme.Start.ToLocalTime().ToString("ddd");
    public string Title => Programme.Title;
    public string? Description => Programme.Description;
}
