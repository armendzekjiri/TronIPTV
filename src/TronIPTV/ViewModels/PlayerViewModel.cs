using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TronIPTV.Core;
using LibVLCSharp.Shared;

namespace TronIPTV.ViewModels;

public sealed record TrackOption(int Id, string Name, bool IsSelected);

/// <summary>
/// Owns the libVLC engine and media player. libVLC calls are serialized on a background queue because
/// Stop/Play may block on network I/O and must never run inside libVLC's own event callbacks.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private const int MaxReconnects = 5;

    public static readonly string?[] AspectRatios = [null, "16:9", "4:3", "16:10", "21:9", "2.35:1", "1:1"];

    private readonly LibVLC _libVlc;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _progressTimer;
    private Task _queue = Task.CompletedTask;
    private readonly object _queueLock = new();
    private Media? _media;
    private Channel? _current;
    private int _networkCachingMs = 1500;
    private int _reconnects;
    private int _generation;
    /// <summary>When the current attempt reached Playing; null until it does.</summary>
    private DateTime? _playingSince;
    private bool _updatingPosition;
    private bool _disposed;

    public MediaPlayer MediaPlayer { get; }

    [ObservableProperty] private string? _title;
    [ObservableProperty] private bool _hasMedia;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private int _volume = 80;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isSeekable;
    [ObservableProperty] private double _position;
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string? _aspectRatio;
    [ObservableProperty] private string? _videoInfo;

    /// <summary>Raised on the UI thread when a VOD item finishes, so the caller can advance episodes.</summary>
    public event EventHandler? MediaEnded;

    public PlayerViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _libVlc = new LibVLC(enableDebugLogs: false, "--no-video-title-show", "--no-snapshot-preview", "--no-stats");
        MediaPlayer = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };

        MediaPlayer.Playing += (_, _) => Ui(OnPlaying);
        MediaPlayer.Paused += (_, _) => Ui(() => IsPlaying = false);
        MediaPlayer.Stopped += (_, _) => Ui(() => { IsPlaying = false; IsBuffering = false; });
        MediaPlayer.Buffering += (_, e) => Ui(() => OnBuffering(e.Cache));
        MediaPlayer.EncounteredError += (_, _) => Ui(() => OnStreamFailed("Stream error"));
        MediaPlayer.EndReached += (_, _) => Ui(OnEndReached);
        MediaPlayer.Vout += (_, _) => Ui(UpdateVideoInfo);

        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateProgress(), dispatcher);
    }

    public Channel? Current => _current;

    public void Play(Channel channel, int networkCachingMs)
    {
        if (channel.StreamUrl is null)
            return;
        _current = channel;
        _networkCachingMs = networkCachingMs;
        _reconnects = 0;
        Title = channel.Name;
        StatusText = null;
        VideoInfo = null;
        HasMedia = true;
        IsSeekable = false;
        Position = 0;
        TimeText = "";
        StartCurrent();
    }

    private void StartCurrent()
    {
        var channel = _current;
        if (channel?.StreamUrl is null)
            return;
        var generation = ++_generation;
        _playingSince = null;
        IsBuffering = true;
        var caching = _networkCachingMs;
        var aspect = AspectRatio;
        var volume = Volume;
        var muted = IsMuted;

        Enqueue(() =>
        {
            if (generation != _generation)
                return; // superseded by a newer zap
            var media = new Media(_libVlc, new Uri(channel.StreamUrl));
            media.AddOption($":network-caching={caching}");
            media.AddOption(":http-reconnect");
            media.AddOption($":http-user-agent={(string.IsNullOrWhiteSpace(channel.UserAgent) ? IptvHttp.DefaultUserAgent : channel.UserAgent)}");
            if (!string.IsNullOrWhiteSpace(channel.Referrer))
                media.AddOption($":http-referrer={channel.Referrer}");

            var previous = _media;
            _media = media;
            MediaPlayer.AspectRatio = aspect;
            MediaPlayer.Play(media);
            MediaPlayer.Volume = volume;
            MediaPlayer.Mute = muted;
            previous?.Dispose();
        });
        _progressTimer.Start();
    }

    [RelayCommand]
    public void TogglePause()
    {
        if (!HasMedia)
            return;
        if (_current?.Kind == ContentKind.Live && !IsPlaying && MediaPlayer.State is VLCState.Stopped or VLCState.Ended or VLCState.Error)
        {
            StartCurrent();
            return;
        }
        Enqueue(() =>
        {
            if (MediaPlayer.State is VLCState.Stopped or VLCState.Ended or VLCState.Error && _media != null)
                MediaPlayer.Play(_media);
            else
                MediaPlayer.Pause();
        });
    }

    [RelayCommand]
    public void Stop()
    {
        _generation++;
        _current = null;
        HasMedia = false;
        IsPlaying = false;
        IsBuffering = false;
        StatusText = null;
        Title = null;
        VideoInfo = null;
        TimeText = "";
        _progressTimer.Stop();
        Enqueue(() =>
        {
            MediaPlayer.Stop();
            _media?.Dispose();
            _media = null;
        });
    }

    [RelayCommand]
    public void ToggleMute() => IsMuted = !IsMuted;

    public void ChangeVolume(int delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    public void SeekBy(TimeSpan delta)
    {
        if (!IsSeekable)
            return;
        Enqueue(() =>
        {
            var length = MediaPlayer.Length;
            if (length > 0)
                MediaPlayer.Time = Math.Clamp(MediaPlayer.Time + (long)delta.TotalMilliseconds, 0, length - 1000);
        });
    }

    partial void OnVolumeChanged(int value)
    {
        Enqueue(() => MediaPlayer.Volume = value);
        if (value > 0 && IsMuted)
            IsMuted = false;
    }

    partial void OnIsMutedChanged(bool value) => Enqueue(() => MediaPlayer.Mute = value);

    partial void OnAspectRatioChanged(string? value) => Enqueue(() => MediaPlayer.AspectRatio = value);

    partial void OnPositionChanged(double value)
    {
        if (_updatingPosition || !IsSeekable)
            return;
        var target = (float)Math.Clamp(value, 0, 0.999);
        Enqueue(() => MediaPlayer.Position = target);
    }

    public List<TrackOption> GetAudioTracks()
    {
        var selected = MediaPlayer.AudioTrack;
        return MediaPlayer.AudioTrackDescription.Select(t => new TrackOption(t.Id, t.Name ?? $"Track {t.Id}", t.Id == selected)).ToList();
    }

    public List<TrackOption> GetSubtitleTracks()
    {
        var selected = MediaPlayer.Spu;
        return MediaPlayer.SpuDescription.Select(t => new TrackOption(t.Id, t.Name ?? $"Track {t.Id}", t.Id == selected)).ToList();
    }

    public void SetAudioTrack(int id) => Enqueue(() => MediaPlayer.SetAudioTrack(id));

    public void SetSubtitleTrack(int id) => Enqueue(() => MediaPlayer.SetSpu(id));

    private void OnPlaying()
    {
        if (_current == null)
            return;
        IsPlaying = true;
        IsBuffering = false;
        StatusText = null;
        _playingSince = DateTime.UtcNow;
        IsSeekable = MediaPlayer.IsSeekable && MediaPlayer.Length > 0;
        UpdateVideoInfo();
    }

    private void OnBuffering(float cache)
    {
        if (_current == null)
            return;
        IsBuffering = cache < 100f;
        if (_reconnects > 0 && _playingSince == null)
            StatusText = $"Reconnecting ({_reconnects}/{MaxReconnects})…";
        else
            StatusText = IsBuffering ? $"Buffering {cache:0}%" : null;
    }

    private void OnEndReached()
    {
        if (_current == null)
            return;
        if (_current.Kind == ContentKind.Live)
        {
            // A live stream should never end; treat it as a dropped connection.
            OnStreamFailed("Stream ended");
            return;
        }
        IsPlaying = false;
        StatusText = "Finished";
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnStreamFailed(string reason)
    {
        if (_current == null)
            return;
        IsPlaying = false;
        IsBuffering = false;
        if (_playingSince is { } since && DateTime.UtcNow - since > TimeSpan.FromSeconds(20))
            _reconnects = 0; // it played fine for a while; this is a fresh drop
        _playingSince = null;

        if (_reconnects >= MaxReconnects)
        {
            StatusText = $"{reason}. The channel may be offline or your connection limit reached.";
            return;
        }
        _reconnects++;
        var delay = TimeSpan.FromSeconds(Math.Min(2 * _reconnects, 8));
        StatusText = $"{reason} — reconnecting ({_reconnects}/{MaxReconnects})…";
        var generation = _generation;
        _ = Task.Delay(delay).ContinueWith(_ => Ui(() =>
        {
            if (generation == _generation && _current != null)
                StartCurrent();
        }), TaskScheduler.Default);
    }

    private void UpdateProgress()
    {
        if (!HasMedia)
            return;
        var length = MediaPlayer.Length;
        var time = MediaPlayer.Time;
        if (IsPlaying)
            UpdateVideoInfo(); // adaptive streams change resolution mid-playback
        IsSeekable = MediaPlayer.IsSeekable && length > 0;
        if (IsSeekable)
        {
            _updatingPosition = true;
            Position = Math.Clamp(MediaPlayer.Position, 0, 1);
            _updatingPosition = false;
            TimeText = $"{Format(time)} / {Format(length)}";
        }
        else
        {
            TimeText = IsPlaying ? "LIVE" : "";
        }
    }

    private void UpdateVideoInfo()
    {
        uint w = 0, h = 0;
        if (MediaPlayer.Size(0, ref w, ref h) && w > 0 && h > 0)
            VideoInfo = $"{w}×{h}";
    }

    private static string Format(long ms)
    {
        if (ms <= 0)
            return "0:00";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void Ui(Action action)
    {
        if (!_disposed)
            _dispatcher.BeginInvoke(action);
    }

    private void Enqueue(Action action)
    {
        lock (_queueLock)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                if (_disposed)
                    return;
                try { action(); }
                catch (Exception e) { Ui(() => StatusText = "Player error: " + e.Message); }
            }, TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _progressTimer.Stop();
        Task pending;
        lock (_queueLock)
            pending = _queue;
        // Let queued work finish, then release native resources off the UI thread with a bounded wait.
        var cleanup = pending.ContinueWith(_ =>
        {
            _disposed = true;
            MediaPlayer.Stop();
            MediaPlayer.Dispose();
            _media?.Dispose();
            _libVlc.Dispose();
        }, TaskScheduler.Default);
        cleanup.Wait(TimeSpan.FromSeconds(3));
    }
}
