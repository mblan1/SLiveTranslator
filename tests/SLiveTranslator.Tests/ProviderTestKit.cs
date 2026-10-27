using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;

namespace SLiveTranslator.Tests;

/// <summary>Canned answers and broken connections for the provider tests; no request leaves the PC.</summary>
static class ProviderKit
{
    public const string TestKey = "test-key-not-a-real-one";

    public static ProviderCredentials Key(string key = TestKey) => new(key);

    public static ProviderConfig Config(string model, string key = TestKey) => new(new ProviderCredentials(key), model);

    public static HttpResponseMessage Answer(int status, string json, string? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    public static TranslateRequest Request(params string[] lines) => new(lines, "en", "vi", [new ContextLine("Hm.", "Hừm.")], TestProfiles.Witcher3());

    /// <summary>The single header value, or null when the request has none.</summary>
    public static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single()
        : request.Content?.Headers.TryGetValues(name, out var contentValues) == true ? contentValues.Single() : null;
}

/// <summary>A connection that never answers.</summary>
sealed class StalledHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>A server that is not running: every connection is refused.</summary>
sealed class RefusedHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it. (localhost:11434)",
            new SocketException((int)SocketError.ConnectionRefused));
}
