using System.IO;
using System.Windows;
using TronIPTV.Core;
using Microsoft.Win32;

namespace TronIPTV.Views;

public partial class SourceDialog : Window
{
    private readonly PlaylistSource? _original;
    private PlaylistSource? _result;

    private SourceDialog(PlaylistSource? original)
    {
        InitializeComponent();
        _original = original;
        if (original != null)
        {
            Heading.Text = "Edit playlist";
            NameBox.Text = original.Name;
            UrlBox.Text = original.Url;
            UserBox.Text = original.Username ?? "";
            PasswordBox.Password = original.Password;
            EpgBox.Text = original.EpgUrl ?? "";
            UserAgentBox.Text = original.UserAgent ?? "";
        }
        (original?.Kind switch
        {
            SourceKind.M3uUrl => KindM3uUrl,
            SourceKind.M3uFile => KindM3uFile,
            _ => KindXtream,
        }).IsChecked = true;
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>Returns a new source (keeping the original Id when editing), or null when cancelled.</summary>
    public static PlaylistSource? Show(Window owner, PlaylistSource? source)
    {
        var dialog = new SourceDialog(source) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._result : null;
    }

    private SourceKind Kind =>
        KindM3uUrl.IsChecked == true ? SourceKind.M3uUrl
        : KindM3uFile.IsChecked == true ? SourceKind.M3uFile
        : SourceKind.Xtream;

    private void Kind_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
            return;
        var kind = Kind;
        XtreamPanel.Visibility = kind == SourceKind.Xtream ? Visibility.Visible : Visibility.Collapsed;
        BrowseButton.Visibility = kind == SourceKind.M3uFile ? Visibility.Visible : Visibility.Collapsed;
        (UrlLabel.Text, UrlHint.Text) = kind switch
        {
            SourceKind.Xtream => ("Server URL", "For example http://provider.example:8080 — a full get.php or player_api.php link also works."),
            SourceKind.M3uUrl => ("Playlist URL", "http(s) link to an .m3u / .m3u8 playlist. Downloads are cached for 12 hours; press F5 to reload."),
            _ => ("Playlist file", "A local .m3u / .m3u8 file."),
        };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8|All files (*.*)|*.*",
            Title = "Choose a playlist",
        };
        if (dialog.ShowDialog(this) == true)
        {
            UrlBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var kind = Kind;
        var url = UrlBox.Text.Trim();
        var name = NameBox.Text.Trim();
        var epg = EpgBox.Text.Trim();

        string? error = null;
        if (url.Length == 0)
            error = kind == SourceKind.M3uFile ? "Choose a playlist file." : "Enter a URL.";
        else if (kind == SourceKind.M3uFile && !File.Exists(url))
            error = "That file does not exist.";
        else if (kind == SourceKind.M3uUrl && !IsHttpUrl(url))
            error = "The playlist URL must start with http:// or https://.";
        else if (kind == SourceKind.Xtream && !IsHttpUrl(XtreamClient.NormalizeServer(url)))
            error = "The server URL is not valid.";
        else if (kind == SourceKind.Xtream && (UserBox.Text.Trim().Length == 0 || PasswordBox.Password.Length == 0))
            error = "Enter your username and password.";
        else if (epg.Length > 0 && !IsHttpUrl(epg))
            error = "The TV guide URL must start with http:// or https://.";

        if (error != null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (name.Length == 0)
        {
            name = kind == SourceKind.M3uFile ? Path.GetFileNameWithoutExtension(url)
                : Uri.TryCreate(kind == SourceKind.Xtream ? XtreamClient.NormalizeServer(url) : url, UriKind.Absolute, out var uri) ? uri.Host
                : "Playlist";
        }

        var result = new PlaylistSource
        {
            Id = _original?.Id ?? Guid.NewGuid(),
            Name = name,
            Kind = kind,
            Username = kind == SourceKind.Xtream ? UserBox.Text.Trim() : null,
            EpgUrl = epg.Length > 0 ? epg : null,
            UserAgent = UserAgentBox.Text.Trim() is { Length: > 0 } ua ? ua : null,
        };
        result.Url = url;
        if (kind == SourceKind.Xtream)
            result.Password = PasswordBox.Password;

        _result = result;
        DialogResult = true;
    }

    private static bool IsHttpUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
