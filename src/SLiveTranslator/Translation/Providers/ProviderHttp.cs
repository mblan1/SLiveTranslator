using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SLiveTranslator.Translation.Providers;

/// <summary>What the providers share for their requests: the client, the deadlines, and the form of an error message.</summary>
internal static class ProviderHttp
{
    /// <summary>For every REST provider. Each request has its own deadline (<see cref="Deadline"/>); the client's only backs it up.</summary>
    public static readonly HttpClient Shared = HttpClients.Create(TimeSpan.FromSeconds(130), "SLiveTranslator/2.0");

    /// <summary>A key check answers quickly or tells the user why not.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    static readonly JsonSerializerOptions BodyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// How long a request of this many lines may take. A subtitle line must not wait long for a stalled request: the
    /// free services answer it instead. A translate-screen chunk of 16 dense paragraphs, each repeated as its fixed
    /// source in an LLM's answer, takes a while longer.
    /// </summary>
    public static TimeSpan Deadline(int lines) => TimeSpan.FromSeconds(Math.Clamp(6 + 4 * (long)lines, 10, 120));

    /// <summary>Room for the answer of an LLM: each line comes back with its fixed source, and thinking counts too.</summary>
    public static int MaxOutputTokens(int lines) => (int)Math.Min(16_000, 2_048 + 384L * lines);

    internal sealed record Response(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)
    {
        public bool Ok => (int)Status is >= 200 and < 300;
    }

    public static StringContent Json(object body) => new(JsonSerializer.Serialize(body, BodyJson), Encoding.UTF8, "application/json");

    /// <summary>
    /// Sends the request and reads the whole answer within <paramref name="timeout"/> (infinite: the caller's token
    /// is the deadline). A timeout or an unreachable server is a <see cref="ProviderException"/>; cancellation by the
    /// caller comes through as it is.
    /// </summary>
    public static async Task<Response> SendAsync(HttpClient http, HttpRequestMessage request, TimeSpan timeout, string service, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using (request)
            using (var response = await http.SendAsync(request, deadline.Token))
            {
                string body = await response.Content.ReadAsStringAsync(deadline.Token);
                return new Response(response.StatusCode, body, RetryAfter(response));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(CheckStatus.Timeout, NoAnswer(service, timeout));
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(CheckStatus.Network, Unreachable(service, ex));
        }
    }

    /// <summary>
    /// Runs one translation within its deadline. A <see cref="ProviderException"/> becomes the chain's
    /// <see cref="TranslatorUnavailableException"/>, and so does the deadline; cancellation by the caller comes through.
    /// </summary>
    public static async Task<T> WithDeadline<T>(string service, TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<T>> run)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            return await run(deadline.Token);
        }
        catch (ProviderException ex)
        {
            throw ex.Unavailable();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslatorUnavailableException(NoAnswer(service, timeout)) { Status = CheckStatus.Timeout };
        }
    }

    /// <summary>A refusal is about this text, not the provider: the next service translates it, and the provider is asked again for the next line.</summary>
    public static TranslatorUnavailableException Declined(string service) => new($"{service} declined to translate this text.", TimeSpan.Zero) { AboutText = true };

    /// <summary>An answer cut off at the length limit; like a refusal, about this text only.</summary>
    public static TranslatorUnavailableException CutOff(string service) => new($"{service}'s answer was cut off at its length limit.", TimeSpan.Zero) { AboutText = true };

    static string NoAnswer(string service, TimeSpan timeout) =>
        timeout == Timeout.InfiniteTimeSpan ? $"{service} did not answer in time." : $"{service} did not answer within {timeout.TotalSeconds:0} seconds.";

    /// <summary>"server not running" for a refused connection (a local server that is not started), else the network's reason.</summary>
    public static string Unreachable(string service, Exception ex) =>
        ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
            ? $"{service} refused the connection: the server is not running."
            : $"{service} cannot be reached ({Shorten(FirstLine(ex.Message))}).";

    public static TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - DateTimeOffset.UtcNow is { Ticks: > 0 } wait ? wait : TimeSpan.Zero,
        _ => null,
    };

    /// <summary>The status an HTTP code means when the provider says nothing more specific.</summary>
    public static CheckStatus StatusOf(HttpStatusCode status) => (int)status switch
    {
        401 => CheckStatus.InvalidKey,
        402 => CheckStatus.NoCredit,
        403 => CheckStatus.NoPermission,
        429 => CheckStatus.RateLimited,
        _ => CheckStatus.Failed,
    };

    /// <summary>"Service: HTTP 429 type: message": the same for every line the problem fails, so it is logged once.</summary>
    public static string Summary(string service, HttpStatusCode status, string type, string message) =>
        $"{service}: HTTP {(int)status}" + (type.Length > 0 ? " " + type : "") + (message.Length > 0 ? ": " + Shorten(FirstLine(message)) : "");

    public static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    public static string Shorten(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
