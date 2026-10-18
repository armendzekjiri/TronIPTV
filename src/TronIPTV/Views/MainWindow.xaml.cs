using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TronIPTV.ViewModels;

namespace TronIPTV.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _idleTimer;
    private bool _fullscreen;
    private WindowState _restoreState;
    private GridLength _restoreBrowser;
    private int _posterColumns;
    private const double PosterMinCardWidth = 150;
    private const double PosterCardMargin = 14;

    public static readonly DependencyProperty PosterWidthProperty =
        DependencyProperty.Register(nameof(PosterWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(156.0));
    public static readonly DependencyProperty PosterHeightProperty =
        DependencyProperty.Register(nameof(PosterHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(234.0));

    /// <summary>Card width that fills the row exactly; posters keep a 2:3 aspect.</summary>
    public double PosterWidth
    {
        get => (double)GetValue(PosterWidthProperty);
        set => SetValue(PosterWidthProperty, value);
    }

    public double PosterHeight
    {
        get => (double)GetValue(PosterHeightProperty);
        set => SetValue(PosterHeightProperty, value);
    }
    private GridLength _restoreDetails;
    private Point _lastMouse;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(Dispatcher)
        {
            EditSourceDialog = source => SourceDialog.Show(this, source),
            Confirm = message => MessageBox.Show(this, message, "TronIPTV", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes,
        };
        DataContext = _vm;
        Overlay.DataContext = _vm.Player;
        _vm.Player.PropertyChanged += Player_PropertyChanged;
        _vm.PropertyChanged += Vm_PropertyChanged;
        Player_PropertyChanged(null, new PropertyChangedEventArgs(nameof(PlayerViewModel.IsPlaying)));
        Player_PropertyChanged(null, new PropertyChangedEventArgs(nameof(PlayerViewModel.IsMuted)));

        _idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => HideControls(), Dispatcher);
        _idleTimer.Stop();

        PreviewKeyDown += Window_PreviewKeyDown;
        Loaded += (_, _) =>
        {
            // VideoView requires its MediaPlayer to be attached after it is loaded.
            VideoView.MediaPlayer = _vm.Player.MediaPlayer;
            _vm.Initialize();
            ChannelList.Focus();
        };
        Closed += (_, _) => _vm.Dispose();
    }

    private void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.IsPlaying):
                PlayPauseButton.Content = _vm.Player.IsPlaying ? "\uE769" : "\uE768";
                break;
            case nameof(PlayerViewModel.IsMuted):
            case nameof(PlayerViewModel.Volume):
                MuteButton.Content = _vm.Player.IsMuted || _vm.Player.Volume == 0 ? "\uE74F" : "\uE767";
                break;
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Items):
                RebuildPosterRows(scrollToTop: true);
                break;
            case nameof(MainViewModel.UsePosterGrid):
                ApplyBrowseLayout();
                RebuildPosterRows(scrollToTop: true);
                break;
        }
    }

    /// <summary>Posters get a wide browser; channel rows keep a fixed column so the player stays large.</summary>
    private void ApplyBrowseLayout()
    {
        if (_fullscreen)
            return;
        BrowserColumn.Width = _vm.UsePosterGrid ? new GridLength(1.5, GridUnitType.Star) : new GridLength(800);
        PlayerColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    // ---------------- Poster grid ----------------

    private void PosterGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            RebuildPosterRows(scrollToTop: false);
    }

    /// <summary>
    /// WPF has no virtualizing wrap panel, so posters are chunked into rows and the rows are virtualized.
    /// This keeps 100k+ titles responsive; only visible rows create cards and download artwork.
    /// </summary>
    private void RebuildPosterRows(bool scrollToTop)
    {
        if (!_vm.UsePosterGrid)
        {
            if (PosterGrid.ItemsSource != null)
                PosterGrid.ItemsSource = null;
            _posterColumns = 0;
            return;
        }
        var width = PosterGrid.ActualWidth - SystemParameters.VerticalScrollBarWidth - 4;
        var columns = Math.Max(1, (int)(width / (PosterMinCardWidth + PosterCardMargin)));
        PosterWidth = Math.Floor(width / columns - PosterCardMargin);
        PosterHeight = Math.Round(PosterWidth * 1.5);
        if (!scrollToTop && columns == _posterColumns)
            return;
        _posterColumns = columns;
        PosterGrid.ItemsSource = _vm.Items.Chunk(columns).ToList();
        if (scrollToTop && PosterGrid.Items.Count > 0)
            PosterGrid.ScrollIntoView(PosterGrid.Items[0]);
    }

    private void PosterCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChannelItem item })
            return;
        if (e.ClickCount == 2)
            _vm.OpenCommand.Execute(item);
        else
            _vm.SelectItemCommand.Execute(item);
        e.Handled = true;
    }

    // ---------------- Playlists menu ----------------

    private void SourceMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (FrameworkElement)sender, Placement = PlacementMode.Bottom };
        MenuItem Item(string glyph, string header) => new()
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons") },
        };
        void Add(string glyph, string header, ICommand command)
        {
            var item = Item(glyph, header);
            item.Command = command;
            menu.Items.Add(item);
        }
        void AddAction(string glyph, string header, Action action)
        {
            var item = Item(glyph, header);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("\uE710", "Add playlist…", _vm.AddSourceCommand);
        Add("\uE70F", "Edit playlist…", _vm.EditSourceCommand);
        Add("\uE72C", "Update from provider", _vm.RefreshCommand);
        Add("\uE74D", "Remove playlist", _vm.RemoveSourceCommand);
        menu.Items.Add(new Separator());
        AddAction("\uE895", "Check for updates…", () => _ = CheckForUpdatesAsync());
        AddAction("\uEBE8", "Report an issue…", () => AppInfo.OpenUrl(AppInfo.IssuesUrl));
        AddAction("\uE946", "About TronIPTV", ShowAbout);
        menu.IsOpen = true;
    }

    private void ShowAbout()
    {
        var text = $"""
            {AppInfo.Title}
            Open-source IPTV player for Windows.

            Licensed under the GNU General Public License v3.0 or later.
            Playback powered by libVLC (VideoLAN, LGPL-2.1-or-later).

            {AppInfo.RepositoryUrl}

            Open the project page?
            """;
        if (MessageBox.Show(this, text, "About TronIPTV", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            AppInfo.OpenUrl(AppInfo.RepositoryUrl);
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var latest = await AppInfo.GetLatestReleaseAsync(cts.Token);
            if (!latest.IsNewer)
            {
                MessageBox.Show(this, $"You're up to date ({AppInfo.DisplayVersion}).", "TronIPTV", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var open = MessageBox.Show(this, $"{latest.Tag} is available (you have {AppInfo.DisplayVersion}).\n\nOpen the download page?",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes)
                AppInfo.OpenUrl(latest.Url);
        }
        catch (Exception e) when (e is Core.IptvException or System.Net.Http.HttpRequestException or TaskCanceledException
                                      or System.Text.Json.JsonException or KeyNotFoundException)
        {
            MessageBox.Show(this, "Could not check for updates: " + e.Message, "TronIPTV", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------- Channel list ----------------

    private void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source)
            return;
        if (FindAncestor<ButtonBase>(source) != null)
            return; // e.g. double-clicking the favorite star
        if (ItemsControl.ContainerFromElement(ChannelList, source) is ListBoxItem { DataContext: ChannelItem item })
            _vm.OpenCommand.Execute(item);
    }

    private void ChannelList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SelectedItem is { } item)
        {
            _vm.OpenCommand.Execute(item);
            e.Handled = true;
        }
    }

    // ---------------- Keyboard ----------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var inText = Keyboard.FocusedElement is TextBox or PasswordBox;
        var inList = Keyboard.FocusedElement is ListBoxItem or ListBox;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var player = _vm.Player;

        switch (e.Key)
        {
            case Key.F11:
            case Key.F when !inText && !ctrl:
                ToggleFullscreen();
                break;
            case Key.Escape when _fullscreen:
                ToggleFullscreen();
                break;
            case Key.Escape when inText:
                if (Keyboard.FocusedElement == CategorySearchBox)
                {
                    _vm.CategorySearch = "";
                    GroupList.Focus();
                }
                else
                {
                    _vm.SearchText = "";
                    ChannelList.Focus();
                }
                break;
            case Key.F when ctrl:
                if (_fullscreen)
                    ToggleFullscreen();
                var box = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? CategorySearchBox : SearchBox;
                box.Focus();
                box.SelectAll();
                break;
            case Key.F5:
                _vm.RefreshCommand.Execute(null);
                break;
            case Key.Space when !inText:
                player.TogglePause();
                break;
            case Key.PageUp:
            case Key.Up when ctrl:
                _vm.ChannelUpCommand.Execute(null);
                break;
            case Key.PageDown:
            case Key.Down when ctrl:
                _vm.ChannelDownCommand.Execute(null);
                break;
            case Key.M when !inText:
                player.ToggleMute();
                break;
            case Key.A when !inText:
                CycleAspect();
                break;
            case Key.D when ctrl && _vm.SelectedItem != null:
                _vm.ToggleFavoriteCommand.Execute(_vm.SelectedItem);
                break;
            case Key.Back when !inText && _vm.OpenSeries != null:
                _vm.CloseSeriesCommand.Execute(null);
                break;
            case Key.Up when !inText && !inList:
                player.ChangeVolume(5);
                break;
            case Key.Down when !inText && !inList:
                player.ChangeVolume(-5);
                break;
            case Key.Right when !inText && !inList:
                player.SeekBy(TimeSpan.FromSeconds(10));
                break;
            case Key.Left when !inText && !inList:
                player.SeekBy(TimeSpan.FromSeconds(-10));
                break;
            default:
                return;
        }
        e.Handled = true;
        ShowControls();
    }

    // ---------------- Overlay / fullscreen ----------------

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleFullscreen();
        else
            Overlay.Focus();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        if (Math.Abs(p.X - _lastMouse.X) + Math.Abs(p.Y - _lastMouse.Y) < 3)
            return; // ignore synthetic moves so the controls can hide
        _lastMouse = p;
        ShowControls();
    }

    private void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.Player.ChangeVolume(e.Delta > 0 ? 5 : -5);
        ShowControls();
    }

    private void ShowControls()
    {
        ControlBar.Visibility = Visibility.Visible;
        TopBar.Opacity = 1;
        Overlay.Cursor = null;
        _idleTimer.Stop();
        if (_fullscreen)
            _idleTimer.Start();
    }

    private void HideControls()
    {
        _idleTimer.Stop();
        if (!_fullscreen || ControlBar.IsMouseOver)
        {
            if (_fullscreen)
                _idleTimer.Start();
            return;
        }
        ControlBar.Visibility = Visibility.Hidden;
        TopBar.Opacity = 0;
        Overlay.Cursor = Cursors.None;
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _fullscreen = true;
            _restoreState = WindowState;
            _restoreBrowser = BrowserColumn.Width;
            _restoreDetails = DetailsRow.Height;

            Browser.Visibility = Visibility.Collapsed;
            Splitter.Visibility = Visibility.Collapsed;
            DetailsPanel.Visibility = Visibility.Collapsed;
            BrowserColumn.MinWidth = 0;
            BrowserColumn.Width = new GridLength(0);
            PlayerColumn.MinWidth = 0;
            SplitterColumn.Width = new GridLength(0);
            DetailsRow.Height = new GridLength(0);

            // Normal -> Maximized after removing the frame covers the taskbar.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            FullscreenButton.Content = "\uE73F";
            Overlay.Focus();
        }
        else
        {
            _fullscreen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _restoreState;

            Browser.Visibility = Visibility.Visible;
            Splitter.Visibility = Visibility.Visible;
            DetailsPanel.Visibility = Visibility.Visible;
            BrowserColumn.Width = _restoreBrowser;
            BrowserColumn.MinWidth = 620;
            PlayerColumn.MinWidth = 380;
            SplitterColumn.Width = new GridLength(4);
            DetailsRow.Height = _restoreDetails;
            FullscreenButton.Content = "\uE740";
        }
        ShowControls();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ChannelUp_Click(object sender, RoutedEventArgs e) => _vm.ChannelUpCommand.Execute(null);
    private void ChannelDown_Click(object sender, RoutedEventArgs e) => _vm.ChannelDownCommand.Execute(null);
    private void Stop_Click(object sender, RoutedEventArgs e) => _vm.StopPlaybackCommand.Execute(null);

    // ---------------- Track menus ----------------

    private void AudioTracks_Click(object sender, RoutedEventArgs e) =>
        ShowTrackMenu((FrameworkElement)sender, _vm.Player.GetAudioTracks(), "No audio tracks", _vm.Player.SetAudioTrack);

    private void Subtitles_Click(object sender, RoutedEventArgs e) =>
        ShowTrackMenu((FrameworkElement)sender, _vm.Player.GetSubtitleTracks(), "No subtitles", _vm.Player.SetSubtitleTrack);

    private static void ShowTrackMenu(FrameworkElement target, List<TrackOption> tracks, string emptyText, Action<int> select)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };
        if (tracks.Count == 0)
            menu.Items.Add(new MenuItem { Header = emptyText, IsEnabled = false });
        foreach (var track in tracks)
        {
            var item = new MenuItem { Header = track.Name, IsCheckable = true, IsChecked = track.IsSelected };
            var id = track.Id;
            item.Click += (_, _) => select(id);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Aspect_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (FrameworkElement)sender, Placement = PlacementMode.Top };
        foreach (var ratio in PlayerViewModel.AspectRatios)
        {
            var item = new MenuItem { Header = ratio ?? "Default", IsCheckable = true, IsChecked = _vm.Player.AspectRatio == ratio };
            var value = ratio;
            item.Click += (_, _) => _vm.Player.AspectRatio = value;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void CycleAspect()
    {
        var ratios = PlayerViewModel.AspectRatios;
        var index = Array.IndexOf(ratios, _vm.Player.AspectRatio);
        _vm.Player.AspectRatio = ratios[(index + 1) % ratios.Length];
        _vm.Player.StatusText = null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match)
                return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
