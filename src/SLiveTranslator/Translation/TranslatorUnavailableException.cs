using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace SLiveTranslator.Translation;

/// <summary>The service refused or is blocked right now (rate limit, daily quota). A fallback service may still work.</summary>
/// <param name="retryAfter">How long the service stays paused, when known.</param>
public sealed class TranslatorUnavailableException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>
    /// The service cannot answer now (refused, paused, unreachable, timed out), as opposed to a request no service can
    /// answer (a bad language code): another service, or an earlier answer, may stand in.
    /// </summary>
    public static bool IsServiceFailure(Exception ex) => ex is TranslatorUnavailableException or HttpRequestException or TaskCanceledException;
}
