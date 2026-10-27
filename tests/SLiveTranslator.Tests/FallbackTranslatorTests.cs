using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
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
    public async Task An_answer_sent_before_the_provider_was_paused_reports_no_way_back_and_so_no_second_outage()
    {
        // Two lines in flight: one gets a 429 and pauses Claude, the other was sent before that and still succeeds.
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var limited = new TaskCompletionSource();
        var answered = new TaskCompletionSource();
        var claude = new Gated("claude/x", text => text == "A"
            ? limited.Task.ContinueWith<IReadOnlyList<string>>(_ => throw new TranslatorUnavailableException("Claude: HTTP 429 rate_limit_error"))
            : answered.Task.ContinueWith<IReadOnlyList<string>>(_ => ["claude:" + text]));
        var chain = new FallbackTranslator("claude/x", claude, new Stub("free-mt", texts => texts.Select(t => "free:" + t).ToList())) { MembersAreProviders = true, Time = clock };
        var switches = new List<ServiceSwitch>();
        chain.ServiceSwitched += (_, e) => switches.Add(e);

        var a = chain.TranslateAsync(["A"], "en", "vi", CancellationToken.None);
        var b = chain.TranslateAsync(["B"], "en", "vi", CancellationToken.None);
        limited.SetResult();
        Assert.Equal(["free:A"], await a);
        answered.SetResult();
        Assert.Equal(["claude:B"], await b); // the answer counts
        Assert.Equal(["free:C"], await chain.TranslateAsync(["C"], "en", "vi", CancellationToken.None)); // Claude is still paused

        Assert.Equal([new ServiceSwitch("claude/x", "free-mt", "Claude: HTTP 429 rate_limit_error")], switches); // one warning for one outage
    }

    /// <summary>Answers each text when the test says so.</summary>
    sealed class Gated(string name, Func<string, Task<IReadOnlyList<string>>> answer) : ITranslator
    {
        public string Name => name;

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) => answer(texts[0]);
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

    [Fact]
    public async Task Lines_a_provider_left_unanswered_go_to_the_next_one_alone_and_the_provider_is_not_paused()
    {
        var llm = new Partial("claude/x", failedLine: 1);
        var free = new Stub("free-mt", texts => texts.Select(t => "free:" + t).ToList());
        var chain = new FallbackTranslator("claude/x", llm, free) { MembersAreProviders = true };
        var switches = new List<ServiceSwitch>();
        chain.ServiceSwitched += (_, e) => switches.Add(e);

        var result = await chain.TranslateAsync(TranslateRequest.Plain(["A", "B", "C"], "en", "vi"), CancellationToken.None);
        await chain.TranslateAsync(TranslateRequest.Plain(["D", "E"], "en", "vi"), CancellationToken.None);

        Assert.Equal(["llm:A", "free:B", "llm:C"], result.Texts);
        Assert.Equal(["fixed:A", "B", "fixed:C"], result.FixedSources);
        Assert.Equal(["claude/x", "free-mt", "claude/x"], Enumerable.Range(0, 3).Select(result.ProviderAt));
        Assert.Equal(["claude/x", "free-mt", "claude/x"], Enumerable.Range(0, 3).Select(result.AnsweredByAt));
        Assert.Null(result.FailedLines);
        Assert.Equal(2, llm.Calls); // asked again at once: a line it left out is no outage
        Assert.Equal(2, free.Calls); // one line each time
        Assert.Empty(switches);
    }

    [Fact]
    public async Task When_nothing_after_it_can_answer_the_lines_a_provider_left_out_its_other_answers_still_count()
    {
        // Google blocked on this network and MyMemory's daily quota used up: the lines Claude answered are paid for.
        var llm = new Partial("claude/x", failedLine: 1);
        var free = new Stub("free-mt", _ => throw new TranslatorUnavailableException("MyMemory quota."));
        var chain = new FallbackTranslator("claude/x", llm, free) { MembersAreProviders = true };
        var switches = new List<ServiceSwitch>();
        chain.ServiceSwitched += (_, e) => switches.Add(e);

        var result = await chain.TranslateAsync(TranslateRequest.Plain(["A", "B", "C"], "en", "vi"), CancellationToken.None);

        Assert.Equal(["llm:A", "B", "llm:C"], result.Texts);
        Assert.Equal([1], result.FailedLines);
        Assert.Equal(["claude/x", "claude/x", "claude/x"], Enumerable.Range(0, 3).Select(result.ProviderAt));
        Assert.Equal(1, free.Calls);
        Assert.Empty(switches);
    }

    [Fact]
    public async Task A_text_the_provider_declined_goes_to_the_next_one_without_a_switch_or_a_pause()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var claude = new Stub("claude/x", texts => texts[0] == "gore"
            ? throw new TranslatorUnavailableException("Claude declined to translate this text.", TimeSpan.Zero) { AboutText = true }
            : texts.Select(t => "claude:" + t).ToList());
        var free = new Stub("free-mt", texts => texts.Select(t => "free:" + t).ToList());
        var chain = new FallbackTranslator("claude/x", claude, free) { MembersAreProviders = true, Time = clock };
        var switches = new List<ServiceSwitch>();
        chain.ServiceSwitched += (_, e) => switches.Add(e);

        Assert.Equal(["free:gore"], await chain.TranslateAsync(["gore"], "en", "vi", CancellationToken.None));
        Assert.Equal(["claude:next"], await chain.TranslateAsync(["next"], "en", "vi", CancellationToken.None));

        Assert.Equal(2, claude.Calls); // asked again at once
        Assert.Empty(switches); // no "Claude is not answering" warning, and none for the way back
    }

    [Fact]
    public async Task A_declined_text_while_the_provider_is_down_reports_no_way_back()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        bool down = true;
        var claude = new Stub("claude/x", _ => throw (down
            ? new TranslatorUnavailableException("Claude: HTTP 529 overloaded_error")
            : new TranslatorUnavailableException("Claude declined to translate this text.", TimeSpan.Zero) { AboutText = true }));
        var chain = new FallbackTranslator("claude/x", claude, new Stub("free-mt", texts => texts.ToList())) { MembersAreProviders = true, Time = clock };
        var switches = new List<ServiceSwitch>();
        chain.ServiceSwitched += (_, e) => switches.Add(e);

        await chain.TranslateAsync(["a"], "en", "vi", CancellationToken.None);
        clock.Advance(FallbackTranslator.DefaultPause);
        down = false;
        await chain.TranslateAsync(["gore"], "en", "vi", CancellationToken.None);

        Assert.Equal([new ServiceSwitch("claude/x", "free-mt", "Claude: HTTP 529 overloaded_error")], switches);
    }

    [Fact]
    public async Task Lines_left_unanswered_by_the_last_service_stay_failed()
    {
        var chain = new FallbackTranslator("claude/x", new Partial("claude/x", failedLine: 0)) { MembersAreProviders = true };

        var result = await chain.TranslateAsync(TranslateRequest.Plain(["A", "B"], "en", "vi"), CancellationToken.None);

        Assert.Equal([0], result.FailedLines);
    }

    [Fact]
    public async Task The_switch_says_what_the_failure_meant_so_the_notice_can_name_it()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var claude = new Stub("claude/x", _ => throw new ProviderException(CheckStatus.NoCredit, "Claude: HTTP 400 billing_error").Unavailable());
        var chain = new FallbackTranslator("claude/x", claude, new Stub("free-mt", texts => texts.ToList())) { MembersAreProviders = true, Time = clock };
        var switches = new List<ServiceSwitch>();
        chain.ServiceSwitched += (_, e) => switches.Add(e);

        await chain.TranslateAsync(["a"], "en", "vi", CancellationToken.None);
        await chain.TranslateAsync(["b"], "en", "vi", CancellationToken.None); // passed over while paused

        var change = Assert.Single(switches);
        Assert.Equal(CheckStatus.NoCredit, change.Status);
        Assert.Equal("Claude: HTTP 400 billing_error", change.Reason);
    }

    [Theory]
    [InlineData("network", CheckStatus.Network)]
    [InlineData("timeout", CheckStatus.Timeout)]
    [InlineData("unknown", null)]
    public async Task A_failure_without_a_provider_status_gets_one_from_its_kind(string failure, CheckStatus? expected)
    {
        var provider = new Stub("openai/x", _ => throw (failure switch
        {
            "network" => new HttpRequestException("no route"),
            "timeout" => new TaskCanceledException("slow"),
            _ => (Exception)new TranslatorUnavailableException("odd answer"),
        }));
        var chain = new FallbackTranslator("openai/x", provider, new Stub("free-mt", texts => texts.ToList())) { MembersAreProviders = true };
        var switches = new List<ServiceSwitch>();
        chain.ServiceSwitched += (_, e) => switches.Add(e);

        await chain.TranslateAsync(["a"], "en", "vi", CancellationToken.None);

        Assert.Equal(expected, Assert.Single(switches).Status);
    }

    /// <summary>An LLM that leaves one line of every request out of its answer.</summary>
    sealed class Partial(string name, int failedLine) : ITranslator
    {
        public int Calls { get; private set; }
        public string Name => name;

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            throw new InvalidOperationException();

        public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
        {
            Calls++;
            var texts = request.Lines.Select((l, i) => i == failedLine ? l : "llm:" + l).ToList();
            var sources = request.Lines.Select((l, i) => i == failedLine ? l : "fixed:" + l).ToList();
            return Task.FromResult(new TranslateResult(texts, sources, name) { FailedLines = [failedLine] });
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
