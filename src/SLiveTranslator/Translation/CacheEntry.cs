using System;
using System.Collections.Generic;

namespace SLiveTranslator.Translation;

/// <summary>One cached translation, as the cache manager shows it.</summary>
/// <param name="Provider">The provider key (<see cref="ProviderKeys"/>); "user" for a hand fix.</param>
/// <param name="Edited">Fixed or edited by hand: never evicted, never translated again.</param>
/// <param name="ProfileVersion">The game profile version it was translated with; 0 without a profile.</param>
/// <param name="AnsweredBy">The service that made it ("google", "mymemory-mt", "user"); "" when unknown.</param>
public sealed record CacheEntry(string Provider, string SourceLang, string TargetLang, string Source, string Translation,
    DateTimeOffset Created, DateTimeOffset Used, int Hits, bool Edited, int ProfileVersion, string AnsweredBy);

/// <summary>Which entries <see cref="CachedTranslator.Snapshot"/> returns; a null field matches every entry.</summary>
/// <param name="Text">Part of the source or of the translation, in any case.</param>
public sealed record CacheFilter(string? Text = null, string? Provider = null, bool? Edited = null);

/// <summary>What <see cref="CachedTranslator.Clear"/> removes.</summary>
public enum CacheScope
{
    All,

    /// <summary>The entries of one provider key.</summary>
    Provider,

    /// <summary>Entries under keys no provider asks for any more (<see cref="ProviderKeys.IsOrphan"/>).</summary>
    Orphans,

    /// <summary>Everything but the hand fixes and hand edits.</summary>
    NonEdited,
}

/// <param name="Bytes">About what the entries take in cache.json.</param>
public sealed record ProviderCacheStats(string Provider, int Entries, int Edited, long Bytes);

public sealed record CacheStats(int Entries, long Bytes, IReadOnlyList<ProviderCacheStats> Providers);
