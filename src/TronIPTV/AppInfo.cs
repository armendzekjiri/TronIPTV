using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using TronIPTV.Core;

namespace TronIPTV;

/// <summary>Product identity and links. The version comes from the git tag via MinVer.</summary>
public static class AppInfo
{
    public const string Name = "TronIPTV";
    public const string RepositoryUrl = "https://github.com/armendzekjiri/TronIPTV";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string IssuesUrl = RepositoryUrl + "/issues/new/choose";
    // The list endpoint (unlike /releases/latest) includes pre-releases, newest first.
    private const string ReleasesApi = "https://api.github.com/repos/armendzekjiri/TronIPTV/releases?per_page=10";

    /// <summary>SemVer without build metadata, e.g. "0.1.0" or "0.2.0-alpha.0.3".</summary>
    public static string Version { get; } = ReadVersion();
    public static string DisplayVersion => "v" + Version;
    public static string Title => $"{Name} {DisplayVersion}";

    private static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public sealed record ReleaseInfo(string Tag, string Url, bool IsNewer);

    /// <summary>Asks GitHub for the latest published release. Only called when the user requests it.</summary>
    public static async Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.TryAddWithoutValidation("User-Agent", $"{Name}/{Version}");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var response = await IptvHttp.Client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new IptvException($"GitHub returned HTTP {(int)response.StatusCode}.");

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var latest = doc.RootElement.EnumerateArray()
            .FirstOrDefault(r => !(r.TryGetProperty("draft", out var d) && d.GetBoolean()));
        if (latest.ValueKind != JsonValueKind.Object)
            throw new IptvException("No release has been published yet.");
        var tag = latest.GetProperty("tag_name").GetString() ?? "";
        var url = latest.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesUrl : ReleasesUrl;
        return new ReleaseInfo(tag, url, ReleaseVersion.IsNewer(tag, Version));
    }
}
