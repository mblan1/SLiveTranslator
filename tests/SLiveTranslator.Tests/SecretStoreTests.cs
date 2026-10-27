using System;
using SLiveTranslator.Models;
using Xunit;

namespace SLiveTranslator.Tests;

public class SecretStoreTests
{
    [Fact]
    public void Round_trips_a_key_without_storing_it_in_plain_text()
    {
        const string key = "sk-ant-api03-Test+Key/ü";

        string blob = SecretStore.Protect(key);

        Assert.DoesNotContain("sk-ant", blob);
        Assert.Equal(key, SecretStore.Unprotect(blob));
    }

    [Fact]
    public void Empty_key_is_stored_as_empty()
    {
        Assert.Equal("", SecretStore.Protect(""));
        Assert.Equal("", SecretStore.Unprotect(""));
        Assert.Equal("", SecretStore.Unprotect(null));
    }

    [Fact]
    public void Garbage_or_damaged_blobs_give_null()
    {
        var damaged = Convert.FromBase64String(SecretStore.Protect("sk-test"));
        damaged[damaged.Length / 2] ^= 0xFF;

        Assert.Null(SecretStore.Unprotect("not base64 !!"));
        Assert.Null(SecretStore.Unprotect(Convert.ToBase64String([1, 2, 3, 4, 5])));
        Assert.Null(SecretStore.Unprotect(Convert.ToBase64String(damaged)));
    }
}
