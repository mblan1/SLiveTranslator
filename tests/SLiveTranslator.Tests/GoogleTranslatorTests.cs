using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

/// <summary>Returns canned responses and records every request.</summary>
sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Bodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string body = request.Content is null ? request.RequestUri!.Query : await request.Content.ReadAsStringAsync(ct);
        Bodies.Add(body);
        var response = respond(request, body);
        response.RequestMessage = request;
        return response;
    }

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class GoogleTranslatorTests
{
    [Fact]
    public async Task Sends_all_strings_in_one_request_and_splits_the_answer()
    {
        // Shape of a real translate_a/single answer: sentence segments [translated, original, ...].
        var handler = new FakeHandler((_, _) => FakeHandler.Json(
            """[[["Bình thuốc hồi máu\n","Health Potion\n",null,null,3],["Con đường phía trước rất nguy hiểm.","The road ahead is dangerous.",null,null,3]],null,"en"]"""));
        var translator = new GoogleTranslator(new HttpClient(handler));

        var result = await translator.TranslateAsync(["Health Potion", "The road ahead is dangerous."], "en", "vi", CancellationToken.None);

        Assert.Single(handler.Bodies);
        Assert.Equal(["Bình thuốc hồi máu", "Con đường phía trước rất nguy hiểm."], result);
    }

    [Fact]
    public async Task Falls_back_to_one_request_per_string_when_lines_do_not_match()
    {
        var handler = new FakeHandler((_, body) => FakeHandler.Json(
            body.Contains("%0A") ? """[[["merged into one line","x",null,null,3]]]""" : """[[["single","x",null,null,3]]]"""));
        var translator = new GoogleTranslator(new HttpClient(handler));

        var result = await translator.TranslateAsync(["a", "b"], "en", "vi", CancellationToken.None);

        Assert.Equal(3, handler.Bodies.Count);
        Assert.Equal(["single", "single"], result);
    }

    [Fact]
    public async Task Redirect_to_the_captcha_page_counts_as_blocked()
    {
        var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://www.google.com/sorry/index?continue=x");
            return response;
        });
        var translator = new GoogleTranslator(new HttpClient(handler));

        await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(["a"], "en", "vi", CancellationToken.None));
    }

    [Fact]
    public async Task Rate_limit_pauses_the_service_without_more_requests()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var translator = new GoogleTranslator(new HttpClient(handler));

        await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(["a"], "en", "vi", CancellationToken.None));
        await Assert.ThrowsAsync<TranslatorUnavailableException>(() => translator.TranslateAsync(["b"], "en", "vi", CancellationToken.None));

        Assert.Single(handler.Bodies);
    }
}
