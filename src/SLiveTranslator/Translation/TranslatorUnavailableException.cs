using System;

namespace SLiveTranslator.Translation;

/// <summary>The service refused or is blocked right now (rate limit, daily quota). A fallback service may still work.</summary>
public sealed class TranslatorUnavailableException(string message) : Exception(message);
