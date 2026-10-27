using System;
using System.Linq;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>The prompt every LLM provider sends, and how its answer is read.</summary>
public class LlmPromptTests
{
    [Fact]
    public void The_system_block_is_the_instructions_then_the_profile_and_stays_the_same_between_calls()
    {
        var request = new TranslateRequest(["Ciri!"], "en", "vi", [new ContextLine("Hm.", "Hừm.")], TestProfiles.Witcher3());
        var later = request with { Lines = ["Get down here!"], Context = [new ContextLine("Ciri!", "Ciri!")] };

        string system = LlmPrompt.System(request);

        Assert.StartsWith("You translate live video-game subtitles from 'en' into natural spoken 'vi'", system);
        Assert.EndsWith(TestProfiles.Witcher3().ToPrompt(), system);
        Assert.Equal(system, LlmPrompt.System(later)); // the cached prefix does not change with the lines or the context
        Assert.Equal(LlmPrompt.Instructions("en", "vi"), LlmPrompt.System(TranslateRequest.Plain(["Ciri!"], "en", "vi")));
    }

    [Fact]
    public void The_conversation_and_the_new_lines_go_in_the_user_message()
    {
        var request = new TranslateRequest(["But you were asleep, Uncle Vesemir.", "Space Skip"], "en", "vi",
            [new ContextLine("Ciri, get down here!", "Ciri, xuống đây!")], null);

        string message = LlmPrompt.UserMessage(request);

        Assert.Equal(
            """{"context":[{"source":"Ciri, get down here!","translation":"Ciri, xuống đây!"}],"lines":[{"id":0,"text":"But you were asleep, Uncle Vesemir."},{"id":1,"text":"Space Skip"}]}""",
            message);
    }

    [Fact]
    public void The_instructions_ask_for_the_items_format()
    {
        string instructions = LlmPrompt.Instructions("auto", "vi");

        Assert.Contains("detect it", instructions);
        Assert.Contains("{\"items\":[{\"id\":0,\"source\":\"...\",\"text\":\"...\"}]}", instructions);
        Assert.Contains("\"translation\"", instructions); // how the context is shaped
        Assert.DoesNotContain("<game_profile>", instructions); // the profile follows the instructions
        Assert.Equal(instructions, LlmPrompt.Instructions("auto", "vi"));
    }

    [Fact]
    public void The_schema_holds_every_field_required_and_no_others_as_strict_modes_want()
    {
        var item = LlmPrompt.Schema.GetProperty("properties").GetProperty("items").GetProperty("items");

        Assert.Equal(["id", "source", "text"], item.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.False(LlmPrompt.Schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["type", "properties", "required", "additionalProperties"], LlmPrompt.SchemaFields.Keys);
    }

    [Fact]
    public void The_answer_is_read_by_id_with_the_fixed_source()
    {
        string answer = """
            ```json
            {"items":[{"id":1,"source":"Space Skip","text":"[Space] Bỏ qua"},{"id":0,"source":"But you were asleep, Uncle Vesemir.","text":"Nhưng chú đã ngủ rồi, chú Vesemir."}]}
            ```
            """;

        var result = LlmPrompt.Parse(answer, ["Butyou were asleep, Uncle Vesemir.", "Space Skip"], "Claude", "claude/x");

        Assert.Equal(["Nhưng chú đã ngủ rồi, chú Vesemir.", "[Space] Bỏ qua"], result.Texts);
        Assert.Equal(["But you were asleep, Uncle Vesemir.", "Space Skip"], result.FixedSources);
        Assert.Null(result.FailedLines);
        Assert.Equal("claude/x", result.AnsweredBy);
    }

    [Fact]
    public void An_empty_text_drops_the_line_and_a_line_the_answer_leaves_out_fails_alone()
    {
        // Replaces M3's "keeps its original": an empty text is "not language", and a missing line goes to the next service.
        var result = LlmPrompt.Parse("""{"items":[{"id":0,"source":"x","text":""},{"id":7,"text":"stray"}]}""", ["HUD 7:09", "Geralt."], "Claude", "claude/x");

        Assert.Equal(["", "Geralt."], result.Texts);
        Assert.Equal([1], result.FailedLines);
        Assert.Equal(["x", "Geralt."], result.FixedSources);
    }

    [Fact]
    public void An_id_written_as_a_string_is_read_and_one_that_is_no_number_is_skipped()
    {
        var result = LlmPrompt.Parse("""{"items":[{"id":"1","text":"Hai"},{"id":"zero","text":"stray"},{"id":0.5,"text":"stray"}]}""", ["One", "Two"], "Claude", "c");

        Assert.Equal(["One", "Hai"], result.Texts);
        Assert.Equal([0], result.FailedLines);
    }

    [Fact]
    public void A_repeated_id_keeps_the_first_item_and_a_blank_source_keeps_the_line()
    {
        var result = LlmPrompt.Parse("""{"items":[{"id":0,"source":" ","text":"Một"},{"id":0,"source":"Two","text":"Hai"}]}""", ["One"], "Claude", "c");

        Assert.Equal(["Một"], result.Texts);
        Assert.Equal(["One"], result.FixedSources);
    }

    [Theory]
    [InlineData("<think>The user wants {json}. Ok.</think>\n{\"items\":[{\"id\":0,\"source\":\"Hi\",\"text\":\"Chào\"}]}")] // a reasoning model
    [InlineData("[{\"id\":0,\"source\":\"Hi\",\"text\":\"Chào\"}]")] // a bare list of items
    [InlineData("Here you go: {\"lines\":[{\"id\":0,\"source\":\"Hi\",\"text\":\"Chào\"}]} Enjoy!")] // the list named otherwise
    [InlineData("Translations [en->vi]:\n{\"items\":[{\"id\":0,\"source\":\"Hi\",\"text\":\"Chào\"}]}")] // a '[' in the text before the object
    [InlineData("[{\"id\":0,\"source\":\"Hi\",\"text\":\"Chào\"},{\"id\":1,\"source\":\"x\",\"text\":\"y\"}]")] // a bare list of several items
    [InlineData("[{\"id\":0,\"source\":\"Hi\",\"text\":\"Chào\",\"alternatives\":[\"Xin chào\"]}]")] // an item with a list of its own
    public void Answers_around_the_format_are_read_tolerantly(string answer)
    {
        Assert.Equal(["Chào"], LlmPrompt.Parse(answer, ["Hi"], "Model", "m").Texts);
    }

    [Theory]
    [InlineData("I can't help with that.")]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":[{\"id\":0,\"source\":\"x\"}]}")] // no text at all
    [InlineData("{\"items\":[{\"id\":0,\"text\":\"cut off")]
    public void An_answer_with_no_usable_item_lets_the_next_service_translate(string answer)
    {
        var ex = Assert.Throws<TranslatorUnavailableException>(() => LlmPrompt.Parse(answer, ["x"], "Claude", "c"));

        Assert.Contains("Claude returned an unexpected response", ex.Message);
    }

    [Theory]
    [InlineData(1, 10)] // a subtitle line
    [InlineData(2, 14)] // a line with a key hint
    [InlineData(16, 70)] // a translate-screen chunk
    [InlineData(100, 120)]
    public void The_deadline_grows_with_the_lines_of_the_request(int lines, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ProviderHttp.Deadline(lines));

    [Fact]
    public void A_record_of_credentials_never_shows_the_key()
    {
        var config = new ProviderConfig(new ProviderCredentials("sk-secret-key", "westeurope"), "general");

        Assert.DoesNotContain("sk-secret-key", config.ToString());
        Assert.Contains("westeurope", config.ToString());
    }
}
