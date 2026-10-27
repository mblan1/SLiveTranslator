using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

public class FallbackTranslatorTests
{
    sealed class Stub(string name, Func<IReadOnlyList<string>, IReadOnlyList<string>> translate) : ITranslator
    {
        public int Calls { get; private set; }
        public string Name => name;

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(translate(texts));
        }
    }

    [Fact]
    public async Task Uses_the_next_service_when_the_first_is_blocked()
    {
        var blocked = new Stub("google", _ => throw new TranslatorUnavailableException("429"));
        var backup = new Stub("mymemory", texts => texts.Select(t => "vi:" + t).ToList());
        var chain = new FallbackTranslator("free", blocked, backup);

        var result = await chain.TranslateAsync(["Sword"], "en", "vi", CancellationToken.None);

        Assert.Equal(["vi:Sword"], result);
        Assert.Equal(1, blocked.Calls);
        Assert.Equal(1, backup.Calls);
    }

    [Fact]
    public async Task Reports_every_reason_when_all_services_fail()
    {
        var chain = new FallbackTranslator("free",
            new Stub("google", _ => throw new TranslatorUnavailableException("Google blocked.")),
            new Stub("mymemory", _ => throw new TranslatorUnavailableException("MyMemory quota.")));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => chain.TranslateAsync(["x"], "en", "vi", CancellationToken.None));

        Assert.Contains("Google blocked.", ex.Message);
        Assert.Contains("MyMemory quota.", ex.Message);
    }

    [Fact]
    public async Task Configuration_errors_are_not_swallowed()
    {
        var chain = new FallbackTranslator("free",
            new Stub("google", _ => throw new InvalidOperationException("bad language code")),
            new Stub("mymemory", texts => texts));

        await Assert.ThrowsAsync<InvalidOperationException>(() => chain.TranslateAsync(["x"], "en", "vi", CancellationToken.None));
    }
}
