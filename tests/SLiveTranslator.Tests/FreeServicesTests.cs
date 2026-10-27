using System;
using System.Net;
using SLiveTranslator.Models;
using SLiveTranslator.Translation;
using Xunit;

namespace SLiveTranslator.Tests;

public class FreeServicesTests
{
    // The free services moved from FreeServices into ProviderRegistry, which builds every chain.

    [Fact]
    public void Google_and_myMemory_live_as_long_as_the_app()
    {
        // A settings save builds the chain again; Google must still know that this network is blocked.
        var free = new ProviderRegistry();
        var google = free.Google;
        var myMemory = free.MyMemory("");

        free.Build(new TranslationSettings());
        free.Build(new TranslationSettings { Free = { MyMemoryEmail = " " } });

        Assert.Same(google, free.Google);
        Assert.Same(myMemory, free.MyMemory(""));
    }

    [Fact]
    public void A_new_email_gets_a_new_myMemory_with_its_own_quota()
    {
        var free = new ProviderRegistry();
        var anonymous = free.MyMemory("");

        var withEmail = free.MyMemory("player@example.com");

        Assert.NotSame(anonymous, withEmail);
        Assert.Same(withEmail, free.MyMemory("player@example.com"));
    }

    [Fact]
    public void Every_service_shares_one_connection_pool_that_keeps_idle_connections_for_ten_minutes()
    {
        var handler = HttpClients.Handler;

        Assert.Equal(TimeSpan.FromMinutes(10), handler.PooledConnectionIdleTimeout);
        Assert.False(handler.AllowAutoRedirect); // Google's 302 to its captcha page must reach GoogleTranslator
        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);

        using var a = HttpClients.Create(TimeSpan.FromSeconds(15), "SLiveTranslator/1.0");
        using var b = HttpClients.Create(TimeSpan.FromSeconds(20), "SLiveTranslator/1.0");
        Assert.Equal(TimeSpan.FromSeconds(15), a.Timeout);
        Assert.Equal("SLiveTranslator/1.0", b.DefaultRequestHeaders.UserAgent.ToString());
    }
}
