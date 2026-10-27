using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace SLiveTranslator.Translation;

/// <summary>Translation with Claude. Sends all strings in one request and asks for a JSON array back.</summary>
public sealed class ClaudeTranslator : ITranslator
{
    readonly AnthropicClient _client;
    readonly string _model;

    /// <param name="apiKey">API key; when empty the SDK falls back to ANTHROPIC_API_KEY / an "ant auth login" profile.</param>
    public ClaudeTranslator(string? apiKey, string model)
    {
        _client = string.IsNullOrWhiteSpace(apiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = apiKey };
        _model = string.IsNullOrWhiteSpace(model) ? "claude-opus-5-5" : model;
    }

    public string Name => "claude";

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
    {
        string from = sourceLang == "auto" ? "the source language (detect it)" : $"'{sourceLang}'";
        string system =
            "You translate text captured by OCR from a video game screen: subtitles, dialogue, item names, tooltips, menus. " +
            $"Translate every string from {from} into '{targetLang}'. Keep numbers, stat values, placeholders and line structure. " +
            "Keep proper nouns recognizable. Silently fix obvious OCR mistakes. " +
            "Reply with ONLY a JSON array of strings with the same length and order as the input array. No commentary.";

        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 4096,
            System = system,
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            // Safety-classifier declines are re-served by a fallback model inside the same call.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Messages = [new BetaUserMessageParam(JsonSerializer.Serialize(texts))],
        }, ct);

        if (response.StopReason is { } stop && stop == BetaStopReason.Refusal)
            throw new InvalidOperationException("Claude declined to translate this text.");

        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        return ParseArray(text, texts);
    }

    static IReadOnlyList<string> ParseArray(string text, IReadOnlyList<string> original)
    {
        int start = text.IndexOf('[');
        int end = text.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            try
            {
                var arr = JsonSerializer.Deserialize<string[]>(text[start..(end + 1)]);
                if (arr is not null)
                {
                    var result = new string[original.Count];
                    for (int i = 0; i < result.Length; i++)
                        result[i] = i < arr.Length && !string.IsNullOrWhiteSpace(arr[i]) ? arr[i] : original[i];
                    return result;
                }
            }
            catch (JsonException)
            {
                // fall through to the error below
            }
        }
        throw new InvalidOperationException("Claude returned an unexpected response: " + (text.Length > 200 ? text[..200] + "…" : text));
    }
}
