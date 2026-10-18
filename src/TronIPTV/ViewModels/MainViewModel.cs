using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TronIPTV.Core;

namespace TronIPTV.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxRecent = 60;

    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _guideTimer;
    private readonly DispatcherTimer _searchDebounce;
    private CancellationTokenSource? _loadCts;
    private List<ChannelItem> _all = [];
    private Dictionary<string, ChannelItem> _byKey = [];
    private List<ChannelItem> _episodes = [];
    private List<GroupItem> _allGroups = [];
    private List<string> _epgUrls = [];
    private EpgData _epg = EpgData.Empty;
    private bool _suppressFilter;
    private bool _autoPlayPending;

    /// <summary>Shows the add/edit dialog. Set by the view; returns null when cancelled.</summary>
    public Func<PlaylistSource?, PlaylistSource?>? EditSourceDialog { get; set; }
    /// <summary>Asks the user to confirm. Set by the view.</summary>
    public Func<string, bool>? Confirm { get; set; }

    public PlayerViewModel Player { get; }
    public ObservableCollection<PlaylistSource> Sources { get; }

    [ObservableProperty] private PlaylistSource? _selectedSource;
    [ObservableProperty] private BrowseTab _tab = BrowseTab.Live;
    [ObservableProperty] private IReadOnlyList<GroupItem> _groups = [];
    [ObservableProperty] private GroupItem? _selectedGroup;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private IReadOnlyList<ChannelItem> _items = [];
    [ObservableProperty] private ChannelItem? _selectedItem;
    [ObservableProperty] private ChannelItem? _playingItem;
    [ObservableProperty] private ChannelItem? _openSeries;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string? _accountInfo;
    [ObservableProperty] private string? _guideStatus;
    /// <summary>When the saved library was last downloaded; it is only re-downloaded on Refresh.</summary>
    [ObservableProperty] private DateTimeOffset? _librarySavedAt;
    public string? LibraryText => LibrarySavedAt is { } at ? "Library updated " + Ago(at) : null;

    partial void OnLibrarySavedAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(LibraryText));

    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.Now - at;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 2 ? "yesterday"
            : $"{(int)span.TotalDays} days ago";
    }

    // Details panel for the selected (or playing) item.
    [ObservableProperty] private ChannelItem? _detailItem;
    [ObservableProperty] private string? _nowText;
    [ObservableProperty] private string? _nowDescription;
    [ObservableProperty] private string? _nextText;
    [ObservableProperty] private IReadOnlyList<ProgrammeItem> _schedule = [];

    /// <summary>Filter for the category list; independent of the item search.</summary>
    [ObservableProperty] private string _categorySearch = "";
    [ObservableProperty] private int _liveCount;
    [ObservableProperty] private int _movieCount;
    [ObservableProperty] private int _seriesCount;
    [ObservableProperty] private int _favoriteCount;
    [ObservableProperty] private int _recentCount;

    public bool HasSources => Sources.Count > 0;
    public bool ShowEmptyList => HasSources && !IsBusy && Items.Count == 0;

    /// <summary>Movies and series browse as a poster grid; channels, episodes and mixed lists as rows.</summary>
    public bool UsePosterGrid => OpenSeries == null && Tab is BrowseTab.Movies or BrowseTab.Series;

    public string TabTitle => OpenSeries?.Name ?? Tab switch
    {
        BrowseTab.Live => "Live TV",
        BrowseTab.Movies => "Movies",
        BrowseTab.Series => "Series",
        BrowseTab.Favorites => "Favorites",
        _ => "Recently watched",
    };

    private string ItemNoun => OpenSeries != null ? "episodes" : Tab switch
    {
        BrowseTab.Live => "channels",
        BrowseTab.Movies => "movies",
        BrowseTab.Series => "series",
        _ => "items",
    };

    public string CategoryNoun => OpenSeries != null ? "Seasons" : "Categories";

    public string ItemSearchHint => SelectedGroup is { IsAll: false } g
        ? $"Search {ItemNoun} in {g.Name}"
        : $"Search all {ItemNoun}";

    public string ItemCountText => $"{Items.Count:N0} {ItemNoun}";

    public bool DetailIsLive => DetailItem?.Channel.Kind == ContentKind.Live;
    public bool DetailIsVod => DetailItem != null && DetailItem.Channel.Kind != ContentKind.Live;

    partial void OnItemsChanged(IReadOnlyList<ChannelItem> value)
    {
        OnPropertyChanged(nameof(ShowEmptyList));
        OnPropertyChanged(nameof(ItemCountText));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyList));

    partial void OnOpenSeriesChanged(ChannelItem? value) => NotifyViewShape();

    partial void OnDetailItemChanged(ChannelItem? value)
    {
        OnPropertyChanged(nameof(DetailIsLive));
        OnPropertyChanged(nameof(DetailIsVod));
    }

    partial void OnCategorySearchChanged(string value) => ApplyCategoryFilter();

    private void NotifyViewShape()
    {
        OnPropertyChanged(nameof(UsePosterGrid));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(CategoryNoun));
        OnPropertyChanged(nameof(ItemSearchHint));
        OnPropertyChanged(nameof(ItemCountText));
    }

    private void UpdateCounts()
    {
        LiveCount = _all.Count(i => i.Channel.Kind == ContentKind.Live);
        MovieCount = _all.Count(i => i.Channel.Kind == ContentKind.Movie);
        SeriesCount = _all.Count(i => i.Channel.Kind is ContentKind.Series or ContentKind.Episode);
        FavoriteCount = _all.Count(i => i.IsFavorite);
        RecentCount = RecentItems().Count();
    }

    public MainViewModel(Dispatcher dispatcher)
    {
        _settings = _store.Load();
        Player = new PlayerViewModel(dispatcher) { Volume = Math.Clamp(_settings.Volume, 0, 100) };
        Player.MediaEnded += (_, _) => PlayNextEpisode();
        Sources = new ObservableCollection<PlaylistSource>(_settings.Sources);
        Sources.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSources));
            OnPropertyChanged(nameof(ShowEmptyList));
        };

        _guideTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => RefreshGuide(), dispatcher);
        _searchDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _searchDebounce!.Stop();
            ApplyFilter();
        }, dispatcher);
        _searchDebounce.Stop();
    }

    public void Initialize()
    {
        _guideTimer.Start();
        _autoPlayPending = _settings.AutoPlayLastChannel;
        SelectedSource = Sources.FirstOrDefault(s => s.Id == _settings.LastSourceId) ?? Sources.FirstOrDefault();
    }

    partial void OnSelectedSourceChanged(PlaylistSource? value)
    {
        _settings.LastSourceId = value?.Id;
        Save();
        _ = LoadSourceAsync(force: false);
    }

    [RelayCommand]
    private Task Refresh() => LoadSourceAsync(force: true);

    private async Task LoadSourceAsync(bool force)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        var source = SelectedSource;

        OpenSeries = null;
        _all = [];
        _byKey = [];
        UpdateCounts();
        _epg = EpgData.Empty;
        AccountInfo = null;
        GuideStatus = null;
        LibrarySavedAt = null;
        RebuildGroups();
        if (source == null)
        {
            StatusText = "Add a playlist to get started.";
            return;
        }

        IsBusy = true;
        BusyText = force ? $"Updating {source.Name} from the provider…" : $"Opening {source.Name}…";
        StatusText = null;
        PlaylistLoadResult result;
        try
        {
            result = await PlaylistLoader.LoadAsync(source, force, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (IptvException e)
        {
            if (!ct.IsCancellationRequested)
            {
                IsBusy = false;
                StatusText = e.Message;
            }
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!ct.IsCancellationRequested)
            {
                IsBusy = false;
                StatusText = "Could not read playlist: " + e.Message;
            }
            return;
        }
        if (ct.IsCancellationRequested)
            return;
        var content = result.Content;
        LibrarySavedAt = result.SavedAt;

        var favorites = _settings.Favorites;
        _all = content.Channels.Select(c => new ChannelItem(c, source.Id, canFavorite: true)
        {
            IsFavorite = favorites.Contains($"{source.Id}|{c.Key}"),
        }).ToList();
        _byKey = new Dictionary<string, ChannelItem>(StringComparer.Ordinal);
        foreach (var item in _all)
            _byKey.TryAdd(item.Channel.Key, item);
        _epgUrls = content.EpgUrls;
        // Account status (expiry, connections) is only live right after a download.
        AccountInfo = result.FromLibrary && content.AccountInfo != null ? content.AccountInfo + "  (at last update)" : content.AccountInfo;

        if (_all.All(i => i.Channel.Kind != ContentKind.Live) && Tab == BrowseTab.Live)
            Tab = _all.Any(i => i.Channel.Kind == ContentKind.Movie) ? BrowseTab.Movies : BrowseTab.Series;
        RebuildGroups();

        UpdateCounts();
        StatusText = result.RefreshFailed ? $"Update failed, keeping the saved library: {result.RefreshError}" : null;
        IsBusy = false;

        if (_autoPlayPending)
        {
            _autoPlayPending = false;
            if (_settings.LastChannelKey is { } key && _byKey.TryGetValue(key, out var last) && last.Channel.StreamUrl != null)
            {
                SelectedItem = last;
                Play(last);
            }
        }
        else if (PlayingItem != null && _byKey.TryGetValue(PlayingItem.Channel.Key, out var same) && same.SourceId == PlayingItem.SourceId)
        {
            MarkPlaying(same);
        }

        await LoadGuideAsync(source, force, ct);
    }

    private async Task LoadGuideAsync(PlaylistSource source, bool force, CancellationToken ct)
    {
        if (_epgUrls.Count == 0 || _all.All(i => i.Channel.Kind != ContentKind.Live))
        {
            GuideStatus = _epgUrls.Count == 0 ? "No TV guide configured" : null;
            return;
        }
        GuideStatus = "Loading TV guide…";
        try
        {
            var channels = _all.Select(i => i.Channel).ToList();
            var (epg, warnings) = await EpgLoader.LoadAsync(_epgUrls, channels, source.UserAgent, force, ct);
            if (ct.IsCancellationRequested)
                return;
            _epg = epg;
            GuideStatus = warnings.Count > 0 ? string.Join("  ", warnings)
                : epg.ChannelCount == 0 ? "TV guide has no matching channels"
                : $"Guide: {epg.ChannelCount:N0} channels";
            RefreshGuide();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void RefreshGuide()
    {
        var now = DateTimeOffset.Now;
        // Only the current list is refreshed; other items are refreshed when they are next filtered in.
        foreach (var item in Items)
            item.UpdateGuide(_epg, now);
        OnPropertyChanged(nameof(LibraryText));
        UpdateDetails();
    }

    partial void OnTabChanged(BrowseTab value)
    {
        OpenSeries = null;
        _suppressFilter = true;
        SearchText = "";
        CategorySearch = "";
        _suppressFilter = false;
        _searchDebounce.Stop();
        NotifyViewShape();
        RebuildGroups();
    }

    partial void OnSelectedGroupChanged(GroupItem? value)
    {
        OnPropertyChanged(nameof(ItemSearchHint));
        if (!_suppressFilter)
            ApplyFilter();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressFilter)
            return;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnSelectedItemChanged(ChannelItem? oldValue, ChannelItem? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;
        if (newValue != null)
            newValue.IsSelected = true;
        UpdateDetails();
    }

    [RelayCommand]
    private void SelectItem(ChannelItem? item)
    {
        if (item != null)
            SelectedItem = item;
    }

    private IEnumerable<ChannelItem> TabItems()
    {
        if (OpenSeries != null)
            return _episodes;
        return Tab switch
        {
            BrowseTab.Live => _all.Where(i => i.Channel.Kind == ContentKind.Live),
            BrowseTab.Movies => _all.Where(i => i.Channel.Kind == ContentKind.Movie),
            BrowseTab.Series => _all.Where(i => i.Channel.Kind is ContentKind.Series or ContentKind.Episode),
            BrowseTab.Favorites => _all.Where(i => i.IsFavorite),
            BrowseTab.Recent => RecentItems(),
            _ => [],
        };
    }

    private IEnumerable<ChannelItem> RecentItems()
    {
        var prefix = SelectedSource?.Id + "|";
        foreach (var key in _settings.Recent)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && _byKey.TryGetValue(key[prefix.Length..], out var item))
                yield return item;
        }
    }

    private void RebuildGroups()
    {
        var previous = SelectedGroup?.Name;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        var total = 0;
        foreach (var item in TabItems())
        {
            total++;
            var g = string.IsNullOrEmpty(item.Channel.Group) ? "Uncategorized" : item.Channel.Group;
            if (counts.TryGetValue(g, out var c))
                counts[g] = c + 1;
            else
            {
                counts[g] = 1;
                order.Add(g);
            }
        }

        var groups = new List<GroupItem>(order.Count + 1) { new("All", total, true) };
        groups.AddRange(order.Select(g => new GroupItem(g, counts[g], false)));
        _allGroups = groups;

        _suppressFilter = true;
        ApplyCategoryFilter();
        SelectedGroup = groups.FirstOrDefault(g => !g.IsAll && g.Name == previous) ?? groups[0];
        _suppressFilter = false;
        ApplyFilter();
    }

    /// <summary>Filters the category list only. "All" stays visible so the selection can always be reset.</summary>
    private void ApplyCategoryFilter()
    {
        var terms = CategorySearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Groups = terms.Length == 0
            ? _allGroups
            : _allGroups.Where(g => g.IsAll || g == SelectedGroup || terms.All(t => g.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase))).ToList();
    }

    private void ApplyFilter()
    {
        IEnumerable<ChannelItem> query = TabItems();
        if (SelectedGroup is { IsAll: false } group)
            query = query.Where(i => (string.IsNullOrEmpty(i.Channel.Group) ? "Uncategorized" : i.Channel.Group) == group.Name);

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length > 0)
            query = query.Where(i => terms.All(t => i.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase)));

        var list = query.ToList();
        var now = DateTimeOffset.Now;
        foreach (var item in list)
            item.UpdateGuide(_epg, now);
        var selected = SelectedItem;
        Items = list;
        if (selected != null && list.Contains(selected))
            SelectedItem = selected;
    }

    [RelayCommand]
    private async Task Open(ChannelItem? item)
    {
        if (item == null)
            return;
        if (item.IsSeries)
        {
            await OpenSeriesAsync(item);
            return;
        }
        Play(item);
    }

    private async Task OpenSeriesAsync(ChannelItem series)
    {
        var source = SelectedSource;
        if (source?.Kind != SourceKind.Xtream || series.Channel.SeriesId is not { } seriesId)
            return;
        var ct = _loadCts?.Token ?? CancellationToken.None;
        IsBusy = true;
        BusyText = $"Loading episodes of {series.Name}…";
        try
        {
            var episodes = await PlaylistLoader.CreateXtream(source).GetEpisodesAsync(seriesId, ct);
            if (ct.IsCancellationRequested)
                return;
            _episodes = episodes.Select(e => new ChannelItem(e, source.Id, canFavorite: false)).ToList();
            if (_episodes.Count == 0)
            {
                StatusText = $"No episodes found for {series.Name}.";
                return;
            }
            OpenSeries = series;
            SearchText = "";
            RebuildGroups();
            SelectedItem = _episodes[0];
        }
        catch (IptvException e)
        {
            StatusText = e.Message;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CloseSeries()
    {
        var series = OpenSeries;
        OpenSeries = null;
        RebuildGroups();
        SelectedItem = series;
    }

    public void Play(ChannelItem item)
    {
        if (item.Channel.StreamUrl == null)
            return;
        Player.Play(item.Channel, _settings.NetworkCachingMs);
        MarkPlaying(item);

        _settings.LastChannelKey = item.Channel.Key;
        if (item.CanFavorite)
        {
            _settings.Recent.Remove(item.FavoriteKey);
            _settings.Recent.Insert(0, item.FavoriteKey);
            if (_settings.Recent.Count > MaxRecent)
                _settings.Recent.RemoveRange(MaxRecent, _settings.Recent.Count - MaxRecent);
        }
        Save();
        RecentCount = RecentItems().Count();
        UpdateDetails();
    }

    private void MarkPlaying(ChannelItem item)
    {
        if (PlayingItem != null)
            PlayingItem.IsPlaying = false;
        PlayingItem = item;
        item.IsPlaying = true;
    }

    [RelayCommand]
    private void StopPlayback()
    {
        Player.Stop();
        if (PlayingItem != null)
            PlayingItem.IsPlaying = false;
        PlayingItem = null;
        UpdateDetails();
    }

    [RelayCommand]
    private void ChannelUp() => Zap(-1);

    [RelayCommand]
    private void ChannelDown() => Zap(+1);

    private void Zap(int direction)
    {
        var current = PlayingItem ?? SelectedItem;
        var list = current != null && Items.Contains(current) ? Items
            : _all.Where(i => i.Channel.Kind == ContentKind.Live).ToList();
        var playable = list.Where(i => i.Channel.StreamUrl != null).ToList();
        if (playable.Count == 0)
            return;
        var index = current == null ? -1 : playable.IndexOf(current);
        var next = playable[((index < 0 ? (direction > 0 ? -1 : 0) : index) + direction + playable.Count) % playable.Count];
        SelectedItem = next;
        Play(next);
    }

    private void PlayNextEpisode()
    {
        if (PlayingItem?.Channel.Kind != ContentKind.Episode)
            return;
        var index = _episodes.IndexOf(PlayingItem);
        if (index >= 0 && index + 1 < _episodes.Count)
        {
            SelectedItem = _episodes[index + 1];
            Play(_episodes[index + 1]);
        }
    }

    [RelayCommand]
    private void ToggleFavorite(ChannelItem? item)
    {
        if (item is not { CanFavorite: true })
            return;
        item.IsFavorite = !item.IsFavorite;
        if (item.IsFavorite)
            _settings.Favorites.Add(item.FavoriteKey);
        else
            _settings.Favorites.Remove(item.FavoriteKey);
        Save();
        FavoriteCount = _all.Count(i => i.IsFavorite);
        if (Tab == BrowseTab.Favorites)
            RebuildGroups();
    }

    [RelayCommand]
    private void AddSource()
    {
        var created = EditSourceDialog?.Invoke(null);
        if (created == null)
            return;
        Sources.Add(created);
        SyncSources();
        SelectedSource = created;
    }

    [RelayCommand]
    private void EditSource()
    {
        var source = SelectedSource;
        if (source == null)
            return;
        var edited = EditSourceDialog?.Invoke(source);
        if (edited == null)
            return;
        var index = Sources.IndexOf(source);
        PlaylistLoader.EvictCache(source, _epgUrls);
        Sources[index] = edited;
        SyncSources();
        SelectedSource = edited;
    }

    [RelayCommand]
    private void RemoveSource()
    {
        var source = SelectedSource;
        if (source == null || Confirm?.Invoke($"Remove playlist \"{source.Name}\"? Its favorites and history will be deleted.") != true)
            return;
        if (PlayingItem?.SourceId == source.Id)
            StopPlayback();
        PlaylistLoader.EvictCache(source, _epgUrls);
        var prefix = source.Id + "|";
        _settings.Favorites.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
        _settings.Recent.RemoveAll(k => k.StartsWith(prefix, StringComparison.Ordinal));
        Sources.Remove(source);
        SyncSources();
        SelectedSource = Sources.FirstOrDefault();
    }

    private void SyncSources()
    {
        _settings.Sources = [.. Sources];
        Save();
    }

    private void UpdateDetails()
    {
        var item = SelectedItem ?? PlayingItem;
        DetailItem = item;
        NowText = NowDescription = NextText = null;
        Schedule = [];
        if (item == null)
            return;

        var channel = item.Channel;
        if (channel.Kind != ContentKind.Live)
        {
            NowText = channel.Group;
            NowDescription = channel.Plot ?? (item.IsSeries ? "Open the series to browse its seasons and episodes." : "No description available.");
            return;
        }

        var now = DateTimeOffset.Now;
        var schedule = _epg.GetSchedule(channel);
        if (schedule.Count == 0)
        {
            NowText = _epgUrls.Count == 0 ? "No TV guide for this playlist" : "No guide data for this channel";
            return;
        }
        var (current, next) = _epg.NowNext(channel, now);
        if (current != null)
        {
            var left = (int)Math.Ceiling((current.Stop - now).TotalMinutes);
            NowText = $"Now: {current.Title}  ({current.Start.ToLocalTime():HH:mm} – {current.Stop.ToLocalTime():HH:mm}, {left} min left)";
            NowDescription = current.Description;
        }
        if (next != null)
            NextText = $"Next: {next.Title} at {next.Start.ToLocalTime():HH:mm}";

        Schedule = schedule
            .Where(p => p.Stop > now.AddHours(-1))
            .Take(40)
            .Select(p => new ProgrammeItem(p, p.IsAiringAt(now), p.Stop <= now))
            .ToList();
    }

    private void Save()
    {
        _settings.Volume = Player.Volume;
        try
        {
            _store.Save(_settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = "Could not save settings: " + e.Message;
        }
    }

    public void Dispose()
    {
        _guideTimer.Stop();
        _loadCts?.Cancel();
        Save();
        Player.Dispose();
    }
}
