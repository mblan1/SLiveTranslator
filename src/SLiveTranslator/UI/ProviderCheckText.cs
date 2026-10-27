using SLiveTranslator.Translation.Providers;

namespace SLiveTranslator.UI;

/// <summary>What a key check found, for the user: a localized summary, with the provider's own message untranslated under it.</summary>
public static class ProviderCheckText
{
    /// <summary>The summary for the provider's kind where it differs ("3 models" for an LLM, a character quota for MT), else the common one.</summary>
    public static string Summary(ProviderKind kind, ProviderCheck check)
    {
        string common = "Check." + check.Status;
        string specific = common + "." + kind;
        return Loc.F(Loc.Instance.Has(specific) ? specific : common, check.Options.Count);
    }

    /// <summary>The summary, then the provider's message on the next line when it has one.</summary>
    public static string WithDetail(ProviderKind kind, ProviderCheck check) =>
        check.Detail.Length == 0 ? Summary(kind, check) : Summary(kind, check) + "\n" + check.Detail;
}
