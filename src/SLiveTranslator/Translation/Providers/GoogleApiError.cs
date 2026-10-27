using System;
using System.Globalization;
using System.Linq;

namespace SLiveTranslator.Translation.Providers;

/// <summary>The error body every Google API answers with: {"error":{"code","message","status","details"|"errors"}}.</summary>
/// <param name="Reason">The first reason of "details" (newer APIs) or "errors" (Translation v2); "" when none.</param>
/// <param name="RetryAfter">A RetryInfo detail's delay, else the Retry-After header.</param>
internal sealed record GoogleApiError(string Status, string Reason, string Message, TimeSpan? RetryAfter)
{
    public static GoogleApiError Read(ProviderHttp.Response response)
    {
        using var doc = JsonRead.Parse(response.Body);
        var error = doc?.RootElement.Get("error");
        var details = error?.Items("details").ToList() ?? [];
        string reason = details.Select(d => d.Str("reason")).Concat(error?.Items("errors").Select(e => e.Str("reason")) ?? [])
            .FirstOrDefault(r => r is { Length: > 0 }) ?? "";
        var retryAfter = details.Select(d => Delay(d.Str("retryDelay"))).FirstOrDefault(d => d is not null) ?? response.RetryAfter;
        return new GoogleApiError(error?.Str("status") ?? "", reason, error?.Str("message") ?? "", retryAfter);
    }

    public string Summary(string service, ProviderHttp.Response response) =>
        ProviderHttp.Summary(service, response.Status, Reason.Length > 0 ? Reason : Status, Message);

    /// <summary>A protobuf Duration as JSON: "37s", "1.5s"; a value no wait can be (negative, NaN, years) is none.</summary>
    static TimeSpan? Delay(string? text) =>
        text is { Length: > 1 } && text.EndsWith('s') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
        && s is >= 0 and <= 86_400 * 2
            ? TimeSpan.FromSeconds(s)
            : null;
}
