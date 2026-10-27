using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

public class ProfileTranslatorTests
{
    /// <summary>A machine translation the test scripts: it gets each request and returns what the test makes of it.</summary>
    sealed class ScriptedMt(Func<string, string> translate, string answeredBy = "google") : ITranslator
    {
        public List<string> Sent { get; } = new();
        public List<TranslateRequest> Requests { get; } = new();
        public string Name => "free-mt";

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            throw new InvalidOperationException("the profile translator sends requests");

        public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            Sent.AddRange(request.Lines);
            return Task.FromResult(new TranslateResult(request.Lines.Select(translate).ToList(), null, answeredBy) { Provider = "free-mt" });
        }
    }

    static Task<TranslateResult> Translate(ITranslator translator, GameProfile? profile, params string[] lines) =>
        translator.TranslateAsync(new TranslateRequest(lines, "en", "vi", [new ContextLine("Hello.", "Xin chào.")], profile), CancellationToken.None);

    [Theory]
    // kept as sent
    [InlineData("QZX1, xuống đây!")]
    // re-cased, or with a space before the number
    [InlineData("Qzx1, xuống đây!")]
    [InlineData("qzx 1, xuống đây!")]
    public async Task Names_go_out_as_placeholders_and_come_back_as_written(string answer)
    {
        var mt = new ScriptedMt(_ => answer);

        var result = await Translate(new ProfileTranslator(mt), TestProfiles.Witcher3(), "Ciri, get down here!");

        Assert.Equal(["QZX1, get down here!"], mt.Sent);
        Assert.Equal(["Ciri, xuống đây!"], result.Texts);
        Assert.Null(mt.Requests[0].Profile); // applied here, not by the engine
        Assert.Single(mt.Requests[0].Context); // what the engine does with context is its own business
    }

    [Fact]
    public async Task A_dropped_placeholder_sends_the_line_again_without_placeholders()
    {
        var mt = new ScriptedMt(line => line.Contains(Placeholders.Marker)
            ? "QZX2 nói với QZX1 rằng nó đã sẵn sàng." // QZX3 (ROACH) dropped
            : "Geralt nói với Vesemir rằng ROACH đã sẵn sàng.");

        var result = await Translate(new ProfileTranslator(mt), TestProfiles.Witcher3(), "Geralt told Vesemir that ROACH was ready.");

        Assert.Equal(["QZX1 told QZX2 that QZX3 was ready.", "Geralt told Vesemir that ROACH was ready."], mt.Sent);
        Assert.Equal(["Geralt nói với Vesemir rằng ROACH đã sẵn sàng."], result.Texts);
    }

    [Fact]
    public async Task Swapped_placeholders_restore_their_own_names()
    {
        var mt = new ScriptedMt(_ => "QZX2 nói với QZX1.");

        var result = await Translate(new ProfileTranslator(mt), TestProfiles.Witcher3(), "Geralt told Vesemir.");

        Assert.Equal(["Vesemir nói với Geralt."], result.Texts);
        Assert.Single(mt.Sent);
    }

    [Theory]
    [InlineData("Một QZX1 và một QZX1.")] // duplicated
    [InlineData("Một witcher.")] // dropped
    [InlineData("Một QZX1 và QZX7.")] // one that was never sent
    public async Task A_translation_whose_placeholders_did_not_survive_is_discarded(string answer)
    {
        var mt = new ScriptedMt(line => line.Contains(Placeholders.Marker) ? answer : "Một phù thủy, không phải ma cà rồng.");

        var result = await Translate(new ProfileTranslator(mt), TestProfiles.Witcher3(), "A witcher, not a vampire.");

        Assert.Equal(["A QZX1, not a vampire.", "A witcher, not a vampire."], mt.Sent);
        // The unprotected answer still gets the fix rules: "phù thủy" is the witcher here (no witch in the line).
        Assert.Equal(["Một witcher, không phải ma cà rồng."], result.Texts);
    }

    [Fact]
    public async Task Only_the_lines_that_lost_a_placeholder_are_sent_again()
    {
        var mt = new ScriptedMt(line => line switch
        {
            "QZX1, get down here!" => "Ciri, xuống đây!", // the engine wrote the name itself: QZX1 lost
            "Where is QZX1?" => "QZX1 đâu?",
            _ => "vi:" + line,
        });

        var result = await Translate(new ProfileTranslator(mt), TestProfiles.Witcher3(), "Ciri, get down here!", "Where is Yennefer?", "Nothing to keep.");

        Assert.Equal(["QZX1, get down here!", "Where is QZX1?", "Nothing to keep.", "Ciri, get down here!"], mt.Sent);
        Assert.Equal(["vi:Ciri, get down here!", "Yennefer đâu?", "vi:Nothing to keep."], result.Texts);
        Assert.Equal(2, mt.Requests.Count);
    }

    [Fact]
    public async Task A_line_of_nothing_but_kept_names_is_answered_without_a_request()
    {
        // The answer could only be the source again, which the cache never keeps: each time it costs a request.
        var mt = new ScriptedMt(line => "vi:" + line);

        var result = await Translate(new ProfileTranslator(mt), TestProfiles.Witcher3(), "Vesemir!", "Ciri, Geralt...", "Alt", "7:09");

        Assert.Empty(mt.Requests);
        Assert.Equal(["Vesemir!", "Ciri, Geralt...", "Alt", "7:09"], result.Texts);
        Assert.Equal(ProfileTranslator.AnsweredByProfile, result.AnsweredByAt(0));
    }

    [Fact]
    public async Task Only_the_lines_with_words_to_translate_are_sent()
    {
        var mt = new ScriptedMt(line => line == "QZX1 Skip" ? "QZX1 Bỏ qua" : "vi:" + line);

        var result = await Translate(new ProfileTranslator(mt), TestProfiles.Witcher3(), "Vesemir!", "Space Skip");

        Assert.Equal(["QZX1 Skip"], mt.Sent);
        Assert.Equal(["Vesemir!", "[Space] Bỏ qua"], result.Texts); // the fix rules still apply to the translated line
        Assert.Equal([ProfileTranslator.AnsweredByProfile, "google"], [result.AnsweredByAt(0), result.AnsweredByAt(1)]);
    }

    [Fact]
    public async Task Fix_rules_run_after_the_names_are_back_and_look_at_the_original_line()
    {
        // "Vesemir" is a placeholder in the request, but the trigger "Uncle Vesemir" is looked for in the line OCR read.
        var mt = new ScriptedMt(_ => "Tôi rất xin lỗi, anh QZX1.");

        var result = await Translate(new ProfileTranslator(mt), TestProfiles.Witcher3(), "I'm very sorry, Uncle Vesemir.");

        Assert.Equal(["I'm very sorry, Uncle QZX1."], mt.Sent);
        Assert.Equal(["Tôi rất xin lỗi, chú Vesemir."], result.Texts);
    }

    [Fact]
    public async Task A_line_that_already_holds_the_marker_goes_out_unprotected()
    {
        var mt = new ScriptedMt(line => line);

        await Translate(new ProfileTranslator(mt), TestProfiles.Witcher3(), "QZX1 is not Geralt.");

        Assert.Equal(["QZX1 is not Geralt."], mt.Sent);
    }

    [Fact]
    public async Task Without_a_profile_the_request_passes_through_untouched()
    {
        var mt = new ScriptedMt(_ => "cá viền đỏ");

        var result = await Translate(new ProfileTranslator(mt), null, "Roach");

        Assert.Equal(["Roach"], mt.Sent);
        Assert.Equal(["cá viền đỏ"], result.Texts);
        Assert.Equal("free-mt", result.Provider);
    }

    [Fact]
    public async Task Who_answered_each_line_is_kept_across_the_second_try()
    {
        int call = 0;
        var mt = new AnsweringMt(request => ++call == 1
            ? new TranslateResult(["Một witcher."], null, "google")
            : new TranslateResult(["Một phù thủy."], null, "mymemory-mt"));

        var result = await Translate(new ProfileTranslator(mt), TestProfiles.Witcher3(), "A witcher.");

        Assert.Equal(["Một witcher."], result.Texts);
        Assert.Equal("mymemory-mt", result.AnsweredByAt(0));
    }

    sealed class AnsweringMt(Func<TranslateRequest, TranslateResult> answer) : ITranslator
    {
        public string Name => "free-mt";

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLang, string targetLang, CancellationToken ct) =>
            throw new InvalidOperationException();

        public Task<TranslateResult> TranslateAsync(TranslateRequest request, CancellationToken ct) => Task.FromResult(answer(request));
    }
}
