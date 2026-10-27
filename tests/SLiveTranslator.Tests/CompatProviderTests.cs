using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using SLiveTranslator.Translation.Providers;
using Xunit;

namespace SLiveTranslator.Tests;

public class CompatProviderTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    static CompatProvider Provider(HttpMessageHandler handler) => new(new HttpClient(handler), new TestClock(Now));

    static ProviderCredentials At(string baseUrl, string key = "") => new(key, BaseUrl: baseUrl);

    static ProviderConfig Config(string baseUrl, string model, string key = "") => new(At(baseUrl, key), model);

    static string Chat(string? content, string finish = "stop") => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-1", @object = "chat.completion",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = finish } },
    });

    const string Items = """{"items":[{"id":0,"source":"Ciri, get down here!","text":"Ciri, xuống đây!"}]}""";

    [Fact]
    public async Task Ollama_needs_no_key_and_its_loaded_model_is_the_fastest()
    {
        var sent = new List<HttpRequestMessage>();
        var handler = new FakeHandler((request, _) =>
        {
            sent.Add(request);
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/models" => FakeHandler.Json("""{"object":"list","data":[{"id":"qwen3:8b","created":1790000000},{"id":"llama3.2:3b","created":1795000000},{"id":"nomic-embed-text:latest","created":1780000000}]}"""),
                "/api/ps" => FakeHandler.Json("""{"models":[{"name":"qwen3:8b","model":"qwen3:8b","size_vram":5000000000}]}"""),
                _ => ProviderKit.Answer(404, "{}"),
            };
        });

        var check = await Provider(handler).CheckAsync(At("http://localhost:11434/v1/"), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal(["qwen3:8b", "llama3.2:3b"], check.Options.Select(o => o.Id)); // the embedding model is gone
        Assert.Equal(ModelTags.Fastest, check.Options[0].Tags);
        Assert.Equal(ModelTags.None, check.Options[1].Tags); // Ollama's "created" is the last pull: no Recommended
        Assert.Null(sent[0].Headers.Authorization);
        Assert.Equal("http://localhost:11434/api/ps", sent[1].RequestUri!.ToString());
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", 4096, true)]
    [InlineData("http://localhost:11434/v1", 8192, false)]
    [InlineData("http://localhost:1234/v1", 4096, true)]
    [InlineData("http://localhost:1234/v1", 32768, false)]
    public async Task A_loaded_model_with_too_small_a_context_for_the_prompt_is_pointed_out(string baseUrl, int context, bool warned)
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => FakeHandler.Json("""{"data":[{"id":"qwen3:8b"}]}"""),
            "/api/ps" => FakeHandler.Json($$"""{"models":[{"name":"qwen3:8b","model":"qwen3:8b","context_length":{{context}}}]}"""),
            "/api/v0/models" => FakeHandler.Json($$"""{"data":[{"id":"qwen3:8b","type":"llm","state":"loaded","max_context_length":131072,"loaded_context_length":{{context}}}]}"""),
            _ => ProviderKit.Answer(404, "{}"),
        });

        var check = await Provider(handler).CheckAsync(At(baseUrl), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, check.Status); // it works, with a warning
        if (warned) Assert.Contains($"qwen3:8b with a context of {context} tokens", check.Detail);
        else Assert.Equal("", check.Detail);
    }

    [Fact]
    public async Task A_local_server_that_is_not_running_says_so()
    {
        var check = await Provider(new RefusedHandler()).CheckAsync(At("http://localhost:1234/v1"), CancellationToken.None);

        Assert.Equal(CheckStatus.Network, check.Status);
        Assert.Equal("LM Studio refused the connection: the server is not running.", check.Detail);
    }

    [Fact]
    public async Task LM_Studio_tells_its_embedding_and_loaded_models_through_its_own_api()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => FakeHandler.Json("""{"data":[{"id":"google/gemma-3-12b"},{"id":"text-embedding-qwen3"},{"id":"mistral-7b-instruct"}]}"""),
            "/api/v0/models" => FakeHandler.Json("""{"data":[{"id":"google/gemma-3-12b","type":"vlm","state":"not-loaded"},{"id":"text-embedding-qwen3","type":"embeddings","state":"loaded"},{"id":"mistral-7b-instruct","type":"llm","state":"loaded"}]}"""),
            _ => ProviderKit.Answer(404, "{}"),
        });

        var check = await Provider(handler).CheckAsync(At("http://127.0.0.1:1234/v1"), CancellationToken.None);

        Assert.Equal(["google/gemma-3-12b", "mistral-7b-instruct"], check.Options.Select(o => o.Id));
        Assert.Equal(ModelTags.Fastest, check.Options[1].Tags);
    }

    [Fact]
    public async Task OpenRouter_proves_the_key_shows_its_usage_and_recommends_the_newest_model_with_structured_output()
    {
        var sent = new List<string>();
        var handler = new FakeHandler((request, _) =>
        {
            sent.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/api/v1/models" => FakeHandler.Json("""
                    {"data":[
                      {"id":"openai/gpt-6.1-terra","created":1790000000,"architecture":{"output_modalities":["text"]},"supported_parameters":["response_format","structured_outputs"],"pricing":{"prompt":"0.000001","completion":"0.000005"},"context_length":400000},
                      {"id":"some/brand-new-model","created":1799000000,"architecture":{"output_modalities":["text"]},"supported_parameters":["temperature"],"pricing":{"prompt":"0","completion":"0"}},
                      {"id":"google/gemini-3.8-flash","created":1795000000,"architecture":{"output_modalities":["text"]},"supported_parameters":["structured_outputs"],"pricing":{"prompt":"0.00000075","completion":"0.00000375"},"context_length":1048576},
                      {"id":"black-forest-labs/flux-3","created":1798000000,"architecture":{"output_modalities":["image"]},"supported_parameters":[]},
                      {"id":"x-ai/grok-5-preview","created":1798500000,"architecture":{"output_modalities":["text"]},"supported_parameters":["structured_outputs"]}
                    ]}
                    """),
                "/api/v1/key" => FakeHandler.Json("""{"data":{"label":"sk-or-v1-abc...","limit":10,"limit_remaining":7.5,"usage":2.5,"is_free_tier":false}}"""),
                _ => ProviderKit.Answer(404, "{}"),
            };
        });

        var check = await Provider(handler).CheckAsync(At("https://openrouter.ai/api/v1", "sk-or-test"), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal(["openai/gpt-6.1-terra", "some/brand-new-model", "google/gemini-3.8-flash", "x-ai/grok-5-preview"], check.Options.Select(o => o.Id));
        Assert.Equal(ModelTags.Recommended | ModelTags.Fastest, check.Options[2].Tags); // newest with structured output; "flash"
        Assert.Equal(ModelTags.None, check.Options[1].Tags); // newer, but no structured output
        Assert.Equal("$1 / $5 per 1M tokens", check.Options[0].CostHint);
        Assert.Equal("free", check.Options[1].CostHint);
        Assert.Equal(new ProviderUsage(2.5, 10, "USD"), check.Usage);
        Assert.Equal(["/api/v1/models", "/api/v1/key"], sent);
    }

    [Fact]
    public async Task OpenRouter_with_no_credit_left_says_so()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath == "/api/v1/key"
            ? FakeHandler.Json("""{"data":{"limit":5,"limit_remaining":0,"usage":5}}""")
            : FakeHandler.Json("""{"data":[{"id":"openai/gpt-6-luna","created":1790000000}]}"""));

        var check = await Provider(handler).CheckAsync(At("https://openrouter.ai/api/v1", "sk-or-test"), CancellationToken.None);

        Assert.Equal(CheckStatus.NoCredit, check.Status);
        Assert.Single(check.Options);
    }

    [Fact]
    public async Task Mistral_drops_models_that_cannot_chat_and_puts_archived_ones_last()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""
            {"object":"list","data":[
              {"id":"mistral-large-2511","created":1790000000,"capabilities":{"completion_chat":true}},
              {"id":"mistral-medium-2508","created":1780000000,"capabilities":{"completion_chat":true},"deprecation":"2026-01-01T00:00:00Z"},
              {"id":"mistral-small-2603","created":1795000000,"capabilities":{"completion_chat":true}},
              {"id":"mistral-embed","created":1700000000,"capabilities":{"completion_chat":false}},
              {"id":"codestral-2508","created":1785000000,"capabilities":{"completion_chat":false}}
            ]}
            """));

        var check = await Provider(handler).CheckAsync(At("https://api.mistral.ai/v1", "key"), CancellationToken.None);

        Assert.Equal(["mistral-large-2511", "mistral-small-2603", "mistral-medium-2508"], check.Options.Select(o => o.Id));
        Assert.Equal(ModelTags.Recommended | ModelTags.Fastest, check.Options[1].Tags);
        Assert.Equal(ModelTags.Deprecated, check.Options[2].Tags);
        Assert.Equal("mistral-medium-2508 (deprecated)", check.Options[2].Label);
    }

    [Fact]
    public async Task DeepSeek_has_no_release_dates_so_nothing_is_recommended_and_flash_is_fastest()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"object":"list","data":[{"id":"deepseek-v4-pro","object":"model"},{"id":"deepseek-flash","object":"model"}]}"""));

        var check = await Provider(handler).CheckAsync(At("https://api.deepseek.com", "key"), CancellationToken.None);

        Assert.Equal([ModelTags.None, ModelTags.Fastest], check.Options.Select(o => o.Tags));
    }

    [Theory]
    [InlineData(401, CheckStatus.InvalidKey)]
    [InlineData(403, CheckStatus.InvalidKey)]
    [InlineData(402, CheckStatus.NoCredit)]
    [InlineData(429, CheckStatus.RateLimited)]
    [InlineData(500, CheckStatus.Failed)]
    public async Task A_failed_list_says_why(int status, CheckStatus expected)
    {
        var handler = new FakeHandler((_, _) => ProviderKit.Answer(status, """{"error":{"message":"The server's own words.","type":"some_type"}}"""));

        var check = await Provider(handler).CheckAsync(At("https://api.groq.com/openai/v1", "key"), CancellationToken.None);

        Assert.Equal(expected, check.Status);
        Assert.Equal($"Groq: HTTP {status} some_type: The server's own words.", check.Detail);
    }

    [Fact]
    public async Task A_wrong_path_points_at_the_base_url()
    {
        var check = await Provider(new FakeHandler((_, _) => ProviderKit.Answer(404, ""))).CheckAsync(At("http://192.168.1.5:8080"), CancellationToken.None);

        Assert.Equal(CheckStatus.Failed, check.Status);
        Assert.Equal("192.168.1.5:8080: HTTP 404: nothing at this address; check the base URL (it usually ends in /v1)", check.Detail);
    }

    [Theory]
    [InlineData("https://api.x.ai/v1", "", CheckStatus.InvalidKey)] // a cloud preset needs its key
    [InlineData("not a url", "key", CheckStatus.Failed)]
    [InlineData("", "", CheckStatus.Failed)]
    public async Task A_check_that_cannot_start_sends_nothing(string baseUrl, string key, CheckStatus expected)
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("no request expected"));

        Assert.Equal(expected, (await Provider(handler).CheckAsync(At(baseUrl, key), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_line_goes_out_as_a_chat_with_a_strict_schema_and_the_key_as_a_bearer()
    {
        HttpRequestMessage? sent = null;
        var handler = new FakeHandler((request, _) =>
        {
            sent = request;
            return FakeHandler.Json(Chat(Items));
        });
        var translator = Provider(handler).Create(Config("https://api.groq.com/openai/v1", "openai/gpt-oss-120b", "gsk-test"));

        var result = await translator.TranslateAsync(ProviderKit.Request("Cirl, get down here!"), CancellationToken.None);

        Assert.Equal(["Ciri, xuống đây!"], result.Texts);
        Assert.Equal("compat/openai/gpt-oss-120b", translator.Name);
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", sent!.RequestUri!.ToString());
        Assert.Equal("Bearer gsk-test", ProviderKit.Header(sent, "Authorization"));
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        var root = body.RootElement;
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Contains("game_profile", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("user", root.GetProperty("messages")[1].GetProperty("role").GetString());
        Assert.Equal("json_schema", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(root.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.False(root.TryGetProperty("provider", out _)); // OpenRouter only
        Assert.False(root.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task The_json_mode_steps_down_only_on_a_format_error_and_the_step_that_works_is_kept()
    {
        var handler = new FakeHandler((_, body) =>
            body.Contains("json_schema") ? ProviderKit.Answer(400, """{"error":{"message":"response_format type json_schema is not supported by this model","type":"invalid_request_error"}}""")
            : body.Contains("json_object") ? ProviderKit.Answer(422, """{"error":"json_object response format is not supported"}""")
            : FakeHandler.Json(Chat("Sure! ```json\n" + Items + "\n```")));
        var provider = Provider(handler);

        var first = await provider.Create(Config("http://localhost:11434/v1", "qwen3:8b")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);
        await provider.Create(Config("http://localhost:11434/v1", "qwen3:8b")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);
        await provider.Create(Config("http://localhost:1234/v1", "qwen3:8b")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);

        Assert.Equal(["Ciri, xuống đây!"], first.Texts);
        Assert.Equal(3 + 1 + 3, handler.Bodies.Count); // schema, object, prompt; then prompt at once; another server starts at the top
        Assert.DoesNotContain("response_format", handler.Bodies[3]);
        Assert.Contains("json_schema", handler.Bodies[4]);
    }

    [Fact]
    public async Task OpenRouter_s_no_endpoints_found_steps_the_json_mode_down_whether_it_comes_as_a_404_or_in_a_200()
    {
        const string noEndpoints = """{"error":{"message":"No endpoints found that can handle the requested parameters.","code":404}}""";
        var handler = new FakeHandler((_, body) =>
            body.Contains("json_schema") ? ProviderKit.Answer(404, noEndpoints)
            : body.Contains("json_object") ? FakeHandler.Json(noEndpoints)
            : FakeHandler.Json(Chat(Items)));
        var provider = Provider(handler);

        var result = await provider.Create(Config("https://openrouter.ai/api/v1", "some/model", "k")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);
        await provider.Create(Config("https://openrouter.ai/api/v1", "some/model", "k")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);

        Assert.Equal(["Ciri, xuống đây!"], result.Texts);
        Assert.Equal(3 + 1, handler.Bodies.Count); // schema, object, prompt; then the prompt at once
        Assert.DoesNotContain("response_format", handler.Bodies[2]);
        Assert.DoesNotContain("require_parameters", handler.Bodies[2]); // the prompt alone needs no endpoint that honours a format
    }

    [Theory]
    [InlineData("No endpoints found matching your data policy (Free model publication). Configure: https://openrouter.ai/settings/privacy")]
    [InlineData("No endpoints found for some/model.")]
    public async Task OpenRouter_s_other_no_endpoints_found_fails_at_once_and_keeps_the_json_mode(string message)
    {
        bool fixedPolicy = false;
        var handler = new FakeHandler((_, _) => fixedPolicy ? FakeHandler.Json(Chat(Items))
            : ProviderKit.Answer(404, $$$"""{"error":{"message":"{{{message}}}","code":404}}"""));
        var provider = Provider(handler);

        await Assert.ThrowsAsync<TranslatorUnavailableException>(() =>
            provider.Create(Config("https://openrouter.ai/api/v1", "some/model", "k")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));
        fixedPolicy = true; // the user changed the privacy setting
        await provider.Create(Config("https://openrouter.ai/api/v1", "some/model", "k")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);

        Assert.Equal(2, handler.Bodies.Count); // no step down: no other JSON mode changes the account's policy
        Assert.Contains("json_schema", handler.Bodies[1]);
        Assert.Contains("require_parameters", handler.Bodies[1]);
    }

    [Fact]
    public async Task A_404_that_names_no_format_fails_at_once()
    {
        var handler = new FakeHandler((_, _) => ProviderKit.Answer(404, """{"error":{"message":"The model `qwen9` does not exist.","type":"not_found"}}"""));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() =>
            Provider(handler).Create(Config("http://localhost:11434/v1", "qwen9")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Single(handler.Bodies);
        Assert.Equal("Ollama: HTTP 404 not_found: The model `qwen9` does not exist.", ex.Message);
    }

    [Fact]
    public void A_local_server_gets_time_for_a_cold_start_and_a_cloud_one_the_usual_deadline()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), CompatTranslator.LocalDeadline(1));
        Assert.Equal(ProviderHttp.Deadline(16), CompatTranslator.LocalDeadline(16)); // a long request already has longer
    }

    [Fact]
    public async Task A_stalled_local_server_is_given_up_after_its_deadline()
    {
        var translator = new CompatTranslator(Config("http://localhost:11434/v1", "qwen3:8b"), new HttpClient(new StalledHandler()),
            new ConcurrentDictionary<string, CompatJsonMode>(), _ => TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Contains("did not answer", ex.Message);
    }

    [Theory]
    [InlineData(1_790_000_000_000L)] // milliseconds, as some servers write it
    [InlineData(1_790_000_000L)]
    public async Task A_release_date_in_seconds_or_milliseconds_is_read(long created)
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json($$"""{"data":[{"id":"grok-old","created":1700000000},{"id":"grok-new","created":{{created}}}]}"""));

        var check = await Provider(handler).CheckAsync(At("https://api.x.ai/v1", "key"), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal(ModelTags.Recommended, check.Options.Single(o => o.Id == "grok-new").Tags);
    }

    [Fact]
    public async Task A_date_out_of_any_range_is_no_date_and_the_check_still_answers()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"data":[{"id":"grok-a","created":9223372036854775807},{"id":"grok-b","created":-62135596801}]}"""));

        var check = await Provider(handler).CheckAsync(At("https://api.x.ai/v1", "key"), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal(2, check.Options.Count);
        Assert.All(check.Options, o => Assert.Equal(ModelTags.None, o.Tags)); // nothing to tell the newest by
    }

    [Fact]
    public async Task A_schema_mismatch_is_tried_once_more_at_the_same_step()
    {
        int calls = 0;
        var handler = new FakeHandler((_, _) => ++calls == 1
            ? ProviderKit.Answer(400, """{"error":{"message":"Generated JSON does not match the expected schema. Please adjust your prompt.","type":"invalid_request_error"}}""")
            : FakeHandler.Json(Chat(Items)));

        var result = await Provider(handler).Create(Config("https://api.groq.com/openai/v1", "openai/gpt-oss-20b", "k")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);

        Assert.Equal(["Ciri, xuống đây!"], result.Texts);
        Assert.All(handler.Bodies, b => Assert.Contains("json_schema", b));
    }

    [Fact]
    public async Task An_empty_answer_is_asked_again_once()
    {
        int calls = 0;
        var handler = new FakeHandler((_, _) => FakeHandler.Json(Chat(++calls == 1 ? "" : Items)));

        var result = await Provider(handler).Create(Config("https://api.deepseek.com", "deepseek-flash", "k")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None);

        Assert.Equal(["Ciri, xuống đây!"], result.Texts);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task OpenRouter_asks_for_endpoints_that_honour_the_format_and_its_200_errors_are_errors()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"error":{"code":402,"message":"Insufficient credits"}}"""));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() =>
            Provider(handler).Create(Config("https://openrouter.ai/api/v1", "openai/gpt-6-luna", "k")).TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Equal("OpenRouter: HTTP 402: Insufficient credits", ex.Message);
        Assert.Equal(ProviderException.AccountProblemPause, ex.RetryAfter);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.True(body.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
    }

    [Theory]
    [InlineData("length", "cut off")]
    [InlineData("content_filter", "declined")]
    public async Task An_answer_cut_short_or_filtered_hands_the_line_on(string finish, string message)
    {
        var translator = Provider(new FakeHandler((_, _) => FakeHandler.Json(Chat("{\"items\":[", finish)))).Create(Config("https://api.x.ai/v1", "grok-5", "k"));

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public async Task A_server_that_is_not_running_while_translating_hands_the_line_on()
    {
        var translator = new CompatTranslator(Config("http://localhost:11434/v1", "qwen3:8b"), new HttpClient(new RefusedHandler()),
            new ConcurrentDictionary<string, CompatJsonMode>());

        var ex = await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(ProviderKit.Request("x"), CancellationToken.None));

        Assert.Equal("Ollama refused the connection: the server is not running.", ex.Message);
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1/", "openrouter")]
    [InlineData("http://127.0.0.1:11434/v1", "ollama")]
    [InlineData("http://localhost:1234/v1", "lmstudio")]
    [InlineData("https://api.deepseek.com", "deepseek")]
    [InlineData("http://localhost:8080/v1", null)]
    [InlineData("https://example.com/v1", null)]
    public void A_preset_is_known_by_its_server(string baseUrl, string? preset) => Assert.Equal(preset, CompatPresets.Of(baseUrl)?.Id);
}
