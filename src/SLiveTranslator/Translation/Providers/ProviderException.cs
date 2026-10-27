using System;

namespace SLiveTranslator.Translation.Providers;

/// <summary>
/// A call to a provider failed: the key-check status the failure means, and how long translation passes the provider
/// over. The message is short and the same for every line the problem fails ("DeepL: HTTP 456: Quota exceeded"), with
/// no request id, raw body or key, so a pause is logged once and the UI can show it as it is.
/// </summary>
internal sealed class ProviderException(CheckStatus status, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    /// <summary>How long a key, permission or credit problem passes the provider over: it does not go away by itself.</summary>
    public static readonly TimeSpan AccountProblemPause = TimeSpan.FromMinutes(10);

    public CheckStatus Status { get; } = status;

    /// <summary>What the provider said, or <see cref="AccountProblemPause"/> for an account problem; null leaves the chain's default.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter ?? (status is CheckStatus.InvalidKey or CheckStatus.NoPermission or CheckStatus.NoCredit ? AccountProblemPause : null);

    public TranslatorUnavailableException Unavailable() => new(Message, RetryAfter);
}
