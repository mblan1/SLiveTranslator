using System;
using System.Net;
using System.Net.Http;

namespace SLiveTranslator.Translation;

/// <summary>
/// One connection pool for every translation service. An idle connection stays open for 10 minutes (the default is
/// one), so the first line after a quiet stretch of the game does not wait for a new TLS connection.
/// </summary>
internal static class HttpClients
{
    /// <summary>
    /// Redirects are not followed: Google answers a blocked network with a 302 to its captcha page, which
    /// <see cref="GoogleTranslator"/> has to see. Connections are renewed after 15 minutes so a DNS change is picked up.
    /// </summary>
    internal static readonly SocketsHttpHandler Handler = new()
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
    };

    /// <summary>A client on the shared pool, with its own timeout and user agent.</summary>
    public static HttpClient Create(TimeSpan timeout, string userAgent)
    {
        var client = new HttpClient(Handler, disposeHandler: false) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }
}
