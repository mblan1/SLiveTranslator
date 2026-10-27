using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>
/// One check and translation per provider against the real service, with the key from the environment. They run only
/// with SLT_LIVE=1 and the provider's key set (SLT_CLAUDE_KEY, SLT_OPENAI_KEY, SLT_GEMINI_KEY, SLT_COMPAT_URL with
/// SLT_COMPAT_KEY, SLT_DEEPL_KEY, SLT_AZURE_KEY with SLT_AZURE_REGION, SLT_GOOGLECLOUD_KEY); otherwise they return at
/// once. They spend the user's quota: run them by hand and rarely.
/// </summary>
public class LiveProviderTests
{
    static string? Env(string name) => TestEnvironment.LiveTestsEnabled && Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    static readonly TranslateRequest Line = new(["Roach, wait for me here."], "en", "vi", [], TestProfiles.Witcher3());

    /// <summary>The key is valid, the list has a recommended model or an engine, and a line comes back with the name kept.</summary>
    static async Task<ITranslator> CheckAndTranslate(ITranslationProvider provider, ProviderCredentials credentials)
    {
        var check = await provider.CheckAsync(credentials, CancellationToken.None);
        Assert.True(check.Status == CheckStatus.Ok, check.Detail);
        var pick = check.Options.FirstOrDefault(o => o.Tags.HasFlag(ModelTags.Recommended)) ?? check.Options.First();
        var translator = provider.Create(new ProviderConfig(credentials, pick.Id));
        var result = await translator.TranslateAsync(Line, CancellationToken.None);
        Assert.Contains("Roach", result.Texts[0]);
        return translator;
    }

    [Fact]
    public async Task Claude_checks_translates_and_serves_the_system_block_from_the_prompt_cache()
    {
        if (Env("SLT_CLAUDE_KEY") is not { } key) return;

        var translator = (ClaudeTranslator)await CheckAndTranslate(new ClaudeProvider(), new ProviderCredentials(key));
        await translator.TranslateAsync(Line with { Lines = ["Get down here, Ciri!"] }, CancellationToken.None);

        Assert.True(translator.LastUsage?.CacheReadInputTokens > 0, "the second call should read the cached system block");
    }

    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-haiku-5-5")] // sent no fallback: structured output, thinking off and effort only
    public async Task Claude_takes_structured_output_with_the_fallback_and_its_beta(string model)
    {
        if (Env("SLT_CLAUDE_KEY") is not { } key) return;
        var memory = new ClaudeModelMemory();

        var result = await new ClaudeTranslator(new ProviderConfig(new ProviderCredentials(key), model), memory).TranslateAsync(Line, CancellationToken.None);

        Assert.Contains("Roach", result.Texts[0]); // translated either way: a rejected option is left out and sent again
        Assert.True(memory.Rejected(model) == ClaudeFeatures.None, $"{model} rejected {memory.Rejected(model)}");
    }

    [Fact]
    public async Task OpenAI_checks_and_translates()
    {
        if (Env("SLT_OPENAI_KEY") is not { } key) return;
        await CheckAndTranslate(new OpenAIProvider(), new ProviderCredentials(key));
    }

    [Fact]
    public async Task Gemini_checks_and_translates()
    {
        if (Env("SLT_GEMINI_KEY") is not { } key) return;
        await CheckAndTranslate(new GeminiProvider(), new ProviderCredentials(key));
    }

    [Fact]
    public async Task A_compatible_server_checks_and_translates()
    {
        if (Env("SLT_COMPAT_URL") is not { } url) return;
        await CheckAndTranslate(new CompatProvider(), new ProviderCredentials(Environment.GetEnvironmentVariable("SLT_COMPAT_KEY") ?? "", BaseUrl: url));
    }

    [Fact]
    public async Task DeepL_checks_and_translates()
    {
        if (Env("SLT_DEEPL_KEY") is not { } key) return;
        await CheckAndTranslate(new DeepLProvider(), new ProviderCredentials(key));
    }

    [Fact]
    public async Task Azure_checks_and_translates()
    {
        if (Env("SLT_AZURE_KEY") is not { } key) return;
        await CheckAndTranslate(new AzureProvider(), new ProviderCredentials(key, Environment.GetEnvironmentVariable("SLT_AZURE_REGION") ?? ""));
    }

    [Fact]
    public async Task Google_cloud_checks_and_translates()
    {
        if (Env("SLT_GOOGLECLOUD_KEY") is not { } key) return;
        // Placeholders keep the name for Google Cloud, as the registry wraps it.
        var check = await new GoogleCloudProvider().CheckAsync(new ProviderCredentials(key), CancellationToken.None);
        Assert.True(check.Status == CheckStatus.Ok, check.Detail);
        var translator = new ProfileTranslator(new GoogleCloudProvider().Create(new ProviderConfig(new ProviderCredentials(key), GoogleCloudProvider.Nmt)));
        Assert.Contains("Roach", (await translator.TranslateAsync(Line, CancellationToken.None)).Texts[0]);
    }
}
