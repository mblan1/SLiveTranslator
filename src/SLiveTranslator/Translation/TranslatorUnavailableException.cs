using System;

namespace SLiveTranslator.Translation;

/// <summary>The service refused or is blocked right now (rate limit, daily quota). A fallback service may still work.</summary>
/// <param name="retryAfter">How long the service stays paused, when known.</param>
public sealed class TranslatorUnavailableException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
