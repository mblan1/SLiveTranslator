using System;
using System.Collections.Generic;

namespace SLiveTranslator.Translation;

/// <summary>An earlier line of the conversation and the translation that was shown for it.</summary>
public sealed record ContextLine(string Source, string Translation);

/// <param name="Lines">The strings to translate; the answer has one text per line, in the same order.</param>
/// <param name="Src">Google-style code ("en", "ja", "zh-CN") or "auto".</param>
/// <param name="Context">Recent lines, oldest first. Engines that cannot use context ignore it.</param>
/// <param name="Profile">The game's names, glossary and pronouns; null when no profile applies.</param>
public sealed record TranslateRequest(IReadOnlyList<string> Lines, string Src, string Tgt, IReadOnlyList<ContextLine> Context, GameProfile? Profile)
{
    /// <summary>Lines alone: no context and no profile.</summary>
    public static TranslateRequest Plain(IReadOnlyList<string> lines, string src, string tgt) => new(lines, src, tgt, Array.Empty<ContextLine>(), null);
}

/// <param name="Texts">One translation per requested line.</param>
/// <param name="FixedSources">The source lines with obvious OCR errors fixed, when the engine reports them (LLMs).</param>
/// <param name="AnsweredBy">The service that answered, e.g. "google", "mymemory-mt", "cache".</param>
public sealed record TranslateResult(IReadOnlyList<string> Texts, IReadOnlyList<string>? FixedSources, string AnsweredBy)
{
    /// <summary>
    /// The provider key the answer belongs to in the cache (see <see cref="ProviderKeys"/>); null means the key of the
    /// translator that was asked. A chain of providers names the one that answered.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Who answered each line, when that differs between lines (MyMemory answers each line from its machine
    /// translation or from its corpus); null when <see cref="AnsweredBy"/> holds for every line.
    /// </summary>
    public IReadOnlyList<string>? LineAnsweredBy { get; init; }

    /// <summary>
    /// The provider key of each line's answer, when a chain merged the answers of several providers (see
    /// <see cref="FailedLines"/>); null when <see cref="Provider"/> holds for every line.
    /// </summary>
    public IReadOnlyList<string>? LineProviders { get; init; }

    /// <summary>
    /// Lines the engine gave no answer for (an LLM's answer left them out); their text is the source line. A chain
    /// asks its next service for them, and the cache keeps none of them. Null when every line was answered.
    /// </summary>
    public IReadOnlyList<int>? FailedLines { get; init; }

    public string AnsweredByAt(int line) => LineAnsweredBy is { } lines && line < lines.Count ? lines[line] : AnsweredBy;

    public string? ProviderAt(int line) => LineProviders is { } providers && line < providers.Count ? providers[line] : Provider;
}
