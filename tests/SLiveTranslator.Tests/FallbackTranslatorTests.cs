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

    [Fact]
    public async Task A_switch_to_the_next_service_is_reported_once_and_so_is_the_way_back()
    {
        bool googleBlocked = true;
        var google = new Stub("google", texts => googleBlocked ? throw new TranslatorUnavailableException("Google blocked.") : texts.ToList());
        var chain = new FallbackTranslator("free-mt", google, new Stub("mymemory", texts => texts.ToList()));
        var switches = new List<ServiceSwitch>();
        chain.ServiceSwitched += (_, e) => switches.Add(e);

        for (int i = 0; i < 4; i++) await chain.TranslateAsync(["line " + i], "en", "vi", CancellationToken.None);
        googleBlocked = false;
        for (int i = 0; i < 3; i++) await chain.TranslateAsync(["line " + i], "en", "vi", CancellationToken.None);

        Assert.Equal([new ServiceSwitch("google", "mymemory", "Google blocked."), new ServiceSwitch("mymemory", "google", "")], switches);
    }

    [Fact]
    public async Task Lines_translated_at_the_same_time_report_one_switch()
    {
        var gate = new TaskCompletionSource();
        var chain = new FallbackTranslator("free-mt",
            new Stub("google", _ => throw new TranslatorUnavailableException("Google blocked.")),
            new Slow("mymemory", gate.Task));
        int switches = 0;
        chain.ServiceSwitched += (_, _) => Interlocked.Increment(ref switches);

        var lines = Enumerable.Range(0, 8).Select(i => Task.Run(() => chain.TranslateAsync(["line " + i], "en", "vi", CancellationToken.None))).ToList();
        gate.SetResult();
        await Task.WhenAll(lines);

        Assert.Equal(1, switches);
    }

    [Fact]
    public async Task No_switch_is_reported_while_the_first_service_answers()
    {
        var chain = new FallbackTranslator("free-mt", new Stub("google", texts => texts.ToList()), new Stub("mymemory", texts => texts.ToList()));
        int switches = 0;
        chain.ServiceSwitched += (_, _) => switches++;

        await chain.TranslateAsync(["a"], "en", "vi", CancellationToken.None);

        Assert.Equal(0, switches);
    }

    [Fact]
    public async Task An_answer_belongs_to_the_chain_or_to_the_member_that_gave_it()
    {
        var down = new Stub("claude/claude-opus-5-5", _ => throw new TranslatorUnavailableException("credit"));
        var free = new FallbackTranslator("free-mt", new Stub("google", texts => texts.ToList()));
        var oneProvider = await free.TranslateAsync(TranslateRequest.Plain(["a"], "en", "vi"), CancellationToken.None);
        var providers = new FallbackTranslator("claude/claude-opus-5-5", down, free) { MembersAreProviders = true };

        var answer = await providers.TranslateAsync(TranslateRequest.Plain(["a"], "en", "vi"), CancellationToken.None);

        Assert.Equal("free-mt", oneProvider.Provider); // Google and MyMemory are one provider: the free services
        Assert.Equal("free-mt", answer.Provider);
        Assert.Equal("google", answer.AnsweredBy);
    }

    [Fact]
    public async Task A_provider_that_failed_is_passed_over_for_a_while_and_then_asked_again()
    {
        // Claude black-holed: each line would wait for its timeout before the free services answer.
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        bool down = true;
        var claude = new Stub("claude/claude-opus-5-5", texts => down ? throw new TaskCanceledException("timed out") : texts.Select(t => "claude:" + t).ToList());
        var free = new Stub("free-mt", texts => texts.Select(t => "free:" + t).ToList());
        var chain = new FallbackTranslator("claude/claude-opus-5-5", claude, free) { MembersAreProviders = true, Time = clock };

        for (int i = 0; i < 4; i++) await chain.TranslateAsync(["line " + i], "en", "vi", CancellationToken.None);
        Assert.Equal(1, claude.Calls);
        Assert.Equal(4, free.Calls);

        clock.Advance(FallbackTranslator.DefaultPause);
        down = false;
        Assert.Equal(["claude:again"], await chain.TranslateAsync(["again"], "en", "vi", CancellationToken.None));
        Assert.Equal(2, claude.Calls);
    }

    [Fact]
    public async Task A_provider_is_passed_over_for_as_long_as_it_says()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var claude = new Stub("claude/claude-opus-5-5", _ => throw new TranslatorUnavailableException("Claude: HTTP 401 authentication_error", TimeSpan.FromMinutes(10)));
        var free = new Stub("free-mt", texts => texts.ToList());
        var chain = new FallbackTranslator("claude/claude-opus-5-5", claude, free) { MembersAreProviders = true, Time = clock };

        await chain.TranslateAsync(["a"], "en", "vi", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(9));
        await chain.TranslateAsync(["b"], "en", "vi", CancellationToken.None);
        Assert.Equal(1, claude.Calls);

        clock.Advance(TimeSpan.FromMinutes(1));
        await chain.TranslateAsync(["c"], "en", "vi", CancellationToken.None);
        Assert.Equal(2, claude.Calls);
    }

    [Fact]
    public async Task The_last_provider_is_always_asked_and_a_passed_over_one_still_gives_its_reason()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var claude = new Stub("claude/claude-opus-5-5", _ => throw new TranslatorUnavailableException("Claude: HTTP 529 overloaded_error"));
        var free = new Stub("free-mt", _ => throw new TranslatorUnavailableException("MyMemory quota."));
        var chain = new FallbackTranslator("claude/claude-opus-5-5", claude, free) { MembersAreProviders = true, Time = clock };

        await Assert.ThrowsAsync<TranslatorUnavailableException>(() => chain.TranslateAsync(["a"], "en", "vi", CancellationToken.None));
        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => chain.TranslateAsync(["b"], "en", "vi", CancellationToken.None));

        Assert.Equal((1, 2), (claude.Calls, free.Calls));
        Assert.Contains("overloaded", ex.Message);
        Assert.Contains("MyMemory quota.", ex.Message);
    }

    [Fact]
    public async Task The_request_reaches_the_member_with_its_context_and_profile()
    {
        var seen = new List<TranslateRequest>();
        var chain = new FallbackTranslator("x", new Recording(seen));
        var request = new TranslateRequest(["a"], "en", "vi", [new ContextLine("Hi.", "Chào.")], TestProfiles.Witcher3());

        await chain.TranslateAsync(request, CancellationToken.None);

        Assert.Same(request, Assert.Single(seen));
    }

    sealed class Slow(string name, Task gate) : ITranslator
    {
        public string Name => name;

        public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct)
        {
            await gate;
            return texts;
        }
    }

    sealed class Recording(List<TranslateRequest> seen) : ITranslator
    {
        public string Name => "recording";

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            throw new InvalidOperationException();

        public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
        {
            seen.Add(request);
            return Task.FromResult(new TranslateResult(request.Lines, null, Name));
        }
    }

    /// <summary>The shared test-run log; other tests may be appending to it at the same time.</summary>
    static string[] ReadLog()
    {
        using var stream = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n');
    }
}
