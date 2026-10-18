using System.Net;

namespace TronIPTV.Core;

public sealed class IptvException(string message, Exception? inner = null) : Exception(message, inner);

public static class IptvHttp
{
    /// <summary>Many IPTV panels only accept player-like agents; libVLC sends this same agent for streams.</summary>
    public const string DefaultUserAgent = "VLC/3.0.24 LibVLC/3.0.24";

    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    public static HttpRequestMessage Get(string url, string? userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        return request;
    }

    public static async Task<HttpResponseMessage> SendAsync(string url, string? userAgent, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(Get(url, userAgent), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new IptvException($"Could not connect to {SafeHost(url)}: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new IptvException($"Request to {SafeHost(url)} timed out.", e);
        }

        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            var reason = response.ReasonPhrase;
            response.Dispose();
            throw new IptvException($"{SafeHost(url)} returned HTTP {code} {reason}.");
        }
        return response;
    }

    /// <summary>Host only, so credential-bearing paths and queries never reach the UI or logs.</summary>
    public static string SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : "server";
}
