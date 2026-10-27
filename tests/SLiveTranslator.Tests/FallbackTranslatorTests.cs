using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;
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

    [Fact]
    public async Task A_paused_service_is_logged_once_per_pause()
    {
        string reason = "paused for the test " + Guid.NewGuid().ToString("N");
        var blocked = new Stub("google", _ => throw new TranslatorUnavailableException(reason, TimeSpan.FromMinutes(10)));
        var chain = new FallbackTranslator("free", blocked, new Stub("mymemory", texts => texts));

        for (int i = 0; i < 5; i++) await chain.TranslateAsync(["line " + i], "en", "vi", CancellationToken.None);

        Assert.Equal(5, blocked.Calls);
        Assert.Single(ReadLog(), line => line.Contains(reason));
    }

    /// <summary>The shared test-run log; other tests may be appending to it at the same time.</summary>
    static string[] ReadLog()
    {
        using var stream = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n');
    }
}
